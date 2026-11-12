using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
using ByteDance.PICO.EnvironmentDepthExtension.Providers;

namespace ByteDance.PICO.EnvironmentDepthExtension
{
    public class EnvironmentDepthDataAccess : MonoBehaviour
    {
        private const int NumEyes = 2;
        // Readback resolution cap, aligned with Meta's 128x128x2 CPU raycast
        // sampling. The native depth texture (e.g. 320+) is nearest-neighbour
        // downsampled to this size before readback, cutting bandwidth/Job cost.
        // Never upscales: the effective sample size is min(k_maxSampleSize, native).
        private const int k_maxSampleSize = 128;
        private const string ComputeShaderResourceNameLower = "copydepthtextureintonativearray";
        private static readonly int CopiedDepthTextureId = Shader.PropertyToID("_CopiedDepthTexture");
        private static readonly int EnvironmentDepthTextureId = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int EnvironmentDepthTextureSizeId = Shader.PropertyToID("_EnvironmentDepthTextureSize");
        private static readonly int SourceTextureSizeId = Shader.PropertyToID("_SourceTextureSize");
        private static readonly int EnvironmentDepthZBufferParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

        public struct DepthFrameData
        {
            public NativeArray<float> DepthMeters;
            public int TextureSize;
            public Vector4 ZBufferParams;

            // Per-eye view-projection captured at submit time (by value).
            public Matrix4x4 ViewProjLeft;
            public Matrix4x4 ViewProjRight;
        }

        // Submit-time snapshot carried with the single in-flight readback so the
        // completion callback emits the matrices/zParams from when the request was
        // queued, not the (possibly newer) values from a later frame.
        private struct PendingMeta
        {
            public Matrix4x4 VpLeftAtSubmit;
            public Matrix4x4 VpRightAtSubmit;
            public Vector4 ZParamsAtSubmit;
            public int SizeAtSubmit;
            public int NativeSizeAtSubmit;
        }

        // Converts the RT-fallback single-channel depth (NDC) to linear meters in
        // parallel, with nearest-neighbour downsampling from the native source to
        // the (smaller) sample grid. Index space is [0, DstPixels*2): first half
        // is the left eye, second half the right. Dst uses Stride to offset the
        // right eye so the layout matches the GPU compute path (eye * size*size).
        [BurstCompile]
        private struct DepthConvertJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> SrcLeft;
            [ReadOnly] public NativeArray<float> SrcRight;
            public int DstPixels;
            public int SrcSize;
            public int DstSize;
            public int Stride;
            public Vector4 ZParams;
            // Writes are provably unique per index (eye*Stride+local never
            // overlaps), so the parallel-for range restriction is safe to lift.
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> Dst;

            public void Execute(int index)
            {
                int eye = index < DstPixels ? 0 : 1;
                int local = eye == 0 ? index : index - DstPixels;

                // Map the destination texel back to a source texel (nearest).
                int dstX = local % DstSize;
                int dstY = local / DstSize;
                float scale = SrcSize / (float)DstSize;
                int srcX = Mathf.Min((int)((dstX + 0.5f) * scale), SrcSize - 1);
                int srcY = Mathf.Min((int)((dstY + 0.5f) * scale), SrcSize - 1);
                int srcIdx = srcY * SrcSize + srcX;

                var src = eye == 0 ? SrcLeft : SrcRight;
                if (srcIdx < 0 || srcIdx >= src.Length)
                {
                    Dst[eye * Stride + local] = 0f;
                    return;
                }

                float raw = src[srcIdx];
                float ndc = raw * 2f - 1f;
                float denom = ndc + ZParams.y;
                float linearDepth = (Mathf.Abs(denom) < 1e-6f)
                    ? 0f
                    : (1f / denom) * ZParams.x;
                if (float.IsNaN(linearDepth) || float.IsInfinity(linearDepth))
                {
                    linearDepth = 0f;
                }

                Dst[eye * Stride + local] = linearDepth;
            }
        }

        public event Action<DepthFrameData> DepthFrameReceived;

        // Effective (downsampled) sample resolution. Internal state only; consumers
        // read the per-frame DepthFrameData.TextureSize instead.
        private int m_textureSize = 320;

