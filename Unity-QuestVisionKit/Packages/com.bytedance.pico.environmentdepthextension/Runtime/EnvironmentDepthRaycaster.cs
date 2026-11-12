using System;
using UnityEngine;

namespace ByteDance.PICO.EnvironmentDepthExtension
{
    [RequireComponent(typeof(EnvironmentDepthDataAccess))]
    public class EnvironmentDepthRaycaster : MonoBehaviour
    {
        [Header("Occlusion Classification")]
        [SerializeField, Tooltip("Absolute depth discontinuity (meters) above which a detected surface is treated as an occluded hit rather than a direct hit. Also gates edge-aware bilinear taps. Meta default is 0.3.")]
        private float m_occlusionDiscontinuityMeters = 0.3f;
        [SerializeField, Range(0f, 0.25f), Tooltip("Extra discontinuity tolerance proportional to the local depth (meters per meter). 0 keeps the absolute threshold only; raise it so far surfaces are not over-classified as occluded.")]
        private float m_occlusionRelativePerMeter = 0f;

        private EnvironmentDepthDataAccess m_depthAccess;
        private EnvironmentDepthDataAccess.DepthFrameData m_lastFrame;
        private bool m_hasFrame;
        private readonly Matrix4x4[] m_vp = new Matrix4x4[2];
        private readonly Matrix4x4[] m_invVp = new Matrix4x4[2];
        private readonly Plane[][] m_camFrustumPlanes = { new Plane[6], new Plane[6] };

        public bool IsDataReady => m_hasFrame && m_lastFrame.DepthMeters.IsCreated && m_lastFrame.TextureSize > 0;

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

        /// <summary>
        /// Casts a ray against the environment depth and returns a rich hit result.
        /// Returns true only when <see cref="EnvironmentRaycastHitStatus.Hit"/>.
        /// </summary>
        public bool Raycast(Ray ray, out EnvironmentRaycastHit hit, float maxDistance = 5f)
        {
            hit = default;

            if (!IsDataReady)
            {
                hit.Status = EnvironmentRaycastHitStatus.NotReady;
                return false;
            }

            float dirMag = ray.direction.magnitude;
            if (dirMag < 1e-6f || maxDistance <= 0.01f)
            {
                hit.Status = EnvironmentRaycastHitStatus.NoHit;
                return false;
            }

            ray.direction /= dirMag;
            var status = RaycastBothEyes(ray, maxDistance, out hit);
            hit.Status = status;
            return status == EnvironmentRaycastHitStatus.Hit;
        }

        [Obsolete("Use Raycast(Ray, out EnvironmentRaycastHit, float).")]
        public bool Raycast(Ray ray, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = default;
            if (!Raycast(ray, out var hit, 5f))
            {
                return false;
            }

            point = hit.Point;
            normal = hit.Normal;
            return true;
        }

        // Double-eye selection, aligned with Meta GetRaycastResultForEye.
        private EnvironmentRaycastHitStatus RaycastBothEyes(Ray ray, float maxDistance, out EnvironmentRaycastHit hit)
        {
            var (statusL, hitL) = RaycastEyeWithClamp(ray, 0, maxDistance);
            if (statusL == EnvironmentRaycastHitStatus.Hit)
            {
                hit = hitL;
                return EnvironmentRaycastHitStatus.Hit;
            }

            var (statusR, hitR) = RaycastEyeWithClamp(ray, 1, maxDistance);
            if (statusR == EnvironmentRaycastHitStatus.Hit)
            {
                hit = hitR;
                return EnvironmentRaycastHitStatus.Hit;
            }

            // Both occluded: keep the one observed farther from origin (more data).
            if (statusL == EnvironmentRaycastHitStatus.HitPointOccluded &&
                statusR == EnvironmentRaycastHitStatus.HitPointOccluded)
            {
                hit = Vector3.Distance(ray.origin, hitL.Point) > Vector3.Distance(ray.origin, hitR.Point) ? hitL : hitR;
                return EnvironmentRaycastHitStatus.HitPointOccluded;
            }

            hit = hitL;
            return statusL;
        }

