using UnityEngine;
using UnityEngine.Networking;
using Unity.XR.CoreUtils;
using ByteDance.PICO.CameraPack;
#if ENABLE_PICO_XR_SDK
using ByteDance.PICO.XR;
#endif

namespace PICO.Samples.HandJointsOverlay
{
    /// <summary>
    /// Connects the PICO passthrough camera, hand joint tracking and the texture painter:
    /// projects the 26 hand joints onto the camera image, draws the skeleton into a copy
    /// of the frame and shows the processed image on a display quad.
    /// </summary>
    [RequireComponent(typeof(TextureSkeletonPainter))]
    public class HandJointsCameraOverlay : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private PXR_CamTextureManager m_cameraAccess;
        [SerializeField] private HandJointProvider m_handProvider;
        [SerializeField] private TextureSkeletonPainter m_painter;

        [Header("Hands")]
        [Tooltip("Draw the left hand skeleton.")]
        [SerializeField] private bool m_showLeftHand = true;
        [Tooltip("Draw the right hand skeleton.")]
        [SerializeField] private bool m_showRightHand = true;

        [Header("Display")]
        [Tooltip("Renderer of the quad that displays the processed camera image.")]
        [SerializeField] private Renderer m_displayRenderer;
        [Tooltip("Texture property to assign on the display material. URP Unlit uses _BaseMap.")]
        [SerializeField] private string m_texturePropertyName = "_BaseMap";

        [Header("Smoothing")]
        [Tooltip("Apply temporal Z-score filtering to joint world positions before projection. " +
                 "Rejects sudden tracking pops and smooths jitter.")]
        [SerializeField] private bool m_smoothJoints;
        [Tooltip("Number of recent frames each joint averages over. Higher = smoother but more lag.")]
        [SerializeField] private int m_smoothingWindow = 8;
        [Tooltip("Samples whose Z-score (distance from the window mean, in standard deviations) " +
                 "exceeds this are treated as outliers and dropped.")]
        [SerializeField] private float m_zScoreThreshold = 2f;
        [Tooltip("Average: mean of inliers (smoothest). Latest: newest inlier (least lag).")]
        [SerializeField] private ZScoreJointFilter.AggregationType m_aggregation =
            ZScoreJointFilter.AggregationType.Latest;

        [Header("Diagnostics")]
        [Tooltip("Log timestamp-alignment counters every 300 processed camera frames.")]
        [SerializeField] private bool m_enableTimingDiagnostics;

        private const int k_jointCount = HandJointProvider.k_jointCount;
        private const int k_diagnosticLogInterval = 300;
        private const double k_diagnosticWarmupMilliseconds = 1000d;
        private const int k_historyResultCount = 6;

        private ZScoreJointFilter m_leftFilter;
        private ZScoreJointFilter m_rightFilter;

        private readonly Vector3[] m_worldPositions = new Vector3[k_jointCount];
        private readonly bool[] m_jointValid = new bool[k_jointCount];
        private readonly Vector2Int[] m_pixels = new Vector2Int[k_jointCount];
        private readonly bool[] m_pixelVisible = new bool[k_jointCount];

        private int m_texturePropertyId;
        private Texture2D m_assignedTexture;
        private Material m_displayMaterialInstance;
        private CameraAPI m_cameraApi;
        private CameraFrameGate m_frameGate;
        private Pose m_frameCameraPose;
        private CameraIntrinsics m_frameIntrinsics;
        private readonly long[] m_historyResultCounts = new long[k_historyResultCount];
        private double m_diagnosticStartTimeMilliseconds;
        private int m_processedCameraFrames;
        private bool m_timestampFallbackWarningLogged;

