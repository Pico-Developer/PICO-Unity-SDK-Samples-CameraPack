using Unity.Collections;
using UnityEngine;

namespace PICO.Samples.HandJointsOverlay
{
    /// <summary>
    /// Selects which skeleton elements are drawn onto the camera image.
    /// </summary>
    public enum SkeletonDrawMode
    {
        PointsAndBones,
        PointsOnly,
        BonesOnly
    }

    /// <summary>
    /// Draws hand joint points and bone lines directly into a copy of the camera image,
    /// producing a "processed image" texture that can be displayed on a quad.
    /// </summary>
    public class TextureSkeletonPainter : MonoBehaviour
    {
        [Header("Appearance")]
        [SerializeField] private Color m_leftHandColor = new Color(0.2f, 0.8f, 1f, 1f);
        [SerializeField] private Color m_rightHandColor = new Color(1f, 0.6f, 0.2f, 1f);
        [Tooltip("Which skeleton elements to draw.")]
        [SerializeField] private SkeletonDrawMode m_drawMode = SkeletonDrawMode.PointsAndBones;
        [Tooltip("Radius of a joint dot in pixels.")]
        [SerializeField] private int m_jointRadius = 4;
        [Tooltip("Half-thickness of a bone line in pixels.")]
        [SerializeField] private int m_lineHalfWidth = 1;
        [Tooltip("Draw a border around the processed camera image.")]
        [SerializeField] private bool m_drawBorder = true;
        [Tooltip("Solid border color.")]
        [SerializeField] private Color m_borderColor =
            new Color(40f / 255f, 168f / 255f, 1f, 1f);
        [Tooltip("Solid border width in pixels.")]
        [SerializeField] private int m_borderWidth = 5;
        [Tooltip("Inward glow width in pixels.")]
        [SerializeField] private int m_borderGlowWidth = 6;

        private const int k_borderGlowStrength = 89;
        private Texture2D m_workTexture;
        private NativeArray<Color32> m_pixels;
        private bool m_hasPixels;
        private int m_width;
        private int m_height;

        // Bone connections expressed as joint index pairs.
        // Joint indices follow HandJoint: Wrist=1, and each finger chains
        // Metacarpal -> Proximal -> (Intermediate) -> Distal -> Tip.
        private static readonly int[] k_boneConnections =
        {
            // Thumb (no intermediate joint)
            1, 2, 2, 3, 3, 4, 4, 5,
            // Index
            1, 6, 6, 7, 7, 8, 8, 9, 9, 10,
            // Middle
            1, 11, 11, 12, 12, 13, 13, 14, 14, 15,
            // Ring
            1, 16, 16, 17, 17, 18, 18, 19, 19, 20,
            // Little
            1, 21, 21, 22, 22, 23, 23, 24, 24, 25
        };

        public Texture2D ResultTexture => m_workTexture;

        /// <summary>
        /// Copies the camera image (the manager's CPU-readable Texture2D, fed each frame via
        /// LoadRawTextureData) into the working texture, rebuilding it when the resolution
        /// changes. Returns false if the source is unusable.
        /// </summary>
        public bool PrepareFrame(Texture2D source)
        {
            if (source == null || source.width <= 0 || source.height <= 0)
            {
                return false;
            }

            int width = source.width;
            int height = source.height;

            if (m_workTexture == null || m_width != width || m_height != height)
            {
                m_width = width;
                m_height = height;

                if (m_workTexture != null)
                {
                    Destroy(m_workTexture);
                }

                m_workTexture = new Texture2D(m_width, m_height, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }

            // Copy straight into the work texture's writable CPU view (one native copy, no
            // managed allocation). Both views are [y * width + x] order, matching SetPixel.
            // The cached NativeArray stays valid until the next GetPixelData/Apply call.
            NativeArray<Color32> src = source.GetPixelData<Color32>(0);
            m_pixels = m_workTexture.GetPixelData<Color32>(0);
            if (src.Length < m_width * m_height || m_pixels.Length < m_width * m_height)
            {
                m_hasPixels = false;
                return false;
            }

            src.CopyTo(m_pixels);
            m_hasPixels = true;
            return true;
        }

        /// <summary>
        /// Draws the skeleton for one hand according to the configured draw mode.
        /// Pixels marks per-joint visibility (valid AND inside the camera frame).
        /// </summary>
        public void DrawHand(Vector2Int[] pixels, bool[] visible, bool isLeftHand)
        {
            if (!m_hasPixels || pixels == null || visible == null)
            {
                return;
            }

            Color32 color = isLeftHand ? (Color32)m_leftHandColor : (Color32)m_rightHandColor;

            if (m_drawMode != SkeletonDrawMode.PointsOnly)
            {
                for (int i = 0; i + 1 < k_boneConnections.Length; i += 2)
                {
                    int a = k_boneConnections[i];
                    int b = k_boneConnections[i + 1];
                    if (a >= pixels.Length || b >= pixels.Length ||
                        a >= visible.Length || b >= visible.Length ||
                        !visible[a] || !visible[b])
                    {
                        continue;
                    }

                    DrawLine(pixels[a], pixels[b], color);
                }
            }

            if (m_drawMode != SkeletonDrawMode.BonesOnly)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (i < visible.Length && visible[i])
                    {
                        DrawDot(pixels[i], color);
                    }
                }
            }
        }