        private (EnvironmentRaycastHitStatus, EnvironmentRaycastHit) RaycastEyeWithClamp(Ray ray, int eyeIndex, float maxDistance)
        {
            var hit = new EnvironmentRaycastHit();

            // Clamp the origin into the eye frustum; skip the eye entirely if the
            // ray never enters it.
            if (!ClampRayOriginToCamFrustumPlanes(ref ray, m_camFrustumPlanes[eyeIndex], ref maxDistance))
            {
                return (EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum, hit);
            }

            // When casting toward the head (opposite the near plane normal) clamp
            // maxDistance to the near plane; otherwise rayEnd produces negative
            // depth that breaks the 1/depth interpolation.
            var nearPlane = m_camFrustumPlanes[eyeIndex][4];
            bool isOppositeDir = Vector3.Dot(ray.direction, nearPlane.normal) < 0f;
            if (isOppositeDir && nearPlane.Raycast(ray, out float distToNearPlane) && maxDistance > distToNearPlane)
            {
                maxDistance = distToNearPlane;
            }

            var status = RaycastEyeDda(ray, eyeIndex, maxDistance, out var position);
            hit.Point = position;
            hit.Distance = Vector3.Distance(ray.origin, position);

            if (status == EnvironmentRaycastHitStatus.Hit || status == EnvironmentRaycastHitStatus.HitPointOccluded)
            {
                if (TryReconstructNormalAtWorldPos(position, eyeIndex, out var normal, out var confidence))
                {
                    hit.Normal = normal;
                    hit.NormalConfidence = confidence;
                }
                else
                {
                    hit.Normal = -ray.direction;
                    hit.NormalConfidence = 0f;
                }
            }

            return (status, hit);
        }

        // Texture-major DDA + 1/depth interpolation, aligned with Meta RaycastInternal.
        private EnvironmentRaycastHitStatus RaycastEyeDda(Ray ray, int eyeIndex, float maxDistance, out Vector3 position)
        {
            position = default;

            var rayOrigin = ray.origin;
            var rayDir = ray.direction;
            var rayEnd = rayOrigin + maxDistance * rayDir;

            var p0 = WorldPosToTexCoord(rayOrigin, eyeIndex);
            var p1 = WorldPosToTexCoord(rayEnd, eyeIndex);
            int lengthX = p1.x - p0.x;
            int lengthY = p1.y - p0.y;
            int maxSideLength = Mathf.Max(Mathf.Abs(lengthX), Mathf.Abs(lengthY));

            if (maxSideLength == 0)
            {
                float envDepthSame = SampleDepthBilinear(new Vector2(p0.x, p0.y), eyeIndex);
                if (envDepthSame < maxDistance &&
                    WorldPosToLinearDepth(rayOrigin, eyeIndex) < envDepthSame &&
                    WorldPosToLinearDepth(rayEnd, eyeIndex) > envDepthSame)
                {
                    position = rayOrigin + rayDir * envDepthSame;
                    return EnvironmentRaycastHitStatus.Hit;
                }
                return EnvironmentRaycastHitStatus.NoHit;
            }

            float invDepthStart = 1f / WorldPosToLinearDepth(rayOrigin, eyeIndex);
            float invDepthEnd = 1f / WorldPosToLinearDepth(rayEnd, eyeIndex);
            float deltaX = (float)lengthX / maxSideLength;
            float deltaY = (float)lengthY / maxSideLength;
            float invRayDepthDelta = (invDepthEnd - invDepthStart) / maxSideLength;

            float currentX = p0.x;
            float currentY = p0.y;
            float invRayDepth = invDepthStart;
            bool foundEmptySpace = false;

            // Previous step's sampled segment, used to solve the sub-texel zero
            // crossing of f = rayDepth - envDepth. Only trusted (havePrev) when the
            // prior sample carried valid (non-hole) depth.
            var prevCoord = new Vector2(currentX, currentY);
            float prevRayDepth = 1f / invRayDepth;
            float prevEnvDepth = 0f;
            bool havePrev = false;

            for (int i = 0; i <= maxSideLength; i++)
            {
                var texCoord = new Vector2Int((int)currentX, (int)currentY);
                if (!IsInBounds(texCoord))
                {
                    return EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum;
                }

                var coordF = new Vector2(currentX, currentY);
                float envDepth = SampleDepthBilinear(coordF, eyeIndex);
                float rayDepth = 1f / invRayDepth;
                if (envDepth != 0f)
                {
                    if (!foundEmptySpace)
                    {
                        foundEmptySpace = envDepth > rayDepth;
                    }
                    else if (envDepth <= rayDepth)
                    {
                        // Linearly interpolate the crossing fraction t in [0,1] where
                        // the ray depth meets the surface depth between prev and cur.
                        float fPrev = prevRayDepth - prevEnvDepth;
                        float fCur = rayDepth - envDepth;
                        float denom = fCur - fPrev;
                        float t = (!havePrev || Mathf.Abs(denom) < 1e-6f)
                            ? 1f
                            : Mathf.Clamp01(-fPrev / denom);

                        var crossCoord = Vector2.Lerp(prevCoord, coordF, t);
                        float crossEnvDepth = Mathf.Lerp(prevEnvDepth, envDepth, t);
                        var surfacePoint = WorldPosAtCoord(crossCoord, crossEnvDepth, eyeIndex);

                        // Keep the result exactly on the ray (the interpolated
                        // surface point can drift off it by sub-texel amounts).
                        float along = Vector3.Dot(surfacePoint - rayOrigin, rayDir);
                        position = rayOrigin + rayDir * Mathf.Max(0f, along);

                        // Classify occlusion on raw nearest samples so a real depth
                        // discontinuity stays sharp (bilinear would soften it).
                        var prevTexCoord = new Vector2Int((int)(currentX - deltaX), (int)(currentY - deltaY));
                        float prevEnvRaw = SampleDepthTexel(prevTexCoord, eyeIndex);
                        float envRaw = SampleDepthTexel(texCoord, eyeIndex);
                        return (prevEnvRaw - envRaw > OcclusionThreshold(envRaw))
                            ? EnvironmentRaycastHitStatus.HitPointOccluded
                            : EnvironmentRaycastHitStatus.Hit;
                    }
                }

                prevCoord = coordF;
                prevRayDepth = rayDepth;
                prevEnvDepth = envDepth;
                havePrev = envDepth != 0f;

                currentX += deltaX;
                currentY += deltaY;
                invRayDepth += invRayDepthDelta;
            }

            // Never saw empty space -> origin was occluded the whole way.
            return foundEmptySpace ? EnvironmentRaycastHitStatus.NoHit : EnvironmentRaycastHitStatus.RayOccluded;
        }