        #region debug-point B-D:sample-projection-comparison
        private const string k_debugServerUrl = "http://127.0.0.1:7777/event";
        private readonly Vector3[] m_debugLatestPositions = new Vector3[k_jointCount];
        private readonly bool[] m_debugLatestValid = new bool[k_jointCount];
        private int m_debugCalibrationSequence;
        private XROrigin m_debugXrOrigin;
        private bool m_debugHandSnapshotSent;
        private bool m_debugCalibrationSnapshotSent;
        #endregion

        private void Awake()
        {
            m_texturePropertyId = Shader.PropertyToID(
                string.IsNullOrEmpty(m_texturePropertyName) ? "_BaseMap" : m_texturePropertyName);

            if (!m_cameraAccess)
            {
                m_cameraAccess = FindAnyObjectByType<PXR_CamTextureManager>(FindObjectsInactive.Include);
            }
            ResolveCameraApi();
            if (!m_handProvider)
            {
                m_handProvider = FindAnyObjectByType<HandJointProvider>(FindObjectsInactive.Include);
            }
            if (!m_painter)
            {
                m_painter = GetComponent<TextureSkeletonPainter>();
            }

            m_frameGate = new CameraFrameGate();
            m_leftFilter = new ZScoreJointFilter(k_jointCount, m_smoothingWindow);
            m_rightFilter = new ZScoreJointFilter(k_jointCount, m_smoothingWindow);

            #region debug-point F:tracking-space
            m_debugXrOrigin = FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            #endregion
        }

        private void LateUpdate()
        {
            if (!m_cameraAccess || !m_handProvider || !m_painter)
            {
                return;
            }

            CameraLifecycleTransition transition =
                m_frameGate.ObservePlayingState(m_cameraAccess.IsPlaying);
            if (!m_cameraAccess.IsPlaying)
            {
                if (transition == CameraLifecycleTransition.Stopped)
                {
                    ResetTrackingState();
                }
                return;
            }

            Texture2D source = m_cameraAccess.Texture;
            if (source == null || source.width <= 0 || source.height <= 0)
            {
                if (transition == CameraLifecycleTransition.Started)
                {
                    m_frameGate.Reset();
                }
                return;
            }

            uint firstUpdateCount = source.updateCount;
            if (transition == CameraLifecycleTransition.Started)
            {
                ResetTrackingState();
                m_frameGate.ArmBaseline(firstUpdateCount);
                return;
            }

            ResolveCameraApi();
            double observedCaptureTimeNanoseconds =
                m_cameraApi != null ? m_cameraApi.GetLatestTimestamp() : 0d;
            uint secondUpdateCount = source.updateCount;
            if (firstUpdateCount != secondUpdateCount)
            {
                return;
            }

            CameraFrameDecision decision = m_frameGate.Evaluate(
                secondUpdateCount,
                observedCaptureTimeNanoseconds,
                out double latchedCaptureTimeNanoseconds);
            if (decision == CameraFrameDecision.NoNewTexture)
            {
                return;
            }
            if (decision == CameraFrameDecision.LifecycleResetRequired)
            {
                ResetTrackingState();
                m_frameGate.ArmBaseline(secondUpdateCount);
                return;
            }

            bool hasCaptureTime = CameraFrameTiming.TryConvertXrTimeToMilliseconds(
                latchedCaptureTimeNanoseconds,
                out double captureTimeMilliseconds);
            bool hasFrameCameraData = false;
            if (hasCaptureTime)
            {
                hasFrameCameraData = TryGetFrameCameraData(
                    captureTimeMilliseconds,
                    out m_frameCameraPose,
                    out m_frameIntrinsics);
            }
            else if (!m_timestampFallbackWarningLogged)
            {
                Debug.LogWarning(
                    "HandJointsCameraOverlay: Camera timestamp is unavailable; " +
                    "falling back to latest hand joints and predicted-display camera pose.");
                m_timestampFallbackWarningLogged = true;
            }

            if (!m_painter.PrepareFrame(source))
            {
                return;
            }

            Vector2Int resolution = new Vector2Int(source.width, source.height);
            if (m_showLeftHand)
            {
                DrawHandIfTracked(
                    true,
                    resolution,
                    hasCaptureTime,
                    hasFrameCameraData,
                    captureTimeMilliseconds);
            }
            if (m_showRightHand)
            {
                DrawHandIfTracked(
                    false,
                    resolution,
                    hasCaptureTime,
                    hasFrameCameraData,
                    captureTimeMilliseconds);
            }

            m_painter.Apply();
            AssignDisplayTexture();
            m_frameGate.Commit();
            m_processedCameraFrames++;
            LogDiagnosticsIfNeeded();
        }

