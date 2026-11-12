using UnityEngine;

#if ENABLE_PICO_XR_SDK
using ByteDance.PICO.XR;
#endif

namespace ByteDance.PICO.EnvironmentDepthExtension.Providers
{
    /// <summary>
    /// XR backend. All <c>#if ENABLE_PICO_XR_SDK</c> usage of
    /// PXR_EnvironmentDepthManager is confined to this file.
    /// </summary>
    public sealed class PicoDepthSourceProvider : GlobalDepthSourceProviderBase
    {
        private readonly MonoBehaviour m_host;

#if ENABLE_PICO_XR_SDK
        private PXR_EnvironmentDepthManager m_manager;
        // True only when this provider created the manager via AddComponent, so
        // Dispose never destroys a manager the user authored in the scene.
        private bool m_ownsManager;
#endif

        public PicoDepthSourceProvider(MonoBehaviour host)
        {
            m_host = host;
        }

        public override string Name => "Pico";

        public override bool IsAvailable
        {
            get
            {
#if ENABLE_PICO_XR_SDK
                return m_manager && m_manager.IsDepthAvailable;
#else
                return false;
#endif
            }
        }

        public override bool TryEnsureManager()
        {
#if ENABLE_PICO_XR_SDK
            if (m_manager)
            {
                return true;
            }

            if (m_host)
            {
                m_manager = m_host.GetComponent<PXR_EnvironmentDepthManager>();
            }

            if (!m_manager)
            {
                m_manager = Object.FindAnyObjectByType<PXR_EnvironmentDepthManager>(FindObjectsInactive.Include);
            }

            if (!m_manager && m_host)
            {
                m_manager = m_host.gameObject.AddComponent<PXR_EnvironmentDepthManager>();
                m_ownsManager = m_manager;
            }

            return m_manager;
#else
            return false;
#endif
        }

        public override void Dispose()
        {
#if ENABLE_PICO_XR_SDK
            // Only destroy the manager we created ourselves; leave user-authored
            // components untouched.
            if (m_ownsManager && m_manager)
            {
                Object.Destroy(m_manager);
            }

            m_manager = null;
            m_ownsManager = false;
#endif
        }
    }
}
