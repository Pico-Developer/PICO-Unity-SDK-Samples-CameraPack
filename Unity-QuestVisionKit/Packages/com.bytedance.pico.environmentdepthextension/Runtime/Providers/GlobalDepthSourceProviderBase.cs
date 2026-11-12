using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ByteDance.PICO.EnvironmentDepthExtension.Providers
{
    /// <summary>
    /// Shared base for global-property depth backends. The depth manager
    /// publishes the depth texture, zBufferParams and reprojection matrices as
    /// global shader properties; this base reads them so backends only override
    /// availability and the manager lifecycle.
    /// </summary>
    public abstract class GlobalDepthSourceProviderBase : IDepthSourceProvider
    {
        private const int NumEyes = 2;

        private static readonly int EnvironmentDepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int EnvironmentDepthZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private static readonly int EnvironmentDepthReprojId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");

        public abstract string Name { get; }
        public abstract bool IsAvailable { get; }
        public abstract bool TryEnsureManager();

        public bool TryGetGlobals(out DepthSourceGlobals globals)
        {
            globals = default;

            var depthTexture = Shader.GetGlobalTexture(EnvironmentDepthTextureId) as RenderTexture;
            if (!depthTexture || !depthTexture.IsCreated())
            {
                return false;
            }

            if (depthTexture.dimension != TextureDimension.Tex2DArray || depthTexture.volumeDepth < NumEyes)
            {
                return false;
            }

            var reproj = Shader.GetGlobalMatrixArray(EnvironmentDepthReprojId);
            if (reproj == null || reproj.Length < NumEyes)
            {
                return false;
            }

            globals.DepthTexture = depthTexture;
            globals.ZBufferParams = Shader.GetGlobalVector(EnvironmentDepthZBufferParamsId);
            globals.ReprojLeft = reproj[0];
            globals.ReprojRight = reproj[1];
            return true;
        }

        /// <summary>
        /// Releases backend-owned resources. The base owns nothing, so this is a
        /// no-op; backends that create a depth-manager component override this to
        /// destroy it.
        /// </summary>
        public virtual void Dispose()
        {
        }
    }
}
