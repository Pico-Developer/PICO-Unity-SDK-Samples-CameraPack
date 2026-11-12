namespace ByteDance.PICO.EnvironmentDepthExtension
{
    /// <summary>
    /// Result classification for an environment depth raycast. Names are kept
    /// identical to Meta's EnvironmentRaycastHitStatus so that callers migrating
    /// between backends do not need to remap states.
    /// </summary>
    public enum EnvironmentRaycastHitStatus
    {
        /// <summary>The ray hit a directly observed surface.</summary>
        Hit,

        /// <summary>
        /// The ray reached a surface, but the hit point sits behind a depth
        /// discontinuity (the observed surface is occluded from this eye).
        /// </summary>
        HitPointOccluded,

        /// <summary>No depth frame is available yet.</summary>
        NotReady,

        /// <summary>The traversed texel left the camera frustum before a hit.</summary>
        HitPointOutsideOfCameraFrustum,

        /// <summary>The ray origin was occluded for the whole traversal.</summary>
        RayOccluded,

        /// <summary>The ray traversed free space without intersecting a surface.</summary>
        NoHit,

        /// <summary>Environment depth is not supported on this device/backend.</summary>
        NotSupported,
    }
}
