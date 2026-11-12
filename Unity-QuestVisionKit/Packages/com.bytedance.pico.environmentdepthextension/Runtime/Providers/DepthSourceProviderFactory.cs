using UnityEngine;

namespace ByteDance.PICO.EnvironmentDepthExtension.Providers
{
    public enum PreferredProvider
    {
        Auto,
        Pico,
    }

    /// <summary>
    /// Resolves the active depth provider. The supported backend is
    /// <see cref="PicoDepthSourceProvider"/>.
    /// </summary>
    public static class DepthSourceProviderFactory
    {
        public static IDepthSourceProvider Resolve(MonoBehaviour host, PreferredProvider preferred)
        {
            // Only a single XR backend is supported; both Auto and the
            // platform-specific variant resolve to the same provider.
            return new PicoDepthSourceProvider(host);
        }
    }
}
