using System.Collections.Generic;
using UnityEngine;
using ByteDance.PICO.CameraPack;
using ByteDance.PICO.EnvironmentDepthExtension;
public class QrCodeDisplayManager : MonoBehaviour
{
    private enum QrRaycastMode
    {
        CenterOnly,
        PerCorner
    }
    
    [SerializeField] private QrRaycastMode raycastMode = QrRaycastMode.PerCorner;
    private EnvironmentDepthRaycaster _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();
#if ZXING_ENABLED
    private QrCodeScanner _scanner;
    private void Awake()
    {
        _scanner = GetComponent<QrCodeScanner>();
        _envRaycastManager = GetComponent<EnvironmentDepthRaycaster>();
    }

    private void Update() => RefreshMarkers();

    private async void RefreshMarkers()
    {
        if (!_envRaycastManager || !_scanner)
        {
            return;
        }

        var qrResults = await _scanner.ScanFrameAsync();
        if (qrResults == null || qrResults.Length == 0)
        {
            CleanupInactiveMarkers();
            return;
        }

        foreach (var qrResult in qrResults)
        {
            if (!TryBuildMarkerPose(qrResult, out var pose, out var scale))
            {
                continue;
            }

            var marker = GetOrCreateMarker(qrResult.text);
            if (!marker)
            {
                continue;
            }

            marker.UpdateMarker(pose.position, pose.rotation, scale, qrResult.text);
        }

        CleanupInactiveMarkers();
    }
#endif

    private static Vector2 ToViewport(Vector2 uv) => new(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));

    // Reconstructs the camera-in-world pose from the head pose captured at the
    // SAME moment the frame was grabbed, combined with the camera extrinsics and
    // the 180-degree X flip. This mirrors the official GetCameraPoseInWorld math
    // but is time-locked to capture instead of the live display time, so the
    // overlay no longer lags behind the (several frames old) decoded corners.
    private static Pose BuildCameraPoseInWorld(QrCodeResult result)
    {
        var head = result.cameraPose;
        var extrinsics = result.cameraExtrinsics;

        var position = head.position + head.rotation * extrinsics.CameraPos;
        var rotation = head.rotation * extrinsics.CameraRot * Quaternion.Euler(180f, 0f, 0f);
        return new Pose(position, rotation);
    }

    // Maps a decode-space UV (origin top-left, normalized to the capture
    // resolution) onto a sensor pixel coordinate, then unprojects it through the
    // intrinsics and the capture-time camera pose. Same math as the official
    // ScreenPointToRayInCamera + ViewportPointToRayInWorld pair.
    private static Ray BuildWorldRay(QrCodeResult result, Vector2 uv, Pose cameraPose)
    {
        var viewport = ToViewport(uv);
        var intrinsics = result.Intrinsics;
        var sensorResolution = (Vector2)intrinsics.Resolution;
        var currentResolution = (Vector2)result.captureResolution;
        if (currentResolution == Vector2.zero)
        {
            currentResolution = sensorResolution;
        }

        var crop = ComputeSensorCrop(sensorResolution, currentResolution);
        var sensorPoint = new Vector2(
            crop.x + crop.width * viewport.x,
            crop.y + crop.height * viewport.y);

        var localDirection = new Vector3(
            (sensorPoint.x - intrinsics.CornerPoint.x) / intrinsics.FocalLength.x,
            (sensorPoint.y - intrinsics.CornerPoint.y) / intrinsics.FocalLength.y,
            1f);

        var worldDirection = cameraPose.rotation * localDirection;
        return new Ray(cameraPose.position, worldDirection);
    }

    private static Rect ComputeSensorCrop(Vector2 sensorResolution, Vector2 currentResolution)
    {
        if (sensorResolution == Vector2.zero)
        {
            return new Rect(0, 0, currentResolution.x, currentResolution.y);
        }

        var scaleFactor = new Vector2(
            currentResolution.x / sensorResolution.x,
            currentResolution.y / sensorResolution.y);
        var maxScale = Mathf.Max(scaleFactor.x, scaleFactor.y);
        if (maxScale <= 0)
        {
            maxScale = 1f;
        }
        scaleFactor /= maxScale;

        return new Rect(
            sensorResolution.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolution.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolution.x * scaleFactor.x,
            sensorResolution.y * scaleFactor.y);
    }

    private static Vector3 ProjectOntoPlane(Plane plane, Ray ray, float fallbackDistance)
    {
        return plane.Raycast(ray, out var planeDistance)
            ? ray.GetPoint(planeDistance)
            : ray.GetPoint(fallbackDistance);
    }

    private bool TryBuildMarkerPose(QrCodeResult result, out Pose pose, out Vector3 scale)
    {
        pose = default;
        scale = default;

        if (result?.corners == null || result.corners.Length < 4)
        {
            return false;
        }

        var count = result.corners.Length;
        var uvs = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            uvs[i] = new Vector2(result.corners[i].x, result.corners[i].y);
        }

        var centerUV = Vector2.zero;
        foreach (var uv in uvs)
        {
            centerUV += uv;
        }
        centerUV /= count;

        var cameraPose = BuildCameraPoseInWorld(result);
        var centerRay = BuildWorldRay(result, centerUV, cameraPose);
        if (!_envRaycastManager.Raycast(centerRay, out var centerHit))
        {
            return false;
        }

        var center = centerHit.Point;
        var distance = Vector3.Distance(centerRay.origin, center);
        var plane = new Plane(centerHit.Normal, centerHit.Point);
        var worldCorners = new Vector3[count];

        for (var i = 0; i < count; i++)
        {
            var ray = BuildWorldRay(result, uvs[i], cameraPose);
            if (raycastMode == QrRaycastMode.PerCorner && _envRaycastManager.Raycast(ray, out var cornerHit))
            {
                worldCorners[i] = cornerHit.Point;
            }
            else
            {
                worldCorners[i] = ProjectOntoPlane(plane, ray, distance);
            }
        }

        center = Vector3.zero;
        foreach (var c in worldCorners)
        {
            center += c;
        }
        center /= count;

        var up = (worldCorners[1] - worldCorners[0]).normalized;
        var right = (worldCorners[2] - worldCorners[1]).normalized;
        var normal = -Vector3.Cross(up, right).normalized;
        var rotation = Quaternion.LookRotation(normal, up);

        var width = Vector3.Distance(worldCorners[0], worldCorners[1]);
        var height = Vector3.Distance(worldCorners[0], worldCorners[3]);
        scale = new Vector3(width, height, 1f);
        pose = new Pose(center, rotation);
        return true;
    }

    private MarkerController GetOrCreateMarker(string key)
    {
        if (_activeMarkers.TryGetValue(key, out var marker))
        {
            return marker;
        }

        var markerGo = MarkerPool.Instance ? MarkerPool.Instance.GetMarker() : null;
        if (!markerGo)
        {
            return null;
        }

        marker = markerGo.GetComponent<MarkerController>();
        if (!marker)
        {
            return null;
        }

        _activeMarkers[key] = marker;
        return marker;
    }

    private void CleanupInactiveMarkers()
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in _activeMarkers)
        {
            if (!kvp.Value || !kvp.Value.gameObject.activeSelf)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _activeMarkers.Remove(key);
        }
    }
}
