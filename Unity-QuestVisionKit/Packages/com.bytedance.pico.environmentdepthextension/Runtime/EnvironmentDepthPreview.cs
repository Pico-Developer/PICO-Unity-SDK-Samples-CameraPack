using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace ByteDance.PICO.EnvironmentDepthExtension
{
    [RequireComponent(typeof(EnvironmentDepthDataAccess))]
    public class EnvironmentDepthPreview : MonoBehaviour
    {
        private enum MappingMode
        {
            Linear,
            AutoRange
        }

        [Header("Target")]
        [SerializeField] private RawImage m_rawImage;

        [Header("Mapping")]
        [SerializeField, HideInInspector] private MappingMode m_mappingMode = MappingMode.AutoRange;
        [SerializeField, HideInInspector] private bool m_invert;
        [SerializeField, HideInInspector] private float m_minDepthMeters = 0.0f;
        [SerializeField, HideInInspector] private float m_maxDepthMeters = 5.0f;
        [SerializeField, HideInInspector] private float m_autoMaxDepthMeters = 5.0f;
        [SerializeField, HideInInspector, Range(0.1f, 3.0f)] private float m_gamma = 1.0f;

        [Header("Diagnostics")]
        [SerializeField, HideInInspector, Range(0, 120)] private int m_logEveryNFrames = 30;

        private EnvironmentDepthDataAccess m_depthAccess;
        private Texture2D m_texture;
        private Color32[] m_pixels;
        private int m_lastLogFrame;

        private void Awake()
        {
            m_depthAccess = GetComponent<EnvironmentDepthDataAccess>();
        }

        private void OnEnable()
        {
            if (m_depthAccess)
            {
                m_depthAccess.DepthFrameReceived += HandleDepthFrameReceived;
            }
        }

        private void OnDisable()
        {
            if (m_depthAccess)
            {
                m_depthAccess.DepthFrameReceived -= HandleDepthFrameReceived;
            }
        }

        private void OnDestroy()
        {
            if (m_texture)
            {
                Destroy(m_texture);
            }
        }

        private void HandleDepthFrameReceived(EnvironmentDepthDataAccess.DepthFrameData frame)
        {
            if (!frame.DepthMeters.IsCreated || frame.TextureSize <= 0)
            {
                return;
            }

            int size = frame.TextureSize;
            EnsureTexture(size);

            int pixelCount = size * size;
            int maxSampleCount = Mathf.Min(pixelCount, frame.DepthMeters.Length);

            float minD = float.MaxValue;
            float maxD = float.MinValue;
            float sum = 0f;
            int valid = 0;
            float farThreshold = float.PositiveInfinity;
            if (TryComputeNearFar(frame.ZBufferParams, out _, out float far))
            {
                farThreshold = far - Mathf.Max(0.01f, far * 0.001f);
            }

            if (m_mappingMode == MappingMode.AutoRange || ShouldLog())
            {
                for (int i = 0; i < maxSampleCount; i++)
                {
                    float d = frame.DepthMeters[i];
                    if (!(d > 0.0001f) || float.IsNaN(d) || float.IsInfinity(d))
                    {
                        continue;
                    }
                    if (d >= farThreshold)
                    {
                        continue;
                    }

                    if (d < minD) minD = d;
                    if (d > maxD) maxD = d;
                    sum += d;
                    valid++;
                }
            }

            float mapMin = m_minDepthMeters;
            float mapMax = m_maxDepthMeters;

            if (m_mappingMode == MappingMode.AutoRange && valid > 0 && (maxD - minD) > 0.001f)
            {
                mapMin = minD;
                mapMax = m_autoMaxDepthMeters > 0.0001f ? Mathf.Min(maxD, m_autoMaxDepthMeters) : maxD;
            }

            float denom = Mathf.Max(mapMax - mapMin, 0.001f);

            for (int i = 0; i < pixelCount; i++)
            {
                float d = i < frame.DepthMeters.Length ? frame.DepthMeters[i] : 0f;
                if (!(d > 0.0001f) || float.IsNaN(d) || float.IsInfinity(d) || d >= farThreshold)
                {
                    m_pixels[i] = new Color32(0, 0, 0, 255);
                    continue;
                }
                float t = Mathf.Clamp01((d - mapMin) / denom);
                if (m_invert)
                {
                    t = 1f - t;
                }
                if (Mathf.Abs(m_gamma - 1.0f) > 0.0001f && m_gamma > 0.0001f)
                {
                    t = Mathf.Pow(t, m_gamma);
                }

                byte v = (byte)(t * 255f);
                m_pixels[i] = new Color32(v, v, v, 255);
            }

            m_texture.SetPixels32(m_pixels);
            m_texture.Apply(false);

            if (m_rawImage)
            {
                m_rawImage.texture = m_texture;
            }

            if (ShouldLog())
            {
                float avg = valid > 0 ? sum / valid : 0f;
                Debug.Log($"[EnvironmentDepthPreview] Valid:{valid}/{maxSampleCount} Min:{minD:F3} Max:{maxD:F3} Avg:{avg:F3} MapMin:{mapMin:F3} MapMax:{mapMax:F3}");
                m_lastLogFrame = Time.frameCount;
            }
        }

        private static bool TryComputeNearFar(Vector4 zBufferParams, out float near, out float far)
        {
            near = 0f;
            far = 0f;
            float a = zBufferParams.x;
            float b = zBufferParams.y;
            float denomNear = b - 1f;
            float denomFar = 1f + b;
            if (Mathf.Abs(denomNear) < 0.000001f || Mathf.Abs(denomFar) < 0.000001f)
            {
                return false;
            }

            near = a / denomNear;
            far = a / denomFar;
            if (!(near > 0.000001f) || !(far > near) || float.IsNaN(near) || float.IsNaN(far) || float.IsInfinity(near) || float.IsInfinity(far))
            {
                return false;
            }

            return true;
        }

        private void EnsureTexture(int size)
        {
            if (m_texture && m_texture.width == size && m_texture.height == size)
            {
                return;
            }

            if (m_texture)
            {
                Destroy(m_texture);
            }

            m_texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            m_pixels = new Color32[size * size];
        }

        private bool ShouldLog()
        {
            if (m_logEveryNFrames <= 0)
            {
                return false;
            }

            return Time.frameCount - m_lastLogFrame >= m_logEveryNFrames;
        }
    }
}
