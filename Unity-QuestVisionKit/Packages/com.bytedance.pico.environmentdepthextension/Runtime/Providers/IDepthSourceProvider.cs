using System;
using UnityEngine;

namespace ByteDance.PICO.EnvironmentDepthExtension.Providers
{
    /// <summary>
    /// Per-frame depth source globals captured from the backend. The depth
    /// manager publishes these as global shader properties; the provider reads
    /// them so the consumer never has to know the backend property names.
    /// </summary>
    public struct DepthSourceGlobals
    {
        /// <summary>The Tex2DArray depth texture (one slice per eye).</summary>
        public RenderTexture DepthTexture;

        /// <summary>zBufferParams used to convert NDC depth to linear meters.</summary>
        public Vector4 ZBufferParams;

        /// <summary>Per-eye view-projection (reprojection) matrices.</summary>
        public Matrix4x4 ReprojLeft;
        public Matrix4x4 ReprojRight;
    }

    /// <summary>
    /// Access to PICO environment depth. The provider owns both availability
    /// detection / depth-manager lifecycle and the per-frame data read, so the
    /// backend property names stay confined to the provider implementation.
    /// </summary>
    public interface IDepthSourceProvider : IDisposable
    {
        /// <summary>Human readable backend name (e.g. "Pico").</summary>
        string Name { get; }

        /// <summary>True when the backend SDK is present and depth is being produced.</summary>
        bool IsAvailable { get; }

        /// <summary>Ensures a depth-manager component exists; adds one if necessary.</summary>
        bool TryEnsureManager();

        /// <summary>
        /// Reads the current depth source globals. Returns false when the depth
        /// texture or matrices are not yet published.
        /// </summary>
        bool TryGetGlobals(out DepthSourceGlobals globals);
    }
}
