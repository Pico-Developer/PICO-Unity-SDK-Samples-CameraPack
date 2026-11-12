using UnityEngine;

namespace ByteDance.PICO.EnvironmentDepthExtension
{
    /// <summary>
    /// Value-type result of an environment depth raycast. Field order matches
    /// design.md §D8.
    /// </summary>
    public struct EnvironmentRaycastHit
    {
        /// <summary>World-space hit point.</summary>
        public Vector3 Point;

        /// <summary>Reconstructed surface normal at the hit point (unit length).</summary>
        public Vector3 Normal;

        /// <summary>Confidence of the reconstructed normal in [0,1].</summary>
        public float NormalConfidence;

        /// <summary>Distance from the ray origin to <see cref="Point"/>.</summary>
        public float Distance;

        /// <summary>Classification of the raycast result.</summary>
        public EnvironmentRaycastHitStatus Status;
    }
}
