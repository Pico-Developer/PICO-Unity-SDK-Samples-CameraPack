namespace PICO.Samples.HandJointsOverlay
{
    public static class CameraFrameTiming
    {
        private const double k_nanosecondsToMilliseconds = 1e-6d;

        public static bool TryConvertXrTimeToMilliseconds(
            double captureTimeNanoseconds,
            out double captureTimeMilliseconds)
        {
            captureTimeMilliseconds = 0d;
            if (double.IsNaN(captureTimeNanoseconds) ||
                double.IsInfinity(captureTimeNanoseconds) ||
                captureTimeNanoseconds <= 0d)
            {
                return false;
            }

            captureTimeMilliseconds = captureTimeNanoseconds * k_nanosecondsToMilliseconds;
            return true;
        }
    }
}
