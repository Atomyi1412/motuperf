using System;
using System.IO;
using Avalonia.Media.Imaging;
using CSharpIosPerfMonitor;
using SkiaSharp;

namespace MoTuPerf.Desktop
{
    internal static class ScreenshotDisplayOrientation
    {
        public static int RotationDegrees(ScreenshotOrientation orientation, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth >= pixelHeight) return 0;
            // The display transform must undo the rotation of the portrait capture canvas.
            if (orientation == ScreenshotOrientation.LandscapeHomeToRight) return -90;
            if (orientation == ScreenshotOrientation.LandscapeHomeToLeft) return 90;
            return 0;
        }

        public static bool IsLandscape(ScreenshotOrientation orientation, int pixelWidth, int pixelHeight)
        {
            return pixelWidth > pixelHeight
                || orientation == ScreenshotOrientation.LandscapeHomeToRight
                || orientation == ScreenshotOrientation.LandscapeHomeToLeft;
        }
    }

    internal static class ScreenshotImageLoader
    {
        public static Bitmap Load(string path, ScreenshotOrientation orientation, int maximumLongEdge = 0)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            using (SKCodec codec = SKCodec.Create(path))
            {
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
                int rotation = ScreenshotDisplayOrientation.RotationDegrees(orientation, codec.Info.Width, codec.Info.Height);
                if (rotation == 0) return LoadWithoutRotation(path, codec.Info.Width, codec.Info.Height, maximumLongEdge);
                using (SKBitmap rotated = DecodeRotatedPixels(codec, rotation, maximumLongEdge))
                {
                    if (rotated == null) return null;
                    using (SKImage image = SKImage.FromBitmap(rotated))
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                    using (MemoryStream stream = new MemoryStream())
                    {
                        data.SaveTo(stream);
                        stream.Position = 0;
                        return new Bitmap(stream);
                    }
                }
            }
        }

        internal static SKBitmap DecodeRotatedPixels(string path, ScreenshotOrientation orientation, int maximumLongEdge = 0)
        {
            using (SKCodec codec = SKCodec.Create(path))
            {
                if (codec == null) return null;
                int rotation = ScreenshotDisplayOrientation.RotationDegrees(orientation, codec.Info.Width, codec.Info.Height);
                return rotation == 0 ? null : DecodeRotatedPixels(codec, rotation, maximumLongEdge);
            }
        }

        private static SKBitmap DecodeRotatedPixels(SKCodec codec, int rotation, int maximumLongEdge)
        {
            SKSizeI decodedSize = ScaledSize(codec, maximumLongEdge);
            SKImageInfo decodedInfo = new SKImageInfo(decodedSize.Width, decodedSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (SKBitmap source = SKBitmap.Decode(codec, decodedInfo))
            {
                return source == null ? null : RotateForDisplay(source, rotation);
            }
        }

        internal static SKBitmap RotateForDisplay(SKBitmap source, int rotation)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (rotation != 90 && rotation != -90) throw new ArgumentOutOfRangeException(nameof(rotation));
            SKBitmap rotated = new SKBitmap(source.Height, source.Width, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (SKCanvas canvas = new SKCanvas(rotated))
            {
                canvas.Clear(SKColors.Transparent);
                if (rotation > 0)
                {
                    canvas.Translate(source.Height, 0);
                    canvas.RotateDegrees(90);
                }
                else
                {
                    canvas.Translate(0, source.Width);
                    canvas.RotateDegrees(-90);
                }
                canvas.DrawBitmap(source, 0, 0);
                canvas.Flush();
            }
            return rotated;
        }

        private static Bitmap LoadWithoutRotation(string path, int width, int height, int maximumLongEdge)
        {
            if (maximumLongEdge <= 0) return new Bitmap(path);
            using (FileStream stream = File.OpenRead(path))
            {
                return width >= height
                    ? Bitmap.DecodeToWidth(stream, maximumLongEdge, BitmapInterpolationMode.MediumQuality)
                    : Bitmap.DecodeToHeight(stream, maximumLongEdge, BitmapInterpolationMode.MediumQuality);
            }
        }

        private static SKSizeI ScaledSize(SKCodec codec, int maximumLongEdge)
        {
            int width = codec.Info.Width;
            int height = codec.Info.Height;
            if (maximumLongEdge <= 0 || Math.Max(width, height) <= maximumLongEdge) return new SKSizeI(width, height);
            float scale = maximumLongEdge / (float)Math.Max(width, height);
            SKSizeI size = codec.GetScaledDimensions(scale);
            return new SKSizeI(Math.Max(1, size.Width), Math.Max(1, size.Height));
        }
    }
}
