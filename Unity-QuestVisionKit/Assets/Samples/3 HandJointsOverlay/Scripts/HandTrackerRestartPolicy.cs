namespace PICO.Samples.HandJointsOverlay
{
    public static class HandTrackerRestartPolicy
    {
        public static bool ShouldRestart(
            CameraLifecycleTransition transition)
        {
            return transition == CameraLifecycleTransition.Started;
        }
    }
}
