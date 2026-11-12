using UnityEngine;

namespace PICO.Samples.HandJointsOverlay
{
    /// <summary>
    /// Temporal Z-score outlier rejection plus smoothing for hand joint world positions.
    ///
    /// Adapted from HoloLab QuestCameraTools' ZScoreFilterComponent (which smooths a single QR
    /// pose). Reworked to filter every joint independently and, crucially, to be allocation-free:
    /// the original List + LINQ implementation churns the managed heap every frame, which this
    /// project forbids. Each joint keeps a fixed-size ring buffer of recent samples; per frame we
    /// compute the window mean/variance, discard samples whose squared Z-score exceeds the
    /// threshold (sudden tracking pops), and aggregate the survivors.
    /// </summary>
    public sealed class ZScoreJointFilter
    {
        public enum AggregationType
        {
            // Mean of the surviving inliers: maximum smoothing, adds latency on fast motion.
            Average = 0,
            // Newest surviving inlier: rejects pops with minimal latency, less smoothing.
            Latest
        }

        private readonly int m_jointCount;
        private readonly int m_windowSize;

        // Flattened ring buffers: sample for joint j, slot s lives at [j * windowSize + s].
        private readonly Vector3[] m_buffer;
        private readonly int[] m_count;
        private readonly int[] m_head;

        public ZScoreJointFilter(int jointCount, int windowSize)
        {
            m_jointCount = Mathf.Max(1, jointCount);
            m_windowSize = Mathf.Max(1, windowSize);
            m_buffer = new Vector3[m_jointCount * m_windowSize];
            m_count = new int[m_jointCount];
            m_head = new int[m_jointCount];
        }

        /// <summary>
        /// Drops all buffered history. Call when tracking is lost for the whole hand.
        /// </summary>
        public void Reset()
        {
            for (int j = 0; j < m_jointCount; j++)
            {
                m_count[j] = 0;
                m_head[j] = 0;
            }
        }

        /// <summary>
        /// Smooths the joint positions in place. Invalid joints have their history cleared and
        /// their position left untouched, so a re-acquired joint does not blend stale samples.
        /// </summary>
        public void Filter(Vector3[] positions, bool[] valid, float zScoreThreshold, AggregationType aggregation)
        {
            if (positions == null || valid == null)
            {
                return;
            }

            float thresholdSqr = zScoreThreshold * zScoreThreshold;
            int count = Mathf.Min(m_jointCount, Mathf.Min(positions.Length, valid.Length));

            for (int j = 0; j < count; j++)
            {
                if (!valid[j])
                {
                    m_count[j] = 0;
                    m_head[j] = 0;
                    continue;
                }

                positions[j] = FilterJoint(j, positions[j], thresholdSqr, aggregation);
            }
        }

        private Vector3 FilterJoint(int joint, Vector3 sample, float thresholdSqr, AggregationType aggregation)
        {
            int baseIndex = joint * m_windowSize;

            // Write the new sample, remembering its slot so "Latest" can scan newest -> oldest.
            int newest = m_head[joint];
            m_buffer[baseIndex + newest] = sample;
            m_head[joint] = (newest + 1) % m_windowSize;
            if (m_count[joint] < m_windowSize)
            {
                m_count[joint]++;
            }

            int n = m_count[joint];
            if (n == 1)
            {
                return sample;
            }

            // Valid samples always occupy slots [0, n): the ring starts at slot 0 and only wraps
            // once full, so order is irrelevant for these mean/variance/average accumulations.
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                mean += m_buffer[baseIndex + i];
            }
            mean /= n;

            float varianceSum = 0f;
            for (int i = 0; i < n; i++)
            {
                varianceSum += (m_buffer[baseIndex + i] - mean).sqrMagnitude;
            }
            float variance = varianceSum / n;

            // Degenerate spread (joint held still): no outlier is definable, keep the live sample.
            if (variance <= Mathf.Epsilon)
            {
                return sample;
            }

            if (aggregation == AggregationType.Latest)
            {
                for (int k = 0; k < n; k++)
                {
                    int index = newest - k;
                    if (index < 0)
                    {
                        index += m_windowSize;
                    }

                    Vector3 candidate = m_buffer[baseIndex + index];
                    if ((candidate - mean).sqrMagnitude / variance < thresholdSqr)
                    {
                        return candidate;
                    }
                }

                return sample;
            }

            Vector3 inlierSum = Vector3.zero;
            int inliers = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 candidate = m_buffer[baseIndex + i];
                if ((candidate - mean).sqrMagnitude / variance < thresholdSqr)
                {
                    inlierSum += candidate;
                    inliers++;
                }
            }

            return inliers > 0 ? inlierSum / inliers : sample;
        }
    }
}