        /// <summary>
        /// Uploads the work texture to the GPU. Call once per frame after drawing. The cached
        /// pixel view becomes invalid after Apply and is refreshed by the next PrepareFrame.
        /// </summary>
        public void Apply()
        {
            if (m_workTexture == null || !m_hasPixels)
            {
                return;
            }

            DrawBorder();
            m_workTexture.Apply(false);
            m_hasPixels = false;
        }

        private void DrawBorder()
        {
            if (!m_drawBorder || !m_hasPixels || m_width <= 0 || m_height <= 0)
            {
                return;
            }

            int solidWidth = Mathf.Max(0, m_borderWidth);
            int glowWidth = Mathf.Max(0, m_borderGlowWidth);
            int maxInset = Mathf.Min(
                solidWidth + glowWidth,
                (Mathf.Min(m_width, m_height) + 1) / 2);
            Color32 borderColor = m_borderColor;

            for (int inset = 0; inset < maxInset; inset++)
            {
                if (inset < solidWidth)
                {
                    DrawBorderLayer(inset, borderColor, 255);
                    continue;
                }

                int glowOffset = inset - solidWidth;
                int blendWeight =
                    glowWidth > 0
                        ? k_borderGlowStrength * (glowWidth - glowOffset) / glowWidth
                        : 0;
                DrawBorderLayer(inset, borderColor, blendWeight);
            }
        }

        private void DrawBorderLayer(
            int inset,
            Color32 borderColor,
            int blendWeight)
        {
            int left = inset;
            int right = m_width - 1 - inset;
            int bottom = inset;
            int top = m_height - 1 - inset;
            if (left > right || bottom > top)
            {
                return;
            }

            for (int x = left; x <= right; x++)
            {
                BlendBorderPixel(x, bottom, borderColor, blendWeight);
                if (top != bottom)
                {
                    BlendBorderPixel(x, top, borderColor, blendWeight);
                }
            }

            for (int y = bottom + 1; y < top; y++)
            {
                BlendBorderPixel(left, y, borderColor, blendWeight);
                if (right != left)
                {
                    BlendBorderPixel(right, y, borderColor, blendWeight);
                }
            }
        }

        private void BlendBorderPixel(
            int x,
            int y,
            Color32 borderColor,
            int blendWeight)
        {
            int index = y * m_width + x;
            if (blendWeight >= 255)
            {
                m_pixels[index] = borderColor;
                return;
            }

            Color32 source = m_pixels[index];
            int sourceWeight = 255 - blendWeight;
            m_pixels[index] = new Color32(
                (byte)((source.r * sourceWeight + borderColor.r * blendWeight + 127) / 255),
                (byte)((source.g * sourceWeight + borderColor.g * blendWeight + 127) / 255),
                (byte)((source.b * sourceWeight + borderColor.b * blendWeight + 127) / 255),
                source.a);
        }

        private void DrawDot(Vector2Int center, Color32 color)
        {
            int r = Mathf.Max(1, m_jointRadius);
            int rSq = r * r;

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy <= rSq)
                    {
                        SetPixel(center.x + dx, center.y + dy, color);
                    }
                }
            }
        }

        private void DrawLine(Vector2Int p0, Vector2Int p1, Color32 color)
        {
            // Integer Bresenham line, thickened by drawing a small square per step.
            int x0 = p0.x;
            int y0 = p0.y;
            int x1 = p1.x;
            int y1 = p1.y;

            int dx = Mathf.Abs(x1 - x0);
            int dy = -Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                DrawThickPoint(x0, y0, color);

                if (x0 == x1 && y0 == y1)
                {
                    break;
                }

                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private void DrawThickPoint(int x, int y, Color32 color)
        {
            int half = Mathf.Max(0, m_lineHalfWidth);
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    SetPixel(x + dx, y + dy, color);
                }
            }
        }

        private void SetPixel(int x, int y, Color32 color)
        {
            if (x < 0 || x >= m_width || y < 0 || y >= m_height)
            {
                return;
            }

            m_pixels[y * m_width + x] = color;
        }

        private void OnDestroy()
        {
            if (m_workTexture != null)
            {
                Destroy(m_workTexture);
                m_workTexture = null;
            }
        }
    }
}