        [SerializeField, Range(0, 120)] private int m_requestEveryNFrames = 1;
        [SerializeField] private PreferredProvider m_preferredProvider = PreferredProvider.Auto;
        [SerializeField, HideInInspector] private bool m_enableCpuReadback = true;
        [SerializeField, HideInInspector, Range(1, 600)] private int m_readbackTimeoutFrames = 120;
        [SerializeField, HideInInspector, Range(0, 120)] private int m_readbackPendingLogEveryNFrames = 30;
        [SerializeField, HideInInspector] private bool m_enableRenderTextureReadbackFallback = true;

        // Single active instance + throttled availability diagnostics (design.md §D9).
        private const float k_diagnosticsIntervalSeconds = 5f;
        private static EnvironmentDepthDataAccess s_instance;

        private ComputeShader m_computeShader;
        private int m_computeKernel = -1;
        private ComputeBuffer m_computeBuffer;
        private NativeArray<float> m_depthBuffer;
        private NativeArray<float> m_gpuRequestBuffer;
        private AsyncGPUReadbackRequest? m_currentGpuReadbackRequest;
        private int m_readbackStartFrame = -1;
        private AsyncGPUReadbackRequest? m_rtReadbackRequestLeft;
        private AsyncGPUReadbackRequest? m_rtReadbackRequestRight;
        private int m_rtReadbackStartFrame = -1;
        private RenderTexture m_rtSliceLeft;
        private RenderTexture m_rtSliceRight;
        private readonly Matrix4x4[] m_matrixVp = new Matrix4x4[NumEyes];
        private Vector4 m_cachedZBufferParams;
        private PendingMeta m_pendingMeta;
        private bool m_loggedMissingComputeShader;
        private IDepthSourceProvider m_provider;
        private float m_nextDiagnosticsTime;
        private int m_nativeDepthSize;

        private void Awake()
        {
            // Enforce a single active instance to avoid duplicated readbacks.
            if (s_instance && s_instance != this)
            {
                Debug.LogWarning("[EnvironmentDepthDataAccess] Duplicate instance detected; disabling self.", this);
                enabled = false;
                return;
            }
            s_instance = this;

            TryResolveComputeShader();

            m_provider = DepthSourceProviderFactory.Resolve(this, m_preferredProvider);
            m_provider.TryEnsureManager();

        }