        private void DrawHandIfTracked(
            bool isLeftHand,
            Vector2Int resolution,
            bool hasCaptureTime,
            bool hasFrameCameraData,
            double captureTimeMilliseconds)
        {
            bool useFrameCameraPose = false;
            ZScoreJointFilter filter = isLeftHand ? m_leftFilter : m_rightFilter;
            #region debug-point B:history-sample-comparison
            int debugSequence = m_processedCameraFrames;
            bool debugShouldReport =
                !m_debugHandSnapshotSent &&
                Time.realtimeSinceStartup >= 5f;
            bool debugLatestTracked = false;
            #endregion
            if (hasCaptureTime)
            {
                HandHistorySampleResult result = m_handProvider.SampleJointsAt(
                    isLeftHand,
                    captureTimeMilliseconds,
                    m_worldPositions,
                    m_jointValid);
                RecordHistoryResult(result, captureTimeMilliseconds);
                #region debug-point B:history-sample-comparison
                if (debugShouldReport)
                {
                    debugLatestTracked = m_handProvider.TryGetJoints(
                        isLeftHand,
                        m_debugLatestPositions,
                        m_debugLatestValid);
                    Vector3 debugDelta = m_worldPositions[1] - m_debugLatestPositions[1];
                    string debugPayload = System.FormattableString.Invariant(
                        $"{{\"sessionId\":\"right-hand-projection-offset\",\"runId\":\"post-fix\",\"hypothesisId\":\"B\",\"ts\":{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"location\":\"HandJointsCameraOverlay.DrawHandIfTracked\",\"msg\":\"[DEBUG] History sample comparison\",\"data\":{{\"seq\":{debugSequence},\"unityFrame\":{Time.frameCount},\"hand\":\"{(isLeftHand ? "left" : "right")}\",\"captureMs\":{captureTimeMilliseconds},\"providerPredictedMs\":{m_handProvider.DebugLatestTimestampMilliseconds},\"sampleResult\":\"{result}\",\"latestTracked\":{(debugLatestTracked ? 1 : 0)},\"sampleWristValid\":{(m_jointValid[1] ? 1 : 0)},\"latestWristValid\":{(m_debugLatestValid[1] ? 1 : 0)},\"sampleWrist\":[{m_worldPositions[1].x},{m_worldPositions[1].y},{m_worldPositions[1].z}],\"latestWrist\":[{m_debugLatestPositions[1].x},{m_debugLatestPositions[1].y},{m_debugLatestPositions[1].z}],\"sampleMinusLatest\":[{debugDelta.x},{debugDelta.y},{debugDelta.z}]}}}}");
                    UnityWebRequest.Post(
                        k_debugServerUrl,
                        debugPayload,
                        "application/json").SendWebRequest();
                }
                #endregion
                if (result != HandHistorySampleResult.Success || !hasFrameCameraData)
                {
                    filter.Reset();
                    return;
                }

                useFrameCameraPose = true;
            }
            else if (!m_handProvider.TryGetJoints(
                         isLeftHand,
                         m_worldPositions,
                         m_jointValid))
            {
                filter.Reset();
                return;
            }

            if (m_smoothJoints)
            {
                filter.Filter(m_worldPositions, m_jointValid, m_zScoreThreshold, m_aggregation);
            }

            for (int i = 0; i < k_jointCount; i++)
            {
                m_pixelVisible[i] = m_jointValid[i] &&
                                    TryProjectToPixel(
                                        m_worldPositions[i],
                                        resolution,
                                        useFrameCameraPose,
                                        out m_pixels[i]);
            }

            #region debug-point D:projection-comparison
            if (debugShouldReport && useFrameCameraPose)
            {
                Vector2 debugSampleFrameViewport = ProjectWithFramePose(m_worldPositions[1]);
                Vector2 debugLatestFrameViewport =
                    ProjectWithFramePose(m_debugLatestPositions[1]);
                Vector2 debugLatestCurrentViewport =
                    m_cameraAccess.WorldToViewportPoint(m_debugLatestPositions[1]);
                string debugPayload = System.FormattableString.Invariant(
                    $"{{\"sessionId\":\"right-hand-projection-offset\",\"runId\":\"post-fix\",\"hypothesisId\":\"D\",\"ts\":{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"location\":\"HandJointsCameraOverlay.DrawHandIfTracked\",\"msg\":\"[DEBUG] Projection comparison\",\"data\":{{\"seq\":{debugSequence},\"unityFrame\":{Time.frameCount},\"hand\":\"{(isLeftHand ? "left" : "right")}\",\"samplePixel\":[{m_pixels[1].x},{m_pixels[1].y}],\"sampleVisible\":{(m_pixelVisible[1] ? 1 : 0)},\"sampleFrameViewport\":[{debugSampleFrameViewport.x},{debugSampleFrameViewport.y}],\"latestFrameViewport\":[{debugLatestFrameViewport.x},{debugLatestFrameViewport.y}],\"latestCurrentViewport\":[{debugLatestCurrentViewport.x},{debugLatestCurrentViewport.y}],\"cameraPosition\":[{m_frameCameraPose.position.x},{m_frameCameraPose.position.y},{m_frameCameraPose.position.z}],\"cameraRotation\":[{m_frameCameraPose.rotation.x},{m_frameCameraPose.rotation.y},{m_frameCameraPose.rotation.z},{m_frameCameraPose.rotation.w}]}}}}");
                UnityWebRequest.Post(
                    k_debugServerUrl,
                    debugPayload,
                    "application/json").SendWebRequest();
            }
            if (debugShouldReport && !isLeftHand)
            {
                m_debugHandSnapshotSent = true;
            }
            #endregion

            m_painter.DrawHand(m_pixels, m_pixelVisible, isLeftHand);
        }

