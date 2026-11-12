# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.0] - 2026-06-25

Environment raycast hit-point precision improvements (`EnvironmentDepthRaycaster`).

### Added
- Edge-aware bilinear depth sampling (`SampleDepthBilinear`) along the DDA walk:
  the four taps are weighted, with holes (0) and taps beyond the occlusion
  threshold from the dominant tap dropped and the remaining weights
  renormalized, so smooth surfaces interpolate while real depth discontinuities
  stay sharp.
- Sub-texel crossing interpolation in the DDA: the exact zero crossing of
  `rayDepth - envDepth` between the previous and current step is solved and used
  to reconstruct the hit point (projected back onto the ray), replacing the
  integer-texel `ClosestPointOnFirstRay` segment closest-point.
- Configurable occlusion classification: `m_occlusionDiscontinuityMeters`
  (serialized, default 0.3) plus an optional depth-proportional
  `m_occlusionRelativePerMeter`, replacing the fixed
  `k_occlusionDiscontinuityMeters` constant.

### Changed
- Package renamed from `com.camerapack.envdepthtools`
  (`CameraPack.EnvironmentDepthTools`) to
  `com.bytedance.pico.environmentdepthextension`
  (`ByteDance.PICO.EnvironmentDepthExtension`) to reflect that it extends the
  PICO Unity (Swan) Environment Depth capability. The assembly, root namespace,
  folder and displayName are updated accordingly; script `.meta` GUIDs are
  preserved so existing scene/prefab references stay intact.
- World-position reconstruction now uses a texel-center convention
  (`(coord + 0.5) / size`) via the new fractional `WorldPosAtCoord`, removing a
  systematic half-texel bias between `WorldPosToTexCoord` (floor) and the
  back-projection used for hit points and normals.
- Occlusion `Hit` / `HitPointOccluded` classification is evaluated on raw
  nearest-neighbour samples so bilinear smoothing does not soften the
  discontinuity decision.

### Removed
- `ClosestPointOnFirstRay` (superseded by sub-texel crossing interpolation).

## [0.4.0] - 2026-06-15

### Removed
- GPU depth preview feature: `EnvironmentDepthGpuPreviewSample`, the
  `CameraPack/EnvironmentDepth/Preview` and `Preview_URP` overlay shaders, the
  `PICOGPUMaterial` resource, and the `Depth GPU Preview` sample scene.
- Orphaned public API with no callers: `EnvironmentDepthDataAccess
  .TryGetDepthTexture` and `EnvironmentDepthRaycaster.TryComputeNearFar`
  (`EnvironmentDepthPreview` keeps its own private near/far helper).
- Dead public member `EnvironmentDepthDataAccess.IsInitialized` (never read by
  any consumer) and the unused `ByteDance.PICO.CameraPack` assembly reference.

### Changed
- `EnvironmentDepthDataAccess.TextureSize` is now the private `m_textureSize`;
  consumers already read the per-frame `DepthFrameData.TextureSize`.
- Sample folder/scene renamed from `DepthPreviewRaycast_PICO` to
  `DepthPreviewRaycast`; the sample is registered without a vendor suffix.
- Documentation (`README.md`) and `package.json` samples/description now use
  vendor-neutral language (XR device/XR SDK descriptions updated to vendor-neutral language).
- Package manifest and README now label the package as a **(Preview)** / **NOT FOR
  PRODUCTION USE** release, and the displayName is suffixed with `(Preview)`.
  APIs and serialized layout are documented as subject to change.

## [0.3.0] - 2026-06-15

Refactor around a single supported XR backend.

### Removed
- Meta XR SDK support: `MetaDepthSourceProvider`, the `PreferredProvider.Meta`
  and `EnvironmentDepthGpuPreviewSample.Backend.Meta` options, the
  `Meta.XR.EnvironmentDepth` / `meta.xr.mrutilitykit` assembly references, the
  Meta sample scenes (`DepthPreviewRaycast_Meta`, `DepthGpuPreview_Meta`), and
  the `MetaGPUMaterial` resource.

### Changed
- `DepthSourceProviderFactory.Resolve` always resolves to the active XR depth manager; both
  `Auto` and `Pico` resolve to `PicoDepthSourceProvider`.
- Documentation and `package.json` description/samples updated to vendor-neutral
  device.

## [0.2.0] - 2026-06-15

Meta MRUK alignment for environment depth raycasting (Spec-Id: envdepthtools-meta-alignment).

### Added
- (R4) Frustum origin clamping (`ClampRayOriginToCamFrustumPlanes`) using
  `GeometryUtility.TestPlanesAABB`, including a near-plane `maxDistance` clamp so
  rays cast toward the head no longer fail on negative depth.
- (R6) 5-tap normal reconstruction with a `NormalConfidence` output.
- (R7) New rich raycast API `Raycast(Ray, out EnvironmentRaycastHit, float)` plus
  the `EnvironmentRaycastHit` value type and 7-state `EnvironmentRaycastHitStatus`
  enum (names aligned with Meta).
- (R8) Backend-agnostic `IDepthSourceProvider` abstraction with
  `PicoDepthSourceProvider`, `MetaDepthSourceProvider` (reflection-loaded), a
  shared `GlobalDepthSourceProviderBase`, and `DepthSourceProviderFactory`
  (`Auto` priority: the installed XR backend, then Meta).
- (R10) Throttled (~0.2 Hz) diagnostics warning when the depth source is
  unavailable.

### Changed
- (R5) Replaced the world-space uniform-step raycast with a texture-major DDA
  walk + `1/depth` interpolation, plus two-eye selection (left-eye-first), and
  occlusion classification driven by a 0.3 m depth-discontinuity threshold
  (`k_occlusionDiscontinuityMeters`) that distinguishes `Hit` /
  `HitPointOccluded` / `RayOccluded`.
- (R2) `EnvironmentDepthDataAccess` now snapshots the submit-time view-projection
  matrices / `ZBufferParams` (`PendingMeta`) with a single in-flight readback and
  double buffering, eliminating per-frame `NativeArray` allocations and matrix
  time-skew. `DepthFrameData` gains `ViewProjLeft` / `ViewProjRight` (by value).
- (R8.3) Removed scattered `#if ENABLE_PICO_XR_SDK` from business code; the
  backend conditional now lives only under `Runtime/Providers/`.
- (R10) `EnvironmentDepthDataAccess` enforces a single active instance via
  `s_instance`.

### Fixed
- (R1) ComputeShader dispatch now covers the whole texture
  (`Dispatch(kernel, ceil(N/8), ceil(N/8), 1)`); the previous Meta-path
  `Dispatch(1,1,1)` only filled the top-left 8x8 block.
- (R3) Added `denom` guards (`abs(denom) < 1e-6`) and NaN/Inf guards in the
  RenderTexture readback fallback and depth-to-linear conversions.

### Deprecated
- (R7) `Raycast(Ray, out Vector3 point, out Vector3 normal)` is now
  `[Obsolete]`; use `Raycast(Ray, out EnvironmentRaycastHit, float)`.

## [0.1.0]

- Initial release: environment depth data access, preview, and depth-based
  raycasting utilities for XR devices.
