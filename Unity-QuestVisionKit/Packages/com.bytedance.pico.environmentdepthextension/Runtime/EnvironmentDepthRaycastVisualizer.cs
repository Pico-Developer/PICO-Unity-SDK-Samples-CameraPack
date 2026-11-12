using UnityEngine;

namespace ByteDance.PICO.EnvironmentDepthExtension
{
    // Drives the environment depth raycast process and renders the result (ray line
    // and surface hit disc). It actively calls EnvironmentDepthRaycaster each frame
    // based on its own ray configuration.
    [RequireComponent(typeof(EnvironmentDepthRaycaster))]
    public class EnvironmentDepthRaycastVisualizer : MonoBehaviour
    {
        [Header("Raycast Settings")]
        [SerializeField, Tooltip("Transform defining the ray origin and direction. If null, this transform is used.")]
        private Transform m_rayOrigin;
        [SerializeField] private float m_maxDistance = 5f;
        [SerializeField] private int m_logEveryNFrames = 0;

        [Header("Line")]
        [SerializeField] private LineRenderer m_lineRenderer;
        [SerializeField] private bool m_drawHit = true;

        [Header("Hit Disc")]
        [SerializeField] private bool m_showHitDisc = true;
        [SerializeField, Range(0.005f, 0.5f)] private float m_hitDiscRadius = 0.03f;
        [SerializeField, Range(0f, 0.02f)] private float m_hitDiscSurfaceOffset = 0.002f;
        [SerializeField, Range(8, 96)] private int m_hitDiscSegments = 32;
        [SerializeField] private Material m_hitDiscMaterial;
        [SerializeField] private Color m_hitDiscColor = new(1f, 0.9f, 0.1f, 0.9f);

        private EnvironmentDepthRaycaster m_raycaster;
        private Transform m_hitDiscTransform;
        private MeshRenderer m_hitDiscRenderer;
        private MeshFilter m_hitDiscMeshFilter;
        private MaterialPropertyBlock m_hitDiscPropertyBlock;
        private int m_hitDiscColorPropertyId;
        private Mesh m_runtimeHitDiscMesh;
        private Material m_runtimeHitDiscMaterial;

        private int m_frameCount;

        private void Awake()
        {
            m_raycaster = GetComponent<EnvironmentDepthRaycaster>();
            if (!m_lineRenderer)
            {
                m_lineRenderer = GetComponent<LineRenderer>();
            }
            EnsureHitDisc();
        }

        private void OnDestroy()
        {
            if (m_runtimeHitDiscMaterial)
            {
                Destroy(m_runtimeHitDiscMaterial);
            }
            if (m_runtimeHitDiscMesh)
            {
                Destroy(m_runtimeHitDiscMesh);
            }
            if (m_hitDiscTransform)
            {
                Destroy(m_hitDiscTransform.gameObject);
            }
        }

        private void Update()
        {
            if (!m_raycaster) return;

            Transform origin = m_rayOrigin ? m_rayOrigin : transform;
            Ray ray = new Ray(origin.position, origin.forward);

            bool isHit = m_raycaster.Raycast(ray, out var hit, m_maxDistance);

            if (m_logEveryNFrames > 0 && ++m_frameCount >= m_logEveryNFrames)
            {
                m_frameCount = 0;
                Debug.Log($"[DepthRaycastVisualizer] status={hit.Status} dist={hit.Distance:F2} pos={hit.Point} norm={hit.Normal} conf={hit.NormalConfidence:F2}", this);
            }

            Vector3 lineEnd = isHit ? hit.Point : ray.GetPoint(m_maxDistance);
            SetLine(ray.origin, lineEnd);

            if (m_showHitDisc && m_raycaster.IsDataReady && isHit)
            {
                UpdateHitDiscAt(ray, hit.Point, hit.Normal);
            }
            else
            {
                SetHitDiscVisible(false);
            }
        }