        private void OnDestroy()
        {
            if (m_currentGpuReadbackRequest is { done: false })
            {
                m_currentGpuReadbackRequest.Value.WaitForCompletion();
            }

            if (m_rtReadbackRequestLeft is { done: false })
            {
                m_rtReadbackRequestLeft.Value.WaitForCompletion();
            }

            if (m_rtReadbackRequestRight is { done: false })
            {
                m_rtReadbackRequestRight.Value.WaitForCompletion();
            }

            m_computeBuffer?.Dispose();
            if (m_depthBuffer.IsCreated)
            {
                m_depthBuffer.Dispose();
            }

            if (m_gpuRequestBuffer.IsCreated)
            {
                m_gpuRequestBuffer.Dispose();
            }

            if (m_rtSliceLeft)
            {
                m_rtSliceLeft.Release();
                Destroy(m_rtSliceLeft);
            }

            if (m_rtSliceRight)
            {
                m_rtSliceRight.Release();
                Destroy(m_rtSliceRight);
            }

            m_provider?.Dispose();
            m_provider = null;

            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        private void Update()
        {
            TryCompleteGpuReadback();
            LogDiagnosticsIfNeeded();

            if (m_requestEveryNFrames <= 0)
            {
                return;
            }

            if (Time.frameCount % m_requestEveryNFrames != 0)
            {
                return;
            }

            RequestDepthSample();
        }

        // Throttled (~0.2 Hz) warning when the depth source is unavailable
        // (design.md §D9), so a misconfigured scene is easy to diagnose without
        // spamming the console.
        private void LogDiagnosticsIfNeeded()
        {
            if (m_provider == null || m_provider.IsAvailable)
            {
                return;
            }

            if (Time.unscaledTime < m_nextDiagnosticsTime)
            {
                return;
            }

            m_nextDiagnosticsTime = Time.unscaledTime + k_diagnosticsIntervalSeconds;
            Debug.LogWarning($"[EnvironmentDepthDataAccess] Depth source '{m_provider.Name}' is not available (SDK missing or depth not yet produced).", this);
        }

        public void RequestDepthSample()
        {
            SampleDepth();
        }

        private void SampleDepth()
        {
            if (!enabled || !IsDepthAvailable())
            {
                return;
            }

            // Provider owns the per-frame data read; consumer never touches the
            // backend property names.
            if (m_provider == null || !m_provider.TryGetGlobals(out var globals))
            {
                return;
            }

            var depthTexture = globals.DepthTexture;
            var zParams = globals.ZBufferParams;

            // Downsample to at most k_maxSampleSize (Meta-aligned 128). TextureSize
            // now reports the sample resolution; consumers index purely by it
            // (UV-normalized), so downsampling stays transparent to them.
            int nativeSize = depthTexture.width;
            int sampleSize = Mathf.Min(k_maxSampleSize, nativeSize);

            if (!m_depthBuffer.IsCreated || m_textureSize != sampleSize)
            {
                if (m_depthBuffer.IsCreated)
                {
                    m_computeBuffer?.Dispose();
                    m_depthBuffer.Dispose();
                    m_gpuRequestBuffer.Dispose();
                }

                m_textureSize = sampleSize;
                int numPixels = m_textureSize * m_textureSize * NumEyes;
                m_computeBuffer = new ComputeBuffer(numPixels, sizeof(float));
                m_depthBuffer = new NativeArray<float>(numPixels, Allocator.Persistent);
                m_gpuRequestBuffer = new NativeArray<float>(numPixels, Allocator.Persistent);
            }

            m_nativeDepthSize = nativeSize;

            m_matrixVp[0] = globals.ReprojLeft;
            m_matrixVp[1] = globals.ReprojRight;

            m_cachedZBufferParams = zParams;

            if (!m_enableCpuReadback)
            {
                DepthFrameReceived?.Invoke(new DepthFrameData
                {
                    DepthMeters = default,
                    TextureSize = m_textureSize,
                    ZBufferParams = zParams,
                    ViewProjLeft = m_matrixVp[0],
                    ViewProjRight = m_matrixVp[1]
                });
                return;
            }

            if (!TryResolveComputeShader())
            {
                if (!m_loggedMissingComputeShader)
                {
                    Debug.LogError("[EnvironmentDepthDataAccess] Missing ComputeShader for depth copy under a Resources folder.", this);
                    m_loggedMissingComputeShader = true;
                }
                StartRenderTextureReadbackFallback(depthTexture);
                return;
            }

            if (m_currentGpuReadbackRequest.HasValue)
            {
                int elapsed = m_readbackStartFrame >= 0 ? (Time.frameCount - m_readbackStartFrame) : 0;
                if (m_readbackPendingLogEveryNFrames > 0 && Time.frameCount % m_readbackPendingLogEveryNFrames == 0)
                {
                    var req = m_currentGpuReadbackRequest.Value;
                    Debug.Log($"[EnvironmentDepthDataAccess] GPU readback pending. done:{req.done} error:{req.hasError} elapsedFrames:{elapsed}", this);
                }

                if (elapsed < m_readbackTimeoutFrames)
                {
                    return;
                }

                m_currentGpuReadbackRequest = null;
                m_readbackStartFrame = -1;
                StartRenderTextureReadbackFallback(depthTexture);
            }

            int kernel = m_computeKernel >= 0 ? m_computeKernel : 0;
            m_computeShader.SetTexture(kernel, EnvironmentDepthTextureId, depthTexture);
            // Output grid = sample size; source size drives nearest-neighbour fetch.
            m_computeShader.SetFloat(EnvironmentDepthTextureSizeId, m_textureSize);
            m_computeShader.SetFloat(SourceTextureSizeId, depthTexture.width);
            Assert.AreEqual(depthTexture.width, depthTexture.height);
            m_computeShader.SetVector(EnvironmentDepthZBufferParamsId, zParams);
            m_computeShader.SetBuffer(kernel, CopiedDepthTextureId, m_computeBuffer);

            // Full-coverage dispatch at the (downsampled) sample size. The compute
            // shader bounds-checks via `id >= size`, so ceil(N/8) thread groups
            // cover the whole output grid.
            int groups = Mathf.CeilToInt(m_textureSize / 8f);
            m_computeShader.Dispatch(kernel, groups, groups, 1);

            // Freeze submit-time matrices/zParams so the completion callback emits
            // the values from when this request was queued (design.md §D3).
            m_pendingMeta = new PendingMeta
            {
                VpLeftAtSubmit = m_matrixVp[0],
                VpRightAtSubmit = m_matrixVp[1],
                ZParamsAtSubmit = zParams,
                SizeAtSubmit = m_textureSize,
                NativeSizeAtSubmit = m_nativeDepthSize,
            };

            m_readbackStartFrame = Time.frameCount;
            m_currentGpuReadbackRequest = AsyncGPUReadback.RequestIntoNativeArray(ref m_gpuRequestBuffer, m_computeBuffer);
        }

        private void StartRenderTextureReadbackFallback(RenderTexture depthTexture)
        {
            if (!m_enableRenderTextureReadbackFallback || !depthTexture)
            {
                return;
            }

            if (m_rtReadbackRequestLeft.HasValue || m_rtReadbackRequestRight.HasValue)
            {
                return;
            }

            EnsureReadbackSlices(depthTexture);
            if (!m_rtSliceLeft || !m_rtSliceRight)
            {
                return;
            }

            try
            {
                Graphics.CopyTexture(depthTexture, 0, 0, m_rtSliceLeft, 0, 0);
                Graphics.CopyTexture(depthTexture, 1, 0, m_rtSliceRight, 0, 0);
            }
            catch
            {
                return;
            }

            m_rtReadbackStartFrame = Time.frameCount;
            m_pendingMeta = new PendingMeta
            {
                VpLeftAtSubmit = m_matrixVp[0],
                VpRightAtSubmit = m_matrixVp[1],
                ZParamsAtSubmit = m_cachedZBufferParams,
                SizeAtSubmit = m_textureSize,
                NativeSizeAtSubmit = m_nativeDepthSize,
            };
            // The depth slices are single-channel; request RFloat (4B/px) instead
            // of RGBAFloat (16B/px) to cut readback bandwidth by 75%.
            m_rtReadbackRequestLeft = AsyncGPUReadback.Request(m_rtSliceLeft, 0, TextureFormat.RFloat);
            m_rtReadbackRequestRight = AsyncGPUReadback.Request(m_rtSliceRight, 0, TextureFormat.RFloat);
        }

        private void TryCompleteGpuReadback()
        {
            if (!m_currentGpuReadbackRequest.HasValue)
            {
                TryCompleteRenderTextureReadback();
                return;
            }

            var req = m_currentGpuReadbackRequest.Value;
            if (!req.done)
            {
                TryCompleteRenderTextureReadback();
                return;
            }

            if (req.hasError)
            {
                m_currentGpuReadbackRequest = null;
                m_readbackStartFrame = -1;
                TryCompleteRenderTextureReadback();
                return;
            }

            (m_depthBuffer, m_gpuRequestBuffer) = (m_gpuRequestBuffer, m_depthBuffer);

            DepthFrameReceived?.Invoke(new DepthFrameData
            {
                DepthMeters = m_depthBuffer,
                TextureSize = m_pendingMeta.SizeAtSubmit,
                ZBufferParams = m_pendingMeta.ZParamsAtSubmit,
                ViewProjLeft = m_pendingMeta.VpLeftAtSubmit,
                ViewProjRight = m_pendingMeta.VpRightAtSubmit
            });

            m_currentGpuReadbackRequest = null;
            m_readbackStartFrame = -1;

            TryCompleteRenderTextureReadback();
        }

        private void TryCompleteRenderTextureReadback()
        {
            if (!m_rtReadbackRequestLeft.HasValue && !m_rtReadbackRequestRight.HasValue)
            {
                return;
            }

            if (!m_depthBuffer.IsCreated)
            {
                m_rtReadbackRequestLeft = null;
                m_rtReadbackRequestRight = null;
                m_rtReadbackStartFrame = -1;
                return;
            }

            bool leftDone = !m_rtReadbackRequestLeft.HasValue || m_rtReadbackRequestLeft.Value.done;
            bool rightDone = !m_rtReadbackRequestRight.HasValue || m_rtReadbackRequestRight.Value.done;

            if (!(leftDone && rightDone))
            {
                if (m_readbackPendingLogEveryNFrames > 0 && Time.frameCount % m_readbackPendingLogEveryNFrames == 0)
                {
                    int elapsed = m_rtReadbackStartFrame >= 0 ? (Time.frameCount - m_rtReadbackStartFrame) : 0;
                    bool lErr = m_rtReadbackRequestLeft.HasValue && m_rtReadbackRequestLeft.Value.hasError;
                    bool rErr = m_rtReadbackRequestRight.HasValue && m_rtReadbackRequestRight.Value.hasError;
                    bool lDone = m_rtReadbackRequestLeft.HasValue && m_rtReadbackRequestLeft.Value.done;
                    bool rDone = m_rtReadbackRequestRight.HasValue && m_rtReadbackRequestRight.Value.done;
                    Debug.Log($"[EnvironmentDepthDataAccess] RT readback pending. Ldone:{lDone} Lerr:{lErr} Rdone:{rDone} Rerr:{rErr} elapsedFrames:{elapsed}", this);
                }

                int elapsedTimeout = m_rtReadbackStartFrame >= 0 ? (Time.frameCount - m_rtReadbackStartFrame) : 0;
                if (elapsedTimeout >= m_readbackTimeoutFrames)
                {
                    m_rtReadbackRequestLeft = null;
                    m_rtReadbackRequestRight = null;
                    m_rtReadbackStartFrame = -1;
                }

                return;
            }

            if (m_rtReadbackRequestLeft.HasValue && m_rtReadbackRequestLeft.Value.hasError)
            {
                m_rtReadbackRequestLeft = null;
                m_rtReadbackRequestRight = null;
                m_rtReadbackStartFrame = -1;
                return;
            }

            if (m_rtReadbackRequestRight.HasValue && m_rtReadbackRequestRight.Value.hasError)
            {
                m_rtReadbackRequestLeft = null;
                m_rtReadbackRequestRight = null;
                m_rtReadbackStartFrame = -1;
                return;
            }

            int dstSize = m_pendingMeta.SizeAtSubmit > 0 ? m_pendingMeta.SizeAtSubmit : m_textureSize;
            int srcSize = m_pendingMeta.NativeSizeAtSubmit > 0 ? m_pendingMeta.NativeSizeAtSubmit : dstSize;
            int dstPixels = dstSize * dstSize;
            var zParams = m_pendingMeta.ZParamsAtSubmit;

            // Both eye requests are issued together, so they are either both
            // present or both absent (and we early-returned on the latter).
            if (m_rtReadbackRequestLeft.HasValue && m_rtReadbackRequestRight.HasValue)
            {
                var srcLeft = m_rtReadbackRequestLeft.Value.GetData<float>();
                var srcRight = m_rtReadbackRequestRight.Value.GetData<float>();

                var convertJob = new DepthConvertJob
                {
                    SrcLeft = srcLeft,
                    SrcRight = srcRight,
                    DstPixels = dstPixels,
                    SrcSize = srcSize,
                    DstSize = dstSize,
                    Stride = dstPixels,
                    ZParams = zParams,
                    Dst = m_depthBuffer
                };
                convertJob.Schedule(dstPixels * 2, 64).Complete();
            }

            DepthFrameReceived?.Invoke(new DepthFrameData
            {
                DepthMeters = m_depthBuffer,
                TextureSize = m_pendingMeta.SizeAtSubmit,
                ZBufferParams = m_pendingMeta.ZParamsAtSubmit,
                ViewProjLeft = m_pendingMeta.VpLeftAtSubmit,
                ViewProjRight = m_pendingMeta.VpRightAtSubmit
            });

            m_rtReadbackRequestLeft = null;
            m_rtReadbackRequestRight = null;
            m_rtReadbackStartFrame = -1;
        }

        private void EnsureReadbackSlices(RenderTexture source)
        {
            if (!source)
            {
                return;
            }

            int w = source.width;
            int h = source.height;

            if (m_rtSliceLeft && m_rtSliceLeft.width == w && m_rtSliceLeft.height == h && m_rtSliceLeft.graphicsFormat == source.graphicsFormat)
            {
                return;
            }

            if (m_rtSliceLeft)
            {
                m_rtSliceLeft.Release();
                Destroy(m_rtSliceLeft);
            }

            if (m_rtSliceRight)
            {
                m_rtSliceRight.Release();
                Destroy(m_rtSliceRight);
            }

            var desc = source.descriptor;
            desc.dimension = TextureDimension.Tex2D;
            desc.volumeDepth = 1;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.enableRandomWrite = false;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;

            m_rtSliceLeft = new RenderTexture(desc) { name = "EnvDepthSlice_L" };
            m_rtSliceRight = new RenderTexture(desc) { name = "EnvDepthSlice_R" };
            m_rtSliceLeft.Create();
            m_rtSliceRight.Create();
        }

        private bool IsDepthAvailable()
        {
            return m_provider != null && m_provider.IsAvailable;
        }

        private bool TryResolveComputeShader()
        {
            if (m_computeShader)
            {
                return true;
            }

            m_computeShader = Resources.Load<ComputeShader>(ComputeShaderResourceNameLower);

            if (m_computeShader)
            {
                try
                {
                    m_computeKernel = m_computeShader.FindKernel("CSMain");
                }
                catch
                {
                    m_computeKernel = 0;
                }
            }

            return m_computeShader;
        }
    }
}