        private static bool ClampRayOriginToCamFrustumPlanes(ref Ray ray, Plane[] planes, ref float maxDistance)
        {
            if (GeometryUtility.TestPlanesAABB(planes, new Bounds(ray.origin, Vector3.zero)))
            {
                return true;
            }

            // Skip the far plane at index 5.
            for (int i = 0; i < 5; i++)
            {
                if (planes[i].Raycast(ray, out float dist))
                {
                    const float tolerance = 0.01f;
                    if (GeometryUtility.TestPlanesAABB(planes, new Bounds(ray.GetPoint(dist + tolerance), Vector3.zero)))
                    {
                        maxDistance -= dist;
                        if (maxDistance <= 0f)
                        {
                            return false;
                        }

                        ray.origin = ray.GetPoint(dist);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInBounds(Vector2Int texCoord)
        {
            int size = m_lastFrame.TextureSize;
            return texCoord.x >= 0 && texCoord.x < size && texCoord.y >= 0 && texCoord.y < size;
        }

        private Vector2Int WorldPosToTexCoord(Vector3 worldPos, int eyeIndex)
        {
            int size = m_lastFrame.TextureSize;
            var clipPos = m_vp[eyeIndex] * new Vector4(worldPos.x, worldPos.y, worldPos.z, 1f);
            float invW = Mathf.Abs(clipPos.w) < 1e-9f ? 0f : 1f / clipPos.w;
            var uv = (new Vector2(clipPos.x, clipPos.y) * invW + Vector2.one) * 0.5f;
            return new Vector2Int(
                Mathf.Clamp((int)(uv.x * size), 0, size - 1),
                Mathf.Clamp((int)(uv.y * size), 0, size - 1));
        }

        private float SampleDepthTexel(Vector2Int texCoord, int eyeIndex)
        {
            int size = m_lastFrame.TextureSize;
            int index = texCoord.x + texCoord.y * size + size * size * eyeIndex;
            if (index < 0 || index >= m_lastFrame.DepthMeters.Length)
            {
                return 0f;
            }

            float v = m_lastFrame.DepthMeters[index];
            return (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v;
        }

        // Edge-aware bilinear depth sample in index space (integer coordinate ==
        // texel center). Holes (0) and taps farther than the occlusion threshold
        // from the dominant (nearest) tap are dropped and the remaining weights
        // renormalized, so a real depth discontinuity stays sharp instead of
        // bleeding a blurred edge into the hit point.
        private float SampleDepthBilinear(Vector2 coord, int eyeIndex)
        {
            int x0 = Mathf.FloorToInt(coord.x);
            int y0 = Mathf.FloorToInt(coord.y);
            float tx = coord.x - x0;
            float ty = coord.y - y0;

            float d00 = SampleDepthTexel(new Vector2Int(x0, y0), eyeIndex);
            float d10 = SampleDepthTexel(new Vector2Int(x0 + 1, y0), eyeIndex);
            float d01 = SampleDepthTexel(new Vector2Int(x0, y0 + 1), eyeIndex);
            float d11 = SampleDepthTexel(new Vector2Int(x0 + 1, y0 + 1), eyeIndex);

            float reference = tx < 0.5f
                ? (ty < 0.5f ? d00 : d01)
                : (ty < 0.5f ? d10 : d11);
            if (reference == 0f)
            {
                reference = d00 != 0f ? d00 : d10 != 0f ? d10 : d01 != 0f ? d01 : d11;
            }
            if (reference == 0f)
            {
                return 0f;
            }

            float tol = OcclusionThreshold(reference);
            float sum = 0f;
            float wsum = 0f;
            AccumulateTap(d00, (1f - tx) * (1f - ty), reference, tol, ref sum, ref wsum);
            AccumulateTap(d10, tx * (1f - ty), reference, tol, ref sum, ref wsum);
            AccumulateTap(d01, (1f - tx) * ty, reference, tol, ref sum, ref wsum);
            AccumulateTap(d11, tx * ty, reference, tol, ref sum, ref wsum);

            return wsum > 1e-6f ? sum / wsum : reference;
        }

        private static void AccumulateTap(float depth, float weight, float reference, float tolerance, ref float sum, ref float weightSum)
        {
            if (depth == 0f || Mathf.Abs(depth - reference) > tolerance)
            {
                return;
            }

            sum += depth * weight;
            weightSum += weight;
        }

        // Depth discontinuity tolerance (meters) at a given local depth: a fixed
        // term plus an optional component proportional to depth.
        private float OcclusionThreshold(float depthMeters)
        {
            return m_occlusionDiscontinuityMeters + m_occlusionRelativePerMeter * Mathf.Abs(depthMeters);
        }

        private Vector3 WorldPosAtTexCoord(Vector2Int texCoord, int eyeIndex)
        {
            return WorldPosAtCoord(new Vector2(texCoord.x, texCoord.y), SampleDepthTexel(texCoord, eyeIndex), eyeIndex);
        }

        // Reconstructs the world position for a (possibly fractional) index-space
        // coordinate and an explicit linear depth. Uses the texel-center
        // convention ((coord + 0.5) / size) so it round-trips with the floor used
        // by WorldPosToTexCoord without a half-texel bias.
        private Vector3 WorldPosAtCoord(Vector2 coord, float linearDepth, int eyeIndex)
        {
            int size = m_lastFrame.TextureSize;
            var zParams = m_lastFrame.ZBufferParams;
            float clipSpaceDepth = linearDepth == 0f ? 0f : zParams.x / linearDepth - zParams.y;
            float oneOverSize = 1f / size;
            var clipPos = new Vector4(
                (coord.x + 0.5f) * oneOverSize * 2f - 1f,
                (coord.y + 0.5f) * oneOverSize * 2f - 1f,
                clipSpaceDepth, 1f);
            var world = m_invVp[eyeIndex] * clipPos;
            float invW = Mathf.Abs(world.w) < 1e-9f ? 0f : 1f / world.w;
            return new Vector3(world.x * invW, world.y * invW, world.z * invW);
        }

        private float WorldPosToLinearDepth(Vector3 worldPos, int eyeIndex)
        {
            var zParams = m_lastFrame.ZBufferParams;
            var clipPos = m_vp[eyeIndex] * new Vector4(worldPos.x, worldPos.y, worldPos.z, 1f);
            if (Mathf.Abs(clipPos.w) < 1e-9f)
            {
                return 0.0001f;
            }

            float ndcZ = clipPos.z / clipPos.w;
            float denom = ndcZ + zParams.y;
            if (Mathf.Abs(denom) < 1e-6f)
            {
                return 0.0001f;
            }

            return (1f / denom) * zParams.x;
        }

        // 5-tap normal reconstruction with confidence (atyuwen / Meta).
        private bool TryReconstructNormalAtWorldPos(Vector3 position, int eyeIndex, out Vector3 normal, out float normalConfidence)
        {
            normal = default;
            normalConfidence = 0f;
            const int filterOffset = 2;
            const int filterSize = filterOffset + 2;

            var texCoord = WorldPosToTexCoord(position, eyeIndex);
            int size = m_lastFrame.TextureSize;
            if (texCoord.x < filterSize || texCoord.x >= size - filterSize ||
                texCoord.y < filterSize || texCoord.y >= size - filterSize)
            {
                return false;
            }

            Span<Vector2Int> offsets = stackalloc Vector2Int[]
            {
                new Vector2Int(-filterOffset, 0),
                new Vector2Int(filterOffset, 0),
                new Vector2Int(0, 0),
                new Vector2Int(0, -filterOffset),
                new Vector2Int(0, filterOffset),
            };
            Span<Vector3> normals = stackalloc Vector3[offsets.Length];

            var avgNormal = Vector3.zero;
            for (int i = 0; i < offsets.Length; i++)
            {
                var n = ReconstructNormal(texCoord + offsets[i], eyeIndex);
                normals[i] = n;
                avgNormal += n;
            }

            avgNormal = Vector3.Normalize(avgNormal);
            if (avgNormal.sqrMagnitude < 1e-6f)
            {
                return false;
            }

            normal = avgNormal;
            float filtered = 0f;
            for (int i = 0; i < offsets.Length; i++)
            {
                if (Vector3.Dot(normals[i], avgNormal) > 0.95f)
                {
                    filtered++;
                }
            }

            normalConfidence = filtered / offsets.Length;
            return true;
        }

        private Vector3 ReconstructNormal(Vector2Int texCoord, int eyeIndex)
        {
            float centerDepth = SampleDepthTexel(texCoord, eyeIndex);
            var centerWorldPos = WorldPosAtTexCoord(texCoord, eyeIndex);
            var horDeriv = ClosestDerivative(new Vector2Int(1, 0));
            var verDeriv = ClosestDerivative(new Vector2Int(0, 1));
            // Flip the sign because Unity uses a left-handed coordinate system.
            return -Vector3.Normalize(Vector3.Cross(horDeriv, verDeriv));

            Vector3 ClosestDerivative(Vector2Int axis)
            {
                var offsets = new Vector4(
                    SampleDepthTexel(texCoord - axis, eyeIndex),
                    SampleDepthTexel(texCoord + axis, eyeIndex),
                    SampleDepthTexel(texCoord - axis * 2, eyeIndex),
                    SampleDepthTexel(texCoord + axis * 2, eyeIndex));
                var extrapolations = new Vector2(
                    Mathf.Abs(offsets.x * offsets.z / (2 * offsets.z - offsets.x) - centerDepth),
                    Mathf.Abs(offsets.y * offsets.w / (2 * offsets.w - offsets.y) - centerDepth));
                return extrapolations.x > extrapolations.y
                    ? WorldPosAtTexCoord(texCoord + axis, eyeIndex) - centerWorldPos
                    : centerWorldPos - WorldPosAtTexCoord(texCoord - axis, eyeIndex);
            }
        }

        private void HandleDepthFrameReceived(EnvironmentDepthDataAccess.DepthFrameData frame)
        {
            m_lastFrame = frame;
            m_hasFrame = true;

            // Per-eye view-projection matrices (submit-time snapshot, by value).
            m_vp[0] = frame.ViewProjLeft;
            m_vp[1] = frame.ViewProjRight;

            for (int i = 0; i < 2; i++)
            {
                m_invVp[i] = m_vp[i].inverse;
                GeometryUtility.CalculateFrustumPlanes(m_vp[i], m_camFrustumPlanes[i]);
            }
        }
    }
}