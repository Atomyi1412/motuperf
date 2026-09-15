using System;
using System.IO;
using CSharpIosPerfMonitor;
using SkiaSharp;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class ScreenshotImageLoaderTests
    {
        [Theory]
        [InlineData(ScreenshotOrientation.LandscapeHomeToRight)]
        [InlineData(ScreenshotOrientation.LandscapeHomeToLeft)]
        public void LandscapeOrientationRestoresTheExpectedPixelOrder(ScreenshotOrientation orientation)
        {
            string path = Path.Combine(Path.GetTempPath(), "motuperf-oriented-pixels-" + Guid.NewGuid().ToString("N") + ".png");
            SKColor[] captured = { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow, SKColors.Magenta, SKColors.Cyan };
            // Portrait rows: RG / BY / MC. Expected landscape rows are independently specified.
            SKColor[] expected = orientation == ScreenshotOrientation.LandscapeHomeToRight
                ? new[] { SKColors.Green, SKColors.Yellow, SKColors.Cyan, SKColors.Red, SKColors.Blue, SKColors.Magenta }
                : new[] { SKColors.Magenta, SKColors.Blue, SKColors.Red, SKColors.Cyan, SKColors.Yellow, SKColors.Green };
            try
            {
                using (SKBitmap source = new SKBitmap(2, 3))
                {
                    source.Pixels = captured;
                    using (SKImage image = SKImage.FromBitmap(source))
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(path, data.ToArray());
                }
                byte[] original = File.ReadAllBytes(path);
                using (SKBitmap result = ScreenshotImageLoader.DecodeRotatedPixels(path, orientation))
                {
                    Assert.Equal(3, result.Width);
                    Assert.Equal(2, result.Height);
                    Assert.Equal(expected, result.Pixels);
                }
                Assert.Equal(original, File.ReadAllBytes(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Theory]
        [InlineData(90)]
        [InlineData(-90)]
        public void RotatesPortraitPixelsIntoLandscapePixels(int rotation)
        {
            using (SKBitmap source = new SKBitmap(20, 30))
            using (SKBitmap image = ScreenshotImageLoader.RotateForDisplay(source, rotation))
            {
                Assert.Equal(30, image.Width);
                Assert.Equal(20, image.Height);
            }
        }

        [Fact]
        public void DecodesAndRotatesARealPngWithoutChangingTheSourceFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "motuperf-orientation-" + Guid.NewGuid().ToString("N") + ".png");
            byte[] original;
            using (SKBitmap bitmap = new SKBitmap(20, 30))
            using (SKCanvas canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.CornflowerBlue);
                canvas.Flush();
                using (SKImage image = SKImage.FromBitmap(bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    original = data.ToArray();
                    File.WriteAllBytes(path, original);
                }
            }

            try
            {
                using (SKBitmap image = ScreenshotImageLoader.DecodeRotatedPixels(path, ScreenshotOrientation.LandscapeHomeToRight, 100))
                {
                    Assert.NotNull(image);
                    Assert.Equal(30, image.Width);
                    Assert.Equal(20, image.Height);
                }
                Assert.Equal(original, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
