namespace PICO.Samples.HandJointsOverlay
{
    public enum CameraLifecycleTransition
    {
        None,
        Started,
        Stopped
    }

    public enum CameraFrameDecision
    {
        NoNewTexture,
        Ready,
        LifecycleResetRequired
    }

    public sealed class CameraFrameGate
    {
        private bool m_hasPlayingObservation;
        private bool m_isPlaying;

        private bool m_isArmed;
        private uint m_baselineTextureUpdateCount;
        private bool m_hasBaselineCaptureTime;
        private double m_baselineCaptureTimeNanoseconds;
        private bool m_hasObservedTextureAdvance;

        private bool m_hasCommitted;
        private uint m_committedTextureUpdateCount;
        private double m_committedCaptureTimeNanoseconds;

        private bool m_hasPending;
        private uint m_pendingTextureUpdateCount;
        private double m_pendingCaptureTimeNanoseconds;
        private bool m_pendingUsesTimestampIdentity;

        public CameraLifecycleTransition ObservePlayingState(bool isPlaying)
        {
            if (!m_hasPlayingObservation)
            {
                m_hasPlayingObservation = true;
                m_isPlaying = isPlaying;
                ClearFrameState();
                return isPlaying
                    ? CameraLifecycleTransition.Started
                    : CameraLifecycleTransition.None;
            }

            if (m_isPlaying == isPlaying)
            {
                return CameraLifecycleTransition.None;
            }

            m_isPlaying = isPlaying;
            ClearFrameState();
            return isPlaying
                ? CameraLifecycleTransition.Started
                : CameraLifecycleTransition.Stopped;
        }

        public void ArmBaseline(uint textureUpdateCount)
        {
            ClearFrameState();
            m_isArmed = true;
            m_baselineTextureUpdateCount = textureUpdateCount;
        }

        public CameraFrameDecision Evaluate(
            uint textureUpdateCount,
            double observedCaptureTimeNanoseconds,
            out double latchedCaptureTimeNanoseconds)
        {
            latchedCaptureTimeNanoseconds = 0d;
            if (!m_isArmed)
            {
                return CameraFrameDecision.NoNewTexture;
            }

            if (m_hasPending)
            {
                if (textureUpdateCount == m_pendingTextureUpdateCount)
                {
                    if (m_pendingUsesTimestampIdentity &&
                        IsValidTimestamp(observedCaptureTimeNanoseconds))
                    {
                        if (observedCaptureTimeNanoseconds <
                            m_pendingCaptureTimeNanoseconds)
                        {
                            return CameraFrameDecision.LifecycleResetRequired;
                        }
                        if (observedCaptureTimeNanoseconds >
                            m_pendingCaptureTimeNanoseconds)
                        {
                            LatchPending(
                                textureUpdateCount,
                                observedCaptureTimeNanoseconds,
                                true);
                        }
                    }

                    latchedCaptureTimeNanoseconds = m_pendingCaptureTimeNanoseconds;
                    return CameraFrameDecision.Ready;
                }

                if (textureUpdateCount < m_pendingTextureUpdateCount ||
                    IsTimestampRegression(
                        observedCaptureTimeNanoseconds,
                        m_pendingCaptureTimeNanoseconds))
                {
                    return CameraFrameDecision.LifecycleResetRequired;
                }

                m_hasObservedTextureAdvance = true;
                LatchPending(
                    textureUpdateCount,
                    observedCaptureTimeNanoseconds,
                    false);
                latchedCaptureTimeNanoseconds = m_pendingCaptureTimeNanoseconds;
                return CameraFrameDecision.Ready;
            }

            uint referenceTextureUpdateCount = m_hasCommitted
                ? m_committedTextureUpdateCount
                : m_baselineTextureUpdateCount;
            if (textureUpdateCount == referenceTextureUpdateCount)
            {
                if (m_hasObservedTextureAdvance ||
                    !IsValidTimestamp(observedCaptureTimeNanoseconds))
                {
                    return CameraFrameDecision.NoNewTexture;
                }

                double referenceCaptureTime = m_hasCommitted
                    ? m_committedCaptureTimeNanoseconds
                    : m_baselineCaptureTimeNanoseconds;
                bool hasReferenceCaptureTime = m_hasCommitted
                    ? IsValidTimestamp(m_committedCaptureTimeNanoseconds)
                    : m_hasBaselineCaptureTime;
                if (!hasReferenceCaptureTime)
                {
                    m_hasBaselineCaptureTime = true;
                    m_baselineCaptureTimeNanoseconds =
                        observedCaptureTimeNanoseconds;
                    return CameraFrameDecision.NoNewTexture;
                }
                if (observedCaptureTimeNanoseconds < referenceCaptureTime)
                {
                    return CameraFrameDecision.LifecycleResetRequired;
                }
                if (observedCaptureTimeNanoseconds == referenceCaptureTime)
                {
                    return CameraFrameDecision.NoNewTexture;
                }

                LatchPending(
                    textureUpdateCount,
                    observedCaptureTimeNanoseconds,
                    true);
                latchedCaptureTimeNanoseconds = m_pendingCaptureTimeNanoseconds;
                return CameraFrameDecision.Ready;
            }

            if (textureUpdateCount < referenceTextureUpdateCount ||
                (m_hasCommitted &&
                 IsTimestampRegression(
                     observedCaptureTimeNanoseconds,
                     m_committedCaptureTimeNanoseconds)))
            {
                return CameraFrameDecision.LifecycleResetRequired;
            }

            m_hasObservedTextureAdvance = true;
            LatchPending(
                textureUpdateCount,
                observedCaptureTimeNanoseconds,
                false);
            latchedCaptureTimeNanoseconds = m_pendingCaptureTimeNanoseconds;
            return CameraFrameDecision.Ready;
        }