        private bool TryProjectToPixel(
            Vector3 worldPosition,
            Vector2Int resolution,
            bool useFrameCameraPose,
            out Vector2Int pixel)
        {
            pixel = default;

            if (resolution.x <= 0 || resolution.y <= 0)
            {
                return false;
            }

            Vector2 viewport = useFrameCameraPose
                ? ProjectWithFramePose(worldPosition)
                : m_cameraAccess.WorldToViewportPoint(worldPosition);

            // Both projection paths return Vector2.zero for points behind the camera and
            // values outside [0,1] for points outside the frame; reject both.
            if (viewport.x <= 0f || viewport.x >= 1f || viewport.y <= 0f || viewport.y >= 1f)
            {
                return false;
            }

            pixel = new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(viewport.x * (resolution.x - 1)), 0, resolution.x - 1),
                Mathf.Clamp(Mathf.RoundToInt(viewport.y * (resolution.y - 1)), 0, resolution.y - 1));
            return true;
        }

        private Vector2 ProjectWithFramePose(Vector3 worldPosition)
        {
            Vector3 localPoint =
                Quaternion.Inverse(m_frameCameraPose.rotation) *
                (worldPosition - m_frameCameraPose.position);
            if (localPoint.z <= 0f)
            {
                return Vector2.zero;
            }

            float uPixel =
                m_frameIntrinsics.FocalLength.x * (localPoint.x / localPoint.z) +
                m_frameIntrinsics.CornerPoint.x;
            float vPixel =
                m_frameIntrinsics.FocalLength.y * (localPoint.y / localPoint.z) +
                m_frameIntrinsics.CornerPoint.y;
            return new Vector2(
                uPixel / m_frameIntrinsics.Resolution.x,
                vPixel / m_frameIntrinsics.Resolution.y);
        }

        private bool TryGetFrameCameraData(
            double captureTimeMilliseconds,
            out Pose cameraPose,
            out CameraIntrinsics intrinsics)
        {
            cameraPose = default;
            intrinsics = default;
            ResolveCameraApi();
            if (m_cameraApi == null)
            {
                return false;
            }

#if ENABLE_PICO_XR_SDK
            PxrSensorState2 sensorState = default;
            int sensorFrameIndex = 0;
            int sensorResult = PXR_Plugin.Pxr_GetPredictedMainSensorState2(
                captureTimeMilliseconds,
                ref sensorState,
                ref sensorFrameIndex);
            Pose worldFromHead = new Pose(
                sensorState.pose.position.ToVector3FlippedZ(),
                sensorState.pose.orientation.ToQuatFlippedZ());
            bool hasExtrinsics = m_cameraApi.GetCameraExtrinsics(
                m_cameraAccess.Eye,
                out CameraExtrinsics extrinsics);
            bool hasIntrinsics = m_cameraApi.GetCameraIntrinsics(
                m_cameraAccess.Eye,
                out intrinsics);
            bool hasComposedPose = CameraFramePoseUtility.TryComposeCameraPose(
                sensorResult,
                sensorState.status,
                worldFromHead,
                hasExtrinsics,
                extrinsics,
                hasIntrinsics,
                intrinsics,
                out cameraPose);

            #region debug-point F:camera-calibration
            if (!m_debugCalibrationSnapshotSent &&
                Time.realtimeSinceStartup >= 5f)
            {
                PxrSensorState2 debugCurrentSensorState = default;
                int debugCurrentSensorFrameIndex = 0;
                double debugCurrentTimestamp =
                    m_handProvider.DebugLatestTimestampMilliseconds;
                int debugCurrentSensorResult =
                    PXR_Plugin.Pxr_GetPredictedMainSensorState2(
                        debugCurrentTimestamp,
                        ref debugCurrentSensorState,
                        ref debugCurrentSensorFrameIndex);
                Pose debugCurrentWorldFromHead = new Pose(
                    debugCurrentSensorState.pose.position.ToVector3FlippedZ(),
                    debugCurrentSensorState.pose.orientation.ToQuatFlippedZ());
                Transform debugOriginTransform =
                    m_debugXrOrigin != null ? m_debugXrOrigin.Origin.transform : null;
                Transform debugCameraFloorOffsetTransform =
                    m_debugXrOrigin != null
                        ? m_debugXrOrigin.CameraFloorOffsetObject.transform
                        : null;
                Transform debugCameraTransform =
                    m_debugXrOrigin != null && m_debugXrOrigin.Camera != null
                        ? m_debugXrOrigin.Camera.transform
                        : Camera.main != null
                            ? Camera.main.transform
                            : null;
                Vector3 debugOriginPosition =
                    debugOriginTransform != null
                        ? debugOriginTransform.position
                        : default;
                Quaternion debugOriginRotation =
                    debugOriginTransform != null
                        ? debugOriginTransform.rotation
                        : Quaternion.identity;
                Vector3 debugFloorOffsetPosition =
                    debugCameraFloorOffsetTransform != null
                        ? debugCameraFloorOffsetTransform.position
                        : default;
                Vector3 debugCameraWorldPosition =
                    debugCameraTransform != null
                        ? debugCameraTransform.position
                        : default;
                Quaternion debugCameraWorldRotation =
                    debugCameraTransform != null
                        ? debugCameraTransform.rotation
                        : Quaternion.identity;
                Vector3 debugCameraLocalPosition =
                    debugCameraTransform != null
                        ? debugCameraTransform.localPosition
                        : default;
                Vector3 debugCameraMinusNativeHead =
                    debugCameraWorldPosition -
                    debugCurrentWorldFromHead.position;
                string debugPayload = System.FormattableString.Invariant(
                    $"{{\"sessionId\":\"right-hand-projection-offset\",\"runId\":\"post-fix\",\"hypothesisId\":\"F\",\"ts\":{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"location\":\"HandJointsCameraOverlay.TryGetFrameCameraData\",\"msg\":\"[DEBUG] Camera calibration snapshot\",\"data\":{{\"seq\":{m_debugCalibrationSequence},\"unityFrame\":{Time.frameCount},\"eye\":\"{m_cameraAccess.Eye}\",\"captureMs\":{captureTimeMilliseconds},\"currentMs\":{debugCurrentTimestamp},\"frameSensorResult\":{sensorResult},\"frameSensorStatus\":{sensorState.status},\"frameSensorIndex\":{sensorFrameIndex},\"currentSensorResult\":{debugCurrentSensorResult},\"currentSensorStatus\":{debugCurrentSensorState.status},\"currentSensorIndex\":{debugCurrentSensorFrameIndex},\"frameHeadPosition\":[{worldFromHead.position.x},{worldFromHead.position.y},{worldFromHead.position.z}],\"frameHeadRotation\":[{worldFromHead.rotation.x},{worldFromHead.rotation.y},{worldFromHead.rotation.z},{worldFromHead.rotation.w}],\"currentHeadPosition\":[{debugCurrentWorldFromHead.position.x},{debugCurrentWorldFromHead.position.y},{debugCurrentWorldFromHead.position.z}],\"currentHeadRotation\":[{debugCurrentWorldFromHead.rotation.x},{debugCurrentWorldFromHead.rotation.y},{debugCurrentWorldFromHead.rotation.z},{debugCurrentWorldFromHead.rotation.w}],\"hasExtrinsics\":{(hasExtrinsics ? 1 : 0)},\"extrinsicsPosition\":[{extrinsics.CameraPos.x},{extrinsics.CameraPos.y},{extrinsics.CameraPos.z}],\"extrinsicsRotation\":[{extrinsics.CameraRot.x},{extrinsics.CameraRot.y},{extrinsics.CameraRot.z},{extrinsics.CameraRot.w}],\"hasIntrinsics\":{(hasIntrinsics ? 1 : 0)},\"focalLength\":[{intrinsics.FocalLength.x},{intrinsics.FocalLength.y}],\"principalPoint\":[{intrinsics.CornerPoint.x},{intrinsics.CornerPoint.y}],\"intrinsicsResolution\":[{intrinsics.Resolution.x},{intrinsics.Resolution.y}],\"hasComposedPose\":{(hasComposedPose ? 1 : 0)},\"hasXrOrigin\":{(m_debugXrOrigin != null ? 1 : 0)},\"trackingOriginMode\":\"{(m_debugXrOrigin != null ? m_debugXrOrigin.CurrentTrackingOriginMode.ToString() : "Unknown")}\",\"originPosition\":[{debugOriginPosition.x},{debugOriginPosition.y},{debugOriginPosition.z}],\"originRotation\":[{debugOriginRotation.x},{debugOriginRotation.y},{debugOriginRotation.z},{debugOriginRotation.w}],\"floorOffsetPosition\":[{debugFloorOffsetPosition.x},{debugFloorOffsetPosition.y},{debugFloorOffsetPosition.z}],\"cameraWorldPosition\":[{debugCameraWorldPosition.x},{debugCameraWorldPosition.y},{debugCameraWorldPosition.z}],\"cameraWorldRotation\":[{debugCameraWorldRotation.x},{debugCameraWorldRotation.y},{debugCameraWorldRotation.z},{debugCameraWorldRotation.w}],\"cameraLocalPosition\":[{debugCameraLocalPosition.x},{debugCameraLocalPosition.y},{debugCameraLocalPosition.z}],\"cameraMinusNativeHead\":[{debugCameraMinusNativeHead.x},{debugCameraMinusNativeHead.y},{debugCameraMinusNativeHead.z}]}}}}");
                UnityWebRequest.Post(
                    k_debugServerUrl,
                    debugPayload,
                    "application/json").SendWebRequest();
                m_debugCalibrationSequence++;
                m_debugCalibrationSnapshotSent = true;
            }
            #endregion

            return hasComposedPose;
#else
            return false;
#endif
        }

        private void ResolveCameraApi()
        {
            if (m_cameraApi == null && m_cameraAccess)
            {
                m_cameraApi = m_cameraAccess.GetComponent<CameraAPI>();
            }
        }

        private void ResetTrackingState()
        {
            if (m_handProvider)
            {
                m_handProvider.ResetHistory();
            }
            m_leftFilter?.Reset();
            m_rightFilter?.Reset();
            ResetDiagnostics();
        }

        private void OnDisable()
        {
            m_frameGate?.Reset();
            ResetTrackingState();
        }

        private void RecordHistoryResult(
            HandHistorySampleResult result,
            double captureTimeMilliseconds)
        {
            if (m_diagnosticStartTimeMilliseconds <= 0d)
            {
                m_diagnosticStartTimeMilliseconds = captureTimeMilliseconds;
            }
            if (captureTimeMilliseconds - m_diagnosticStartTimeMilliseconds <
                k_diagnosticWarmupMilliseconds)
            {
                return;
            }

            int index = (int)result;
            if (index >= 0 && index < m_historyResultCounts.Length)
            {
                m_historyResultCounts[index]++;
            }
        }

        private void LogDiagnosticsIfNeeded()
        {
            if (!m_enableTimingDiagnostics ||
                m_processedCameraFrames <= 0 ||
                m_processedCameraFrames % k_diagnosticLogInterval != 0)
            {
                return;
            }

            Debug.Log(
                "HandJointsCameraOverlay timing: " +
                $"frames={m_processedCameraFrames}, " +
                $"success={m_historyResultCounts[(int)HandHistorySampleResult.Success]}, " +
                $"invalid={m_historyResultCounts[(int)HandHistorySampleResult.InvalidArguments]}, " +
                $"empty={m_historyResultCounts[(int)HandHistorySampleResult.Empty]}, " +
                $"before={m_historyResultCounts[(int)HandHistorySampleResult.TargetBeforeOldest]}, " +
                $"after={m_historyResultCounts[(int)HandHistorySampleResult.TargetAfterNewest]}, " +
                $"tracking={m_historyResultCounts[(int)HandHistorySampleResult.TrackingUnavailable]}");
        }

        private void ResetDiagnostics()
        {
            m_diagnosticStartTimeMilliseconds = 0d;
            m_processedCameraFrames = 0;
            for (int i = 0; i < m_historyResultCounts.Length; i++)
            {
                m_historyResultCounts[i] = 0L;
            }
        }

        private void AssignDisplayTexture()
        {
            if (!m_displayRenderer)
            {
                return;
            }

            Texture2D result = m_painter.ResultTexture;
            if (result == null || ReferenceEquals(result, m_assignedTexture))
            {
                return;
            }

            if (m_displayMaterialInstance == null)
            {
                m_displayMaterialInstance = m_displayRenderer.material;
            }

            m_displayMaterialInstance.SetTexture(m_texturePropertyId, result);
            m_assignedTexture = result;
        }

        private void OnDestroy()
        {
            if (m_displayMaterialInstance != null)
            {
                Destroy(m_displayMaterialInstance);
                m_displayMaterialInstance = null;
            }
        }
    }
}
