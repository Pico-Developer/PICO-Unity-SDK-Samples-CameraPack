using ByteDance.PICO.CameraPack;
using UnityEngine;

namespace PICO.Samples.HandJointsOverlay
{
    public static class CameraFramePoseUtility
    {
        private const int k_requiredSensorStatus = 0x3;
        private const float k_minQuaternionMagnitudeSqr = 0.5f;
        private static readonly Quaternion k_cameraRotationAdjustment =
            Quaternion.Euler(180f, 0f, 0f);

        public static bool TryComposeCameraPose(
            int sensorResult,
            int sensorStatus,
            Pose worldFromHead,
            bool hasExtrinsics,
            CameraExtrinsics extrinsics,
            bool hasIntrinsics,
            CameraIntrinsics intrinsics,
            out Pose cameraPose)
        {
            cameraPose = default;
            if (sensorResult != 0 ||
                (sensorStatus & k_requiredSensorStatus) != k_requiredSensorStatus ||
                !IsValidPose(worldFromHead) ||
                !hasExtrinsics ||
                !IsFinite(extrinsics.CameraPos) ||
                !IsValidQuaternion(extrinsics.CameraRot) ||
                !hasIntrinsics ||
                !IsValidIntrinsics(intrinsics))
            {
                return false;
            }

            cameraPose.position =
                worldFromHead.position +
                worldFromHead.rotation * extrinsics.CameraPos;
            cameraPose.rotation =
                worldFromHead.rotation *
                extrinsics.CameraRot *
                k_cameraRotationAdjustment;

            return IsValidPose(cameraPose);
        }

        private static bool IsValidIntrinsics(CameraIntrinsics intrinsics)
        {
            return IsFinite(intrinsics.FocalLength) &&
                   intrinsics.FocalLength.x > 0f &&
                   intrinsics.FocalLength.y > 0f &&
                   IsFinite(intrinsics.CornerPoint) &&
                   IsFinite(intrinsics.Resolution) &&
                   intrinsics.Resolution.x > 0f &&
                   intrinsics.Resolution.y > 0f;
        }

        private static bool IsValidPose(Pose pose)
        {
            return IsFinite(pose.position) &&
                   IsValidQuaternion(pose.rotation);
        }

        private static bool IsValidQuaternion(Quaternion rotation)
        {
            if (!IsFinite(rotation.x) ||
                !IsFinite(rotation.y) ||
                !IsFinite(rotation.z) ||
                !IsFinite(rotation.w))
            {
                return false;
            }

            float magnitudeSqr =
                rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w;
            return magnitudeSqr > k_minQuaternionMagnitudeSqr;
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) &&
                   IsFinite(value.y) &&
                   IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