        public void Commit()
        {
            if (!m_hasPending)
            {
                return;
            }

            m_hasCommitted = true;
            m_committedTextureUpdateCount = m_pendingTextureUpdateCount;
            m_committedCaptureTimeNanoseconds = m_pendingCaptureTimeNanoseconds;
            m_baselineTextureUpdateCount = m_pendingTextureUpdateCount;
            m_hasBaselineCaptureTime =
                IsValidTimestamp(m_pendingCaptureTimeNanoseconds);
            m_baselineCaptureTimeNanoseconds =
                m_pendingCaptureTimeNanoseconds;
            m_hasPending = false;
            m_pendingTextureUpdateCount = 0U;
            m_pendingCaptureTimeNanoseconds = 0d;
            m_pendingUsesTimestampIdentity = false;
        }

        public void Reset()
        {
            m_hasPlayingObservation = false;
            m_isPlaying = false;
            ClearFrameState();
        }

        private void LatchPending(
            uint textureUpdateCount,
            double captureTimeNanoseconds,
            bool usesTimestampIdentity)
        {
            m_hasPending = true;
            m_pendingTextureUpdateCount = textureUpdateCount;
            m_pendingCaptureTimeNanoseconds = captureTimeNanoseconds;
            m_pendingUsesTimestampIdentity = usesTimestampIdentity;
        }

        private void ClearFrameState()
        {
            m_isArmed = false;
            m_baselineTextureUpdateCount = 0U;
            m_hasBaselineCaptureTime = false;
            m_baselineCaptureTimeNanoseconds = 0d;
            m_hasObservedTextureAdvance = false;
            m_hasCommitted = false;
            m_committedTextureUpdateCount = 0U;
            m_committedCaptureTimeNanoseconds = 0d;
            m_hasPending = false;
            m_pendingTextureUpdateCount = 0U;
            m_pendingCaptureTimeNanoseconds = 0d;
            m_pendingUsesTimestampIdentity = false;
        }

        private static bool IsTimestampRegression(double current, double previous)
        {
            return IsValidTimestamp(current) &&
                   IsValidTimestamp(previous) &&
                   current < previous;
        }

        private static bool IsValidTimestamp(double timestamp)
        {
            return !double.IsNaN(timestamp) &&
                   !double.IsInfinity(timestamp) &&
                   timestamp > 0d;
        }
    }
}
