using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MoTuPerf.Desktop;
using SkiaSharp;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class IconGeometryTests
    {
        [Fact]
        public void SharedIconsRenderNonblankWithVisibleInternalDetailsInAllThemes()
        {
            DirectoryInfo root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "MoTuPerf.CrossPlatform.sln")))
                root = root.Parent;
            Assert.NotNull(root);
            XDocument app = XDocument.Load(Path.Combine(root.FullName, "src", "MoTuPerf.Desktop", "App.axaml"));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement[] icons = app.Descendants().Where(element => element.Name.LocalName == "StreamGeometry"
                && ((string)element.Attribute(xaml + "Key") ?? "").StartsWith("Icon", StringComparison.Ordinal)).ToArray();
            Assert.Equal(11, icons.Length);

            using (SKBitmap preview = new SKBitmap(icons.Length * 80, AppThemeManager.Themes.Count * 80))
            using (SKCanvas sheet = new SKCanvas(preview))
            {
                for (int row = 0; row < AppThemeManager.Themes.Count; row++)
                {
                    AppThemeDefinition theme = AppThemeManager.Themes[row];
                    SKColor background = SKColor.Parse(theme.Resources["Theme.ToolbarBackground"]);
                    SKColor foreground = SKColor.Parse(theme.Resources["Theme.TextSecondary"]);
                    for (int column = 0; column < icons.Length; column++)
                    {
                        XElement icon = icons[column];
                        string key = (string)icon.Attribute(xaml + "Key");
                        Assert.StartsWith("F1 ", icon.Value);
                        using (SKPath path = SKPath.ParseSvgPathData(icon.Value.Substring(3)))
                        using (SKBitmap bitmap = new SKBitmap(48, 48))
                        using (SKCanvas canvas = new SKCanvas(bitmap))
                        using (SKPaint paint = new SKPaint { Color = foreground, IsAntialias = true })
                        {
                            Assert.NotNull(path);
                            path.FillType = SKPathFillType.Winding;
                            Assert.InRange(path.Bounds.Width, 10, 24);
                            Assert.InRange(path.Bounds.Height, 10, 24);
                            canvas.Clear(background);
                            canvas.Scale(2);
                            canvas.DrawPath(path, paint);
                            canvas.Flush();
                            Assert.InRange(bitmap.Pixels.Count(pixel => pixel != background), 100, 1900);
                            if (key == "IconSettings") Assert.Equal(background, bitmap.GetPixel(24, 24));
                            if (key == "IconInfo") Assert.Equal(background, bitmap.GetPixel(24, 26));
                            if (key == "IconHelp") Assert.Equal(background, bitmap.GetPixel(24, 33));
                            using (SKPaint surface = new SKPaint { Color = background })
                                sheet.DrawRect(column * 80, row * 80, 80, 80, surface);
                            sheet.DrawBitmap(bitmap, column * 80 + 16, row * 80 + 16);
                        }
                    }
                }
                string previewPath = Environment.GetEnvironmentVariable("MOTUPERF_ICON_PREVIEW");
                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    using (SKImage image = SKImage.FromBitmap(preview))
                    using (SKData png = image.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(previewPath, png.ToArray());
                }
            }
        }
    }
}
