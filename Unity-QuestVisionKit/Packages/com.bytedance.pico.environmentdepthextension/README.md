# PICO Environment Depth Extension (Preview)

> **PREVIEW / NOT FOR PRODUCTION USE** — This package is in early preview and its
> APIs, serialized layout and naming conventions are subject to change without
> notice. Do not rely on it for a production release.

Extensions built on top of the PICO Unity (Swan) Environment Depth capability
(<https://developer-cn.picoxr.com/document/unity-swan/environment-depth/>):
environment depth data access, preview, and depth-based raycasting utilities.
The raycaster uses a texture-space DDA, two-eye selection,
occlusion classification, and 5-tap normal reconstruction.

## Quick start

1. Add `EnvironmentDepthDataAccess` to a GameObject in your XR rig. It resolves a
   depth provider on `Awake` and publishes `DepthFrameData` via the
   `DepthFrameReceived` event.
2. Add `EnvironmentDepthRaycaster` (it requires `EnvironmentDepthDataAccess`) to
   cast rays against the environment depth.

```csharp
var raycaster = GetComponent<EnvironmentDepthRaycaster>();
var ray = new Ray(transform.position, transform.forward);
if (raycaster.Raycast(ray, out EnvironmentRaycastHit hit, maxDistance: 5f))
{
    // hit.Point, hit.Normal, hit.NormalConfidence, hit.Distance are valid.
}
```

`Raycast` returns `true` only when `hit.Status == EnvironmentRaycastHitStatus.Hit`.
Inspect `hit.Status` for the full outcome even when it returns `false`.

## Provider selection

The depth manager publishes global shader properties
(`_EnvironmentDepthTexture`, `_EnvironmentDepthZBufferParams`,
`_EnvironmentDepthReprojectionMatrices`). Reading those globals, availability
detection and the depth-manager lifecycle are all isolated behind
`IDepthSourceProvider`, so `EnvironmentDepthDataAccess` consumes depth without
knowing the backend property names.

`EnvironmentDepthDataAccess` exposes a `Preferred Provider` field:

| Value  | Behavior                                              |
|--------|-------------------------------------------------------|
| `Auto` | Resolve the installed XR backend (`PXR_EnvironmentDepthManager`) if available. |
| `Pico` | Force the PICO-compatible XR backend (`PXR_EnvironmentDepthManager`). |

## Raycast status state machine

```
                 Raycast(ray, out hit, maxDistance)
                              │
              no frame yet ──►├──► NotReady
                              │
       origin outside frustum ├──► HitPointOutsideOfCameraFrustum
        (and never enters it) │
                              │
        per eye (left first): ▼
            ┌──────────── DDA walk ────────────┐
            │                                   │
   crossed empty space then    never saw empty space
   envDepth <= rayDepth?         │
            │                    └──► RayOccluded
   prevEnv - envDepth > 0.3 m ?
        │            │
        yes          no
        ▼            ▼
 HitPointOccluded   Hit
            │
   walked off end without a crossing ──► NoHit
```

Two-eye selection: if the left eye reports `Hit`, it is returned immediately;
otherwise the right eye is tried. If both eyes report `HitPointOccluded`, the
hit observed farther from the ray origin (more environment data) is returned.

`NotSupported` is reserved for backends that report the depth feature as
unsupported.

## Notes

- The legacy `Raycast(Ray, out Vector3 point, out Vector3 normal)` overload is
  `[Obsolete]`; migrate to `Raycast(Ray, out EnvironmentRaycastHit, float)`.
- `EnvironmentDepthDataAccess` enforces a single active instance and emits a
  throttled warning (~0.2 Hz) when no depth source is available.
