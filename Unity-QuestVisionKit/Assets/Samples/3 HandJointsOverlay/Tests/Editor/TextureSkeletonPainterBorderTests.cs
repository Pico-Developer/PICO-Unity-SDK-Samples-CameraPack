using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PICO.Samples.HandJointsOverlay.Tests
{
    public class TextureSkeletonPainterBorderTests
    {
        private static readonly Color32 k_sourceColor = new Color32(20, 30, 40, 255);
        private static readonly Color32 k_borderColor = new Color32(40, 168, 255, 255);

        [Test]
        public void Apply_DefaultBorder_DrawsSolidEdgesAndFadingGlow()
        {
            Texture2D source = CreateSolidTexture(32, 24, k_sourceColor);
            GameObject gameObject = new GameObject("TextureSkeletonPainterBorderTest");
            var painter = gameObject.AddComponent<TextureSkeletonPainter>();

            try
            {
                Assert.That(painter.PrepareFrame(source), Is.True);
                painter.Apply();

                Assert.That(ReadPixel(painter.ResultTexture, 0, 12), Is.EqualTo(k_borderColor));
                Assert.That(ReadPixel(painter.ResultTexture, 4, 12), Is.EqualTo(k_borderColor));

                Color32 nearGlow = ReadPixel(painter.ResultTexture, 5, 12);
                Color32 farGlow = ReadPixel(painter.ResultTexture, 10, 12);
                Assert.That(nearGlow.r, Is.GreaterThan(farGlow.r));
                Assert.That(farGlow.r, Is.GreaterThan(k_sourceColor.r));
                Assert.That(nearGlow.g, Is.GreaterThan(farGlow.g));
                Assert.That(farGlow.g, Is.GreaterThan(k_sourceColor.g));
                Assert.That(nearGlow.b, Is.GreaterThan(farGlow.b));
                Assert.That(farGlow.b, Is.GreaterThan(k_sourceColor.b));
                Assert.That(nearGlow.a, Is.EqualTo(k_sourceColor.a));
                Assert.That(farGlow.a, Is.EqualTo(k_sourceColor.a));

                Assert.That(ReadPixel(painter.ResultTexture, 16, 12), Is.EqualTo(k_sourceColor));
            }
            finally
            {
                DestroyTestObjects(gameObject, painter.ResultTexture, source);
            }
        }

        [Test]
        public void Apply_BorderDisabled_PreservesSourcePixels()
        {
            Texture2D source = CreateSolidTexture(16, 16, k_sourceColor);
            GameObject gameObject = new GameObject("TextureSkeletonPainterBorderDisabledTest");
            var painter = gameObject.AddComponent<TextureSkeletonPainter>();
            var serializedPainter = new SerializedObject(painter);
            serializedPainter.FindProperty("m_drawBorder").boolValue = false;
            serializedPainter.ApplyModifiedPropertiesWithoutUndo();

            try
            {
                Assert.That(painter.PrepareFrame(source), Is.True);
                painter.Apply();

                Assert.That(ReadPixel(painter.ResultTexture, 0, 0), Is.EqualTo(k_sourceColor));
                Assert.That(ReadPixel(painter.ResultTexture, 8, 8), Is.EqualTo(k_sourceColor));
            }
            finally
            {
                DestroyTestObjects(gameObject, painter.ResultTexture, source);
            }
        }

        [Test]
        public void Apply_TextureSmallerThanBorder_RemainsBoundsSafe()
        {
            Texture2D source = CreateSolidTexture(4, 3, k_sourceColor);
            GameObject gameObject = new GameObject("TextureSkeletonPainterSmallTextureTest");
            var painter = gameObject.AddComponent<TextureSkeletonPainter>();

            try
            {
                Assert.That(painter.PrepareFrame(source), Is.True);
                Assert.DoesNotThrow(painter.Apply);
                Assert.That(ReadPixel(painter.ResultTexture, 2, 1), Is.EqualTo(k_borderColor));
            }
            finally
            {
                DestroyTestObjects(gameObject, painter.ResultTexture, source);
            }
        }

        private static Texture2D CreateSolidTexture(int width, int height, Color32 color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = texture.GetPixelData<Color32>(0);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            texture.Apply(false);
            return texture;
        }

        private static Color32 ReadPixel(Texture2D texture, int x, int y)
        {
            return texture.GetPixelData<Color32>(0)[y * texture.width + x];
        }

        private static void DestroyTestObjects(
            GameObject gameObject,
            Texture2D result,
            Texture2D source)
        {
            if (result != null)
            {
                Object.DestroyImmediate(result);
            }
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(gameObject);
        }
    }
}
