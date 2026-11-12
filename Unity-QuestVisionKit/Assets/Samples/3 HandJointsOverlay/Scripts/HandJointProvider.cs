using UnityEngine;
#if ENABLE_PICO_XR_SDK
using ByteDance.PICO.XR;
#endif

namespace PICO.Samples.HandJointsOverlay
{
    /// <summary>
    /// Reads the 26 hand joint world-space positions for left and right hands
    /// from the PICO hand tracking system each frame and exposes them for consumers.
    /// </summary>
    public class HandJointProvider : MonoBehaviour
    {
        // Number of joints reported by the PICO hand tracker (HandJoint.JointMax).
        public const int k_jointCount = 26;
        private const int k_historyCapacity = 64;

        private readonly Vector3[] m_leftPositions = new Vector3[k_jointCount];
        private readonly Vector3[] m_rightPositions = new Vector3[k_jointCount];
        private readonly bool[] m_leftValid = new bool[k_jointCount];
        private readonly bool[] m_rightValid = new bool[k_jointCount];
        private readonly TimedHandJointHistory m_leftHistory =
            new TimedHandJointHistory(k_jointCount, k_historyCapacity);
        private readonly TimedHandJointHistory m_rightHistory =
            new TimedHandJointHistory(k_jointCount, k_historyCapacity);

        private bool m_leftHandTracked;
        private bool m_rightHandTracked;

        #region debug-point A-C-E:provider-state
        public double DebugLatestTimestampMilliseconds { get; private set; }
        #endregion

#if ENABLE_PICO_XR_SDK
        private HandJointLocations m_leftLocations = new HandJointLocations();
        private HandJointLocations m_rightLocations = new HandJointLocations();
#endif

        public bool IsLeftHandTracked => m_leftHandTracked;
        public bool IsRightHandTracked => m_rightHandTracked;

        private void Update()
        {
            bool hasTimestamp = TryGetPredictedDisplayTime(out double timestampMilliseconds);
            m_leftHandTracked = UpdateHand(true, m_leftPositions, m_leftValid);
            m_rightHandTracked = UpdateHand(false, m_rightPositions, m_rightValid);

            if (!hasTimestamp)
            {
                ResetHistory();
                return;
            }

            m_leftHistory.Push(
                timestampMilliseconds,
                m_leftPositions,
                m_leftValid,
                m_leftHandTracked);
            m_rightHistory.Push(
                timestampMilliseconds,
                m_rightPositions,
                m_rightValid,
                m_rightHandTracked);

            #region debug-point A:raw-hand-snapshot
            DebugLatestTimestampMilliseconds = timestampMilliseconds;
            #endregion
        }

        /// <summary>
        /// Copies the latest joint world positions and validity flags for the requested hand.
        /// Returns false (and marks everything invalid) when the hand is not tracked.
        /// </summary>
        public bool TryGetJoints(bool isLeftHand, Vector3[] worldPositions, bool[] valid)
        {
            if (worldPositions == null || valid == null ||
                worldPositions.Length < k_jointCount || valid.Length < k_jointCount)
            {
                return false;
            }

            Vector3[] sourcePositions = isLeftHand ? m_leftPositions : m_rightPositions;
            bool[] sourceValid = isLeftHand ? m_leftValid : m_rightValid;
            bool tracked = isLeftHand ? m_leftHandTracked : m_rightHandTracked;

            for (int i = 0; i < k_jointCount; i++)
            {
                worldPositions[i] = sourcePositions[i];
                valid[i] = tracked && sourceValid[i];
            }

            return tracked;
        }

        public HandHistorySampleResult SampleJointsAt(
            bool isLeftHand,
            double timestampMilliseconds,
            Vector3[] worldPositions,
            bool[] valid)
        {
            TimedHandJointHistory history = isLeftHand
                ? m_leftHistory
                : m_rightHistory;
            return history.Sample(timestampMilliseconds, worldPositions, valid);
        }

        public void ResetHistory()
        {
            m_leftHistory.Reset();
            m_rightHistory.Reset();
        }

        private void OnDisable()
        {
            ResetHistory();
        }

        private bool UpdateHand(bool isLeftHand, Vector3[] positions, bool[] valid)
        {
#if ENABLE_PICO_XR_SDK
            HandType handType = isLeftHand ? HandType.HandLeft : HandType.HandRight;
            ref HandJointLocations locations = ref (isLeftHand ? ref m_leftLocations : ref m_rightLocations);

            // GetJointLocations returns false when hand tracking is disabled in project settings
            // or the data is unavailable. Treat both as "not tracked".
            if (!PXR_HandTracking.GetJointLocations(handType, ref locations) || locations.isActive == 0U)
            {
                ClearValidity(valid);
                return false;
            }

            int count = Mathf.Min((int)locations.jointCount, k_jointCount);
            for (int i = 0; i < k_jointCount; i++)
            {
                if (i < count && locations.jointLocations != null)
                {
                    HandJointLocation joint = locations.jointLocations[i];

                    // ToVector3() already flips Z into Unity tracking space, matching the
                    // coordinate system used by PXR_CamTextureManager.WorldToViewportPoint.
                    positions[i] = joint.pose.Position.ToVector3();
                    valid[i] = (joint.locationStatus & HandLocationStatus.PositionValid) != 0;
                }
                else
                {
                    valid[i] = false;
                }
            }

            return true;
#else
            ClearValidity(valid);
            return false;
#endif
        }

        private static bool TryGetPredictedDisplayTime(out double timestampMilliseconds)
        {
            timestampMilliseconds = 0d;
#if ENABLE_PICO_XR_SDK && UNITY_ANDROID && !UNITY_EDITOR
            int result = PXR_Plugin.Pxr_GetPredictedDisplayTime(ref timestampMilliseconds);
            return result == 0 &&
                   !double.IsNaN(timestampMilliseconds) &&
                   !double.IsInfinity(timestampMilliseconds) &&
                   timestampMilliseconds > 0d;
#else
            return false;
#endif
        }

        private static void ClearValidity(bool[] valid)
        {
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = false;
            }
        }
    }
}
