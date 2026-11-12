using System;
using UnityEngine;

namespace PICO.Samples.HandJointsOverlay
{
    public enum HandHistorySampleResult
    {
        Success,
        InvalidArguments,
        Empty,
        TargetBeforeOldest,
        TargetAfterNewest,
        TrackingUnavailable
    }

    public sealed class TimedHandJointHistory
    {
        private readonly int m_jointCount;
        private readonly int m_capacity;
        private readonly double[] m_timestamps;
        private readonly Vector3[] m_positions;
        private readonly bool[] m_valid;
        private readonly bool[] m_tracked;

        private int m_nextWrite;
        private int m_count;

        public TimedHandJointHistory(int jointCount, int capacity)
        {
            if (jointCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jointCount));
            }
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            m_jointCount = jointCount;
            m_capacity = capacity;
            m_timestamps = new double[capacity];
            m_positions = new Vector3[jointCount * capacity];
            m_valid = new bool[jointCount * capacity];
            m_tracked = new bool[capacity];
        }

        public void Reset()
        {
            m_nextWrite = 0;
            m_count = 0;
        }

        public void Push(
            double timestampMilliseconds,
            Vector3[] positions,
            bool[] valid,
            bool tracked)
        {
            if (!IsValidTimestamp(timestampMilliseconds) ||
                positions == null ||
                valid == null ||
                positions.Length < m_jointCount ||
                valid.Length < m_jointCount)
            {
                return;
            }

            if (m_count > 0)
            {
                int newest = GetPhysicalIndex(m_count - 1);
                double newestTimestamp = m_timestamps[newest];
                if (timestampMilliseconds < newestTimestamp)
                {
                    Reset();
                }
                else if (timestampMilliseconds == newestTimestamp)
                {
                    WriteSnapshot(newest, timestampMilliseconds, positions, valid, tracked);
                    return;
                }
            }

            int snapshotIndex = m_nextWrite;
            WriteSnapshot(snapshotIndex, timestampMilliseconds, positions, valid, tracked);
            m_nextWrite = (m_nextWrite + 1) % m_capacity;
            if (m_count < m_capacity)
            {
                m_count++;
            }
        }

        public HandHistorySampleResult Sample(
            double targetTimestampMilliseconds,
            Vector3[] outputPositions,
            bool[] outputValid)
        {
            ClearValidity(outputValid);
            if (!IsValidTimestamp(targetTimestampMilliseconds) ||
                outputPositions == null ||
                outputValid == null ||
                outputPositions.Length < m_jointCount ||
                outputValid.Length < m_jointCount)
            {
                return HandHistorySampleResult.InvalidArguments;
            }

            if (m_count == 0)
            {
                return HandHistorySampleResult.Empty;
            }

            int oldest = GetPhysicalIndex(0);
            int newest = GetPhysicalIndex(m_count - 1);
            if (targetTimestampMilliseconds < m_timestamps[oldest])
            {
                return HandHistorySampleResult.TargetBeforeOldest;
            }
            if (targetTimestampMilliseconds > m_timestamps[newest])
            {
                return HandHistorySampleResult.TargetAfterNewest;
            }

            for (int logicalIndex = 0; logicalIndex < m_count; logicalIndex++)
            {
                int current = GetPhysicalIndex(logicalIndex);
                double currentTimestamp = m_timestamps[current];
                if (targetTimestampMilliseconds == currentTimestamp)
                {
                    return CopyExactSnapshot(current, outputPositions, outputValid);
                }

                if (targetTimestampMilliseconds < currentTimestamp)
                {
                    int previous = GetPhysicalIndex(logicalIndex - 1);
                    return InterpolateSnapshots(
                        previous,
                        current,
                        targetTimestampMilliseconds,
                        outputPositions,
                        outputValid);
                }
            }

            return HandHistorySampleResult.TargetAfterNewest;
        }

        private HandHistorySampleResult CopyExactSnapshot(
            int snapshotIndex,
            Vector3[] outputPositions,
            bool[] outputValid)
        {
            if (!m_tracked[snapshotIndex])
            {
                return HandHistorySampleResult.TrackingUnavailable;
            }

            int baseIndex = snapshotIndex * m_jointCount;
            for (int joint = 0; joint < m_jointCount; joint++)
            {
                outputPositions[joint] = m_positions[baseIndex + joint];
                outputValid[joint] = m_valid[baseIndex + joint];
            }
            return HandHistorySampleResult.Success;
        }

        private HandHistorySampleResult InterpolateSnapshots(
            int olderSnapshot,
            int newerSnapshot,
            double targetTimestampMilliseconds,
            Vector3[] outputPositions,
            bool[] outputValid)
        {
            if (!m_tracked[olderSnapshot] || !m_tracked[newerSnapshot])
            {
                return HandHistorySampleResult.TrackingUnavailable;
            }

            double olderTimestamp = m_timestamps[olderSnapshot];
            double newerTimestamp = m_timestamps[newerSnapshot];
            float interpolation = (float)(
                (targetTimestampMilliseconds - olderTimestamp) /
                (newerTimestamp - olderTimestamp));

            int olderBase = olderSnapshot * m_jointCount;
            int newerBase = newerSnapshot * m_jointCount;
            for (int joint = 0; joint < m_jointCount; joint++)
            {
                bool isValid =
                    m_valid[olderBase + joint] &&
                    m_valid[newerBase + joint];
                outputValid[joint] = isValid;
                outputPositions[joint] = isValid
                    ? Vector3.LerpUnclamped(
                        m_positions[olderBase + joint],
                        m_positions[newerBase + joint],
                        interpolation)
                    : default;
            }

            return HandHistorySampleResult.Success;
        }

        private void WriteSnapshot(
            int snapshotIndex,
            double timestampMilliseconds,
            Vector3[] positions,
            bool[] valid,
            bool tracked)
        {
            m_timestamps[snapshotIndex] = timestampMilliseconds;
            m_tracked[snapshotIndex] = tracked;

            int baseIndex = snapshotIndex * m_jointCount;
            for (int joint = 0; joint < m_jointCount; joint++)
            {
                m_positions[baseIndex + joint] = positions[joint];
                m_valid[baseIndex + joint] = tracked && valid[joint];
            }
        }

        private int GetPhysicalIndex(int logicalIndex)
        {
            int oldest = m_nextWrite - m_count;
            if (oldest < 0)
            {
                oldest += m_capacity;
            }
            return (oldest + logicalIndex) % m_capacity;
        }

        private static void ClearValidity(bool[] valid)
        {
            if (valid == null)
            {
                return;
            }

            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = false;
            }
        }

        private static bool IsValidTimestamp(double timestamp)
        {
            return !double.IsNaN(timestamp) &&
                   !double.IsInfinity(timestamp) &&
                   timestamp > 0d;
        }
    }
}
