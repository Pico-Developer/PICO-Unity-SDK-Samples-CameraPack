using ByteDance.PICO.CameraPack;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PICO.Samples.HandJointsOverlay.Tests
{
    public class HandJointsCameraTimingTests
    {
        [Test]
        public void TryConvertXrTimeToMilliseconds_ConvertsNanoseconds()
        {
            bool success = CameraFrameTiming.TryConvertXrTimeToMilliseconds(
                1_234_000_000d,
                out double milliseconds);

            Assert.That(success, Is.True);
            Assert.That(milliseconds, Is.EqualTo(1234d).Within(0.000001d));
        }

        [Test]
        public void ProjectSetting_EnablesHandTracking()
        {
            Object projectSetting = AssetDatabase.LoadMainAssetAtPath(
                "Assets/Resources/PXR_ProjectSetting.asset");
            var serializedSetting = new SerializedObject(projectSetting);

            SerializedProperty handTracking =
                serializedSetting.FindProperty("handTracking");

            Assert.That(handTracking, Is.Not.Null);
            Assert.That(handTracking.boolValue, Is.True);
        }

        [Test]
        public void VisualizerScene_CameraOutputIsVisibleToMainCamera()
        {
            const string scenePath =
                "Assets/Samples/3 HandJointsOverlay/HandJointsOverlayVisualizer.unity";
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                HandJointsCameraOverlay overlay = FindInScene<HandJointsCameraOverlay>(scene);
                Assert.That(overlay, Is.Not.Null);
                Assert.That(overlay.isActiveAndEnabled, Is.True);

                var serializedOverlay = new SerializedObject(overlay);
                PXR_CamTextureManager cameraAccess =
                    serializedOverlay.FindProperty("m_cameraAccess").objectReferenceValue
                    as PXR_CamTextureManager;
                TextureSkeletonPainter painter =
                    serializedOverlay.FindProperty("m_painter").objectReferenceValue
                    as TextureSkeletonPainter;
                Renderer displayRenderer =
                    serializedOverlay.FindProperty("m_displayRenderer").objectReferenceValue
                    as Renderer;

                Assert.That(cameraAccess, Is.Not.Null);
                Assert.That(cameraAccess.isActiveAndEnabled, Is.True);
                Assert.That(painter, Is.Not.Null);
                Assert.That(painter.isActiveAndEnabled, Is.True);
                Assert.That(displayRenderer, Is.Not.Null);
                Assert.That(displayRenderer.enabled, Is.True);
                Assert.That(displayRenderer.gameObject.activeInHierarchy, Is.True);
                Assert.That(displayRenderer.sharedMaterial, Is.Not.Null);
                Assert.That(displayRenderer.sharedMaterial.HasProperty("_BaseMap"), Is.True);

                Camera mainCamera = FindMainCamera(scene);
                Assert.That(mainCamera, Is.Not.Null);
                Assert.That(mainCamera.isActiveAndEnabled, Is.True);
                Assert.That(
                    displayRenderer.transform.IsChildOf(mainCamera.transform),
                    Is.True,
                    "The camera output display must follow the tracked XR camera.");
                Assert.That(
                    mainCamera.cullingMask & (1 << displayRenderer.gameObject.layer),
                    Is.Not.Zero);
                Assert.That(
                    GeometryUtility.TestPlanesAABB(
                        GeometryUtility.CalculateFrustumPlanes(mainCamera),
                        displayRenderer.bounds),
                    Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [TestCase(CameraLifecycleTransition.None, false)]
        [TestCase(CameraLifecycleTransition.Started, true)]
        [TestCase(CameraLifecycleTransition.Stopped, false)]
        public void HandTrackerRestartPolicy_RestartsOnlyWhenCameraStarts(
            CameraLifecycleTransition transition,
            bool expected)
        {
            Assert.That(
                HandTrackerRestartPolicy.ShouldRestart(transition),
                Is.EqualTo(expected));
        }

        [TestCase(0d)]
        [TestCase(-1d)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void TryConvertXrTimeToMilliseconds_RejectsInvalidValues(double value)
        {
            bool success = CameraFrameTiming.TryConvertXrTimeToMilliseconds(value, out double milliseconds);

            Assert.That(success, Is.False);
            Assert.That(milliseconds, Is.Zero);
        }

        [Test]
        public void Gate_Unarmed_DoesNotAcceptTexture()
        {
            var gate = new CameraFrameGate();

            CameraFrameDecision decision = gate.Evaluate(1U, 100d, out double timestamp);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(timestamp, Is.Zero);
        }

        [Test]
        public void Gate_ArmBaseline_SkipsCurrentTextureAndAcceptsNext()
        {
            CameraFrameGate gate = CreateArmedGate(10U);

            Assert.That(
                gate.Evaluate(10U, 100d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(
                gate.Evaluate(11U, 200d, out double timestamp),
                Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(timestamp, Is.EqualTo(200d));
        }

        [Test]
        public void Gate_Commit_PreventsDuplicateProcessing()
        {
            CameraFrameGate gate = CreateArmedGate(10U);
            Assert.That(gate.Evaluate(11U, 200d, out _), Is.EqualTo(CameraFrameDecision.Ready));

            gate.Commit();

            Assert.That(
                gate.Evaluate(11U, 200d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
        }

        [Test]
        public void Gate_CommitWithoutPending_DoesNotAcceptBaseline()
        {
            CameraFrameGate gate = CreateArmedGate(10U);

            gate.Commit();

            Assert.That(
                gate.Evaluate(10U, 100d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
        }

        [Test]
        public void Gate_RetrySameTexture_KeepsFirstLatchedTimestamp()
        {
            CameraFrameGate gate = CreateArmedGate(10U);
            Assert.That(gate.Evaluate(11U, 200d, out _), Is.EqualTo(CameraFrameDecision.Ready));

            CameraFrameDecision decision = gate.Evaluate(11U, 300d, out double timestamp);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(timestamp, Is.EqualTo(200d));
        }

        [Test]
        public void Gate_NewerTexture_ReplacesUncommittedPendingFrame()
        {
            CameraFrameGate gate = CreateArmedGate(10U);
            Assert.That(gate.Evaluate(11U, 200d, out _), Is.EqualTo(CameraFrameDecision.Ready));

            CameraFrameDecision decision = gate.Evaluate(12U, 300d, out double timestamp);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(timestamp, Is.EqualTo(300d));
        }

        [Test]
        public void Gate_TextureRegression_RequiresLifecycleReset()
        {
            CameraFrameGate gate = CreateCommittedGate(10U, 11U, 200d);

            CameraFrameDecision decision = gate.Evaluate(9U, 300d, out _);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.LifecycleResetRequired));
        }

        [Test]
        public void Gate_TimestampRegression_RequiresLifecycleReset()
        {
            CameraFrameGate gate = CreateCommittedGate(10U, 11U, 200d);

            CameraFrameDecision decision = gate.Evaluate(12U, 199d, out _);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.LifecycleResetRequired));
        }

        [Test]
        public void Gate_InvalidTimestamp_StillAcceptsNewTexture()
        {
            CameraFrameGate gate = CreateArmedGate(10U);

            CameraFrameDecision decision = gate.Evaluate(11U, 0d, out double timestamp);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(timestamp, Is.Zero);
        }

        [Test]
        public void Gate_StaticTextureCount_UsesAdvancingCaptureTimestamp()
        {
            CameraFrameGate gate = CreateArmedGate(0U);

            Assert.That(
                gate.Evaluate(0U, 100d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(
                gate.Evaluate(0U, 200d, out double firstTimestamp),
                Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(firstTimestamp, Is.EqualTo(200d));
            gate.Commit();

            Assert.That(
                gate.Evaluate(0U, 200d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(
                gate.Evaluate(0U, 300d, out double secondTimestamp),
                Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(secondTimestamp, Is.EqualTo(300d));
        }

        [Test]
        public void Gate_StaticTextureCount_ReplacesPendingWithNewerTimestamp()
        {
            CameraFrameGate gate = CreateArmedGate(0U);
            Assert.That(
                gate.Evaluate(0U, 100d, out _),
                Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(
                gate.Evaluate(0U, 200d, out _),
                Is.EqualTo(CameraFrameDecision.Ready));

            CameraFrameDecision decision = gate.Evaluate(0U, 300d, out double timestamp);

            Assert.That(decision, Is.EqualTo(CameraFrameDecision.Ready));
            Assert.That(timestamp, Is.EqualTo(300d));
        }

        [Test]
        public void Gate_PlayingEdges_ResetFrameState()
        {
            var gate = new CameraFrameGate();
            Assert.That(gate.ObservePlayingState(false), Is.EqualTo(CameraLifecycleTransition.None));
            Assert.That(gate.ObservePlayingState(true), Is.EqualTo(CameraLifecycleTransition.Started));
            gate.ArmBaseline(10U);
            Assert.That(gate.Evaluate(11U, 200d, out _), Is.EqualTo(CameraFrameDecision.Ready));
            gate.Commit();

            Assert.That(gate.ObservePlayingState(false), Is.EqualTo(CameraLifecycleTransition.Stopped));
            Assert.That(gate.Evaluate(12U, 300d, out _), Is.EqualTo(CameraFrameDecision.NoNewTexture));
            Assert.That(gate.ObservePlayingState(true), Is.EqualTo(CameraLifecycleTransition.Started));
            Assert.That(gate.Evaluate(12U, 300d, out _), Is.EqualTo(CameraFrameDecision.NoNewTexture));
        }

        [Test]
        public void Gate_Reset_AlreadyPlayingCameraProducesNewStartedEdge()
        {
            var gate = new CameraFrameGate();
            Assert.That(gate.ObservePlayingState(true), Is.EqualTo(CameraLifecycleTransition.Started));
            gate.ArmBaseline(10U);

            gate.Reset();

            Assert.That(gate.ObservePlayingState(true), Is.EqualTo(CameraLifecycleTransition.Started));
        }

        [Test]
        public void TryComposeCameraPose_ComposesHeadAndExtrinsics()
        {
            var worldFromHead = new Pose(new Vector3(1f, 2f, 3f), Quaternion.identity);
            var extrinsics = new CameraExtrinsics
            {
                CameraPos = new Vector3(0.1f, 0.2f, 0.3f),
                CameraRot = Quaternion.identity
            };

            bool success = CameraFramePoseUtility.TryComposeCameraPose(
                sensorResult: 0,
                sensorStatus: 0x3,
                worldFromHead,
                hasExtrinsics: true,
                extrinsics,
                hasIntrinsics: true,
                CreateValidIntrinsics(),
                out Pose cameraPose);

            Assert.That(success, Is.True);
            Assert.That(cameraPose.position, Is.EqualTo(new Vector3(1.1f, 2.2f, 3.3f)));
            Assert.That(
                Quaternion.Angle(cameraPose.rotation, Quaternion.Euler(180f, 0f, 0f)),
                Is.LessThan(0.001f));
        }

        [Test]
        public void TryComposeCameraPose_AppliesHeadRotationToExtrinsicPosition()
        {
            var worldFromHead = new Pose(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            var extrinsics = new CameraExtrinsics
            {
                CameraPos = Vector3.forward,
                CameraRot = Quaternion.identity
            };

            bool success = CameraFramePoseUtility.TryComposeCameraPose(
                0,
                0x3,
                worldFromHead,
                true,
                extrinsics,
                true,
                CreateValidIntrinsics(),
                out Pose cameraPose);

            Assert.That(success, Is.True);
            Assert.That(Vector3.Distance(cameraPose.position, Vector3.right), Is.LessThan(0.0001f));
        }

        [Test]
        public void TryComposeCameraPose_RejectsSensorFailure()
        {
            Assert.That(
                TryComposeWith(sensorResult: -1, sensorStatus: 0x3),
                Is.False);
        }

        [TestCase(0x0)]
        [TestCase(0x1)]
        [TestCase(0x2)]
        public void TryComposeCameraPose_RejectsIncompleteSensorStatus(int status)
        {
            Assert.That(TryComposeWith(sensorResult: 0, sensorStatus: status), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsNonFiniteHeadPose()
        {
            Pose worldFromHead = new Pose(
                new Vector3(float.NaN, 0f, 0f),
                Quaternion.identity);

            Assert.That(
                TryComposeWith(worldFromHead: worldFromHead),
                Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsUnavailableExtrinsics()
        {
            Assert.That(TryComposeWith(hasExtrinsics: false), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsInvalidExtrinsicPosition()
        {
            CameraExtrinsics extrinsics = CreateValidExtrinsics();
            extrinsics.CameraPos.x = float.PositiveInfinity;

            Assert.That(TryComposeWith(extrinsics: extrinsics), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsInvalidExtrinsicRotation()
        {
            CameraExtrinsics extrinsics = CreateValidExtrinsics();
            extrinsics.CameraRot = default;

            Assert.That(TryComposeWith(extrinsics: extrinsics), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsUnavailableIntrinsics()
        {
            Assert.That(TryComposeWith(hasIntrinsics: false), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsInvalidFocalLength()
        {
            CameraIntrinsics intrinsics = CreateValidIntrinsics();
            intrinsics.FocalLength.x = 0f;

            Assert.That(TryComposeWith(intrinsics: intrinsics), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsInvalidCornerPoint()
        {
            CameraIntrinsics intrinsics = CreateValidIntrinsics();
            intrinsics.CornerPoint.y = float.NaN;

            Assert.That(TryComposeWith(intrinsics: intrinsics), Is.False);
        }

        [Test]
        public void TryComposeCameraPose_RejectsInvalidResolution()
        {
            CameraIntrinsics intrinsics = CreateValidIntrinsics();
            intrinsics.Resolution.y = 0f;

            Assert.That(TryComposeWith(intrinsics: intrinsics), Is.False);
        }

        private static bool TryComposeWith(
            int sensorResult = 0,
            int sensorStatus = 0x3,
            Pose? worldFromHead = null,
            bool hasExtrinsics = true,
            CameraExtrinsics? extrinsics = null,
            bool hasIntrinsics = true,
            CameraIntrinsics? intrinsics = null)
        {
            return CameraFramePoseUtility.TryComposeCameraPose(
                sensorResult,
                sensorStatus,
                worldFromHead ?? new Pose(Vector3.zero, Quaternion.identity),
                hasExtrinsics,
                extrinsics ?? CreateValidExtrinsics(),
                hasIntrinsics,
                intrinsics ?? CreateValidIntrinsics(),
                out _);
        }

        private static CameraExtrinsics CreateValidExtrinsics()
        {
            return new CameraExtrinsics
            {
                CameraPos = new Vector3(0.1f, 0.2f, 0.3f),
                CameraRot = Quaternion.identity
            };
        }

        private static CameraIntrinsics CreateValidIntrinsics()
        {
            return new CameraIntrinsics
            {
                FocalLength = new Vector2(500f, 500f),
                CornerPoint = new Vector2(320f, 320f),
                Resolution = new Vector2(640f, 640f),
                Fov = new Vector2(90f, 90f)
            };
        }

        private static T FindInScene<T>(Scene scene)
            where T : Component
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                T component = rootObjects[i].GetComponentInChildren<T>(true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static Camera FindMainCamera(Scene scene)
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            Camera fallback = null;
            for (int i = 0; i < rootObjects.Length; i++)
            {
                Camera[] cameras = rootObjects[i].GetComponentsInChildren<Camera>(true);
                for (int j = 0; j < cameras.Length; j++)
                {
                    Camera camera = cameras[j];
                    if (fallback == null)
                    {
                        fallback = camera;
                    }
                    if (camera.CompareTag("MainCamera"))
                    {
                        return camera;
                    }
                }
            }

            return fallback;
        }

        private static CameraFrameGate CreateArmedGate(uint baseline)
        {
            var gate = new CameraFrameGate();
            Assert.That(gate.ObservePlayingState(true), Is.EqualTo(CameraLifecycleTransition.Started));
            gate.ArmBaseline(baseline);
            return gate;
        }

        private static CameraFrameGate CreateCommittedGate(
            uint baseline,
            uint textureUpdateCount,
            double timestamp)
        {
            CameraFrameGate gate = CreateArmedGate(baseline);
            Assert.That(
                gate.Evaluate(textureUpdateCount, timestamp, out _),
                Is.EqualTo(CameraFrameDecision.Ready));
            gate.Commit();
            return gate;
        }
    }
}