        private void EnsureHitDisc()
        {
            if (!m_showHitDisc)
            {
                return;
            }

            if (m_hitDiscTransform)
            {
                return;
            }

            var go = new GameObject("HitDisc");
            go.transform.SetParent(transform, false);
            m_hitDiscTransform = go.transform;

            m_hitDiscMeshFilter = go.AddComponent<MeshFilter>();
            m_hitDiscRenderer = go.AddComponent<MeshRenderer>();

            m_runtimeHitDiscMesh = CreateDiscMesh(Mathf.Clamp(m_hitDiscSegments, 8, 96));
            m_hitDiscMeshFilter.sharedMesh = m_runtimeHitDiscMesh;
            m_hitDiscRenderer.sharedMaterial = ResolveHitDiscMaterial();
            m_hitDiscPropertyBlock = new MaterialPropertyBlock();
            m_hitDiscColorPropertyId = Shader.PropertyToID("_BaseColor");
            if (!m_hitDiscRenderer.sharedMaterial || !m_hitDiscRenderer.sharedMaterial.HasProperty(m_hitDiscColorPropertyId))
            {
                m_hitDiscColorPropertyId = Shader.PropertyToID("_Color");
            }
            SetHitDiscVisible(false);
        }

        private Material ResolveHitDiscMaterial()
        {
            if (m_hitDiscMaterial)
            {
                return m_hitDiscMaterial;
            }

            if (m_lineRenderer && m_lineRenderer.sharedMaterial)
            {
                return m_lineRenderer.sharedMaterial;
            }

            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");

            if (!shader)
            {
                return null;
            }

            m_runtimeHitDiscMaterial = new Material(shader);
            return m_runtimeHitDiscMaterial;
        }

        private static Mesh CreateDiscMesh(int segments)
        {
            var mesh = new Mesh();
            int vertexCount = segments + 1;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            normals[0] = Vector3.forward;
            uvs[0] = new Vector2(0.5f, 0.5f);

            float step = Mathf.PI * 2f / segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i * step;
                float x = Mathf.Cos(a) * 0.5f;
                float y = Mathf.Sin(a) * 0.5f;
                int vi = i + 1;
                vertices[vi] = new Vector3(x, y, 0f);
                normals[vi] = Vector3.forward;
                uvs[vi] = new Vector2(x + 0.5f, y + 0.5f);

                int ti = i * 3;
                triangles[ti + 0] = 0;
                triangles[ti + 1] = vi;
                triangles[ti + 2] = (i == segments - 1) ? 1 : (vi + 1);
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private void SetHitDiscVisible(bool visible)
        {
            if (m_hitDiscRenderer)
            {
                m_hitDiscRenderer.enabled = visible;
            }
        }

        // Places the hit disc at a world position with a precomputed normal
        // (from the Meta-aligned raycast). Falls back to facing the ray when the
        // supplied normal is degenerate.
        private void UpdateHitDiscAt(Ray ray, Vector3 position, Vector3 normal)
        {
            EnsureHitDisc();
            if (!m_hitDiscTransform || !m_hitDiscRenderer)
            {
                return;
            }

            if (normal.sqrMagnitude < 1e-6f)
            {
                normal = -ray.direction.normalized;
            }

            if (Vector3.Dot(normal, ray.direction) > 0f)
            {
                normal = -normal;
            }

            m_hitDiscTransform.position = position + normal * m_hitDiscSurfaceOffset;
            m_hitDiscTransform.rotation = Quaternion.LookRotation(normal);
            float diameter = Mathf.Max(0.001f, m_hitDiscRadius * 2f);
            m_hitDiscTransform.localScale = new Vector3(diameter, diameter, 1f);

            var mat = m_hitDiscRenderer.sharedMaterial;
            if (mat && m_hitDiscPropertyBlock != null)
            {
                m_hitDiscRenderer.GetPropertyBlock(m_hitDiscPropertyBlock);
                m_hitDiscPropertyBlock.SetColor(m_hitDiscColorPropertyId, m_hitDiscColor);
                m_hitDiscRenderer.SetPropertyBlock(m_hitDiscPropertyBlock);
            }
            SetHitDiscVisible(true);
        }

        private void SetLine(Vector3 a, Vector3 b)
        {
            if (!m_lineRenderer)
            {
                return;
            }

            m_lineRenderer.enabled = m_drawHit;
            m_lineRenderer.useWorldSpace = true;
            m_lineRenderer.positionCount = 2;
            m_lineRenderer.SetPosition(0, a);
            m_lineRenderer.SetPosition(1, b);
        }
    }
}
