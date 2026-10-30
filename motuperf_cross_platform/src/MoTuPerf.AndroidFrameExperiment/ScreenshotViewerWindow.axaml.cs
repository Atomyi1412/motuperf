using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace MoTuPerf.Desktop
{
    public partial class ScreenshotViewerWindow : Window
    {
        private readonly List<ScreenshotItemViewModel> _screenshots;
        private Image PreviewImage;
        private ScrollViewer ImageScroll;
        private TextBlock CounterText;
        private TextBlock TimeText;
        private int _index;
        private double _zoom;
        private Bitmap _displayedImage;

        public event Action<ScreenshotItemViewModel> ScreenshotSelected;

        public ScreenshotViewerWindow()
            : this(Array.Empty<ScreenshotItemViewModel>(), null)
        {
        }

        public ScreenshotViewerWindow(IEnumerable<ScreenshotItemViewModel> screenshots, ScreenshotItemViewModel selected)
        {
            _screenshots = (screenshots ?? Enumerable.Empty<ScreenshotItemViewModel>()).Where(delegate(ScreenshotItemViewModel item) { return item != null; }).OrderBy(delegate(ScreenshotItemViewModel item) { return item.ElapsedSec; }).ToList();
            _index = Math.Max(0, _screenshots.FindIndex(delegate(ScreenshotItemViewModel item) { return ReferenceEquals(item, selected); }));
            InitializeComponent();
            Opened += async delegate { await ShowCurrentAsync(true); };
            SizeChanged += async delegate { if (_zoom <= 0) await ShowCurrentAsync(true); };
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            PreviewImage = this.FindControl<Image>("PreviewImage");
            ImageScroll = this.FindControl<ScrollViewer>("ImageScroll");
            CounterText = this.FindControl<TextBlock>("CounterText");
            TimeText = this.FindControl<TextBlock>("TimeText");
        }

        private async void Previous(object sender, RoutedEventArgs e) { await MoveAsync(-1); }
        private async void Next(object sender, RoutedEventArgs e) { await MoveAsync(1); }
        private void ZoomOut(object sender, RoutedEventArgs e) { SetZoom(_zoom <= 0 ? 0.8 : _zoom / 1.25); }
        private void ZoomIn(object sender, RoutedEventArgs e) { SetZoom(_zoom <= 0 ? 1.25 : _zoom * 1.25); }
        private async void Fit(object sender, RoutedEventArgs e) { await ShowCurrentAsync(true); }
        private void CloseWindow(object sender, RoutedEventArgs e) { Close(); }
        private void TitleBarPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }
        private async void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left) { await MoveAsync(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { await MoveAsync(1); e.Handled = true; }
            else if (e.Key == Key.Add || e.Key == Key.OemPlus) { ZoomIn(sender, e); e.Handled = true; }
            else if (e.Key == Key.Subtract || e.Key == Key.OemMinus) { ZoomOut(sender, e); e.Handled = true; }
        }
        private async Task MoveAsync(int delta)
        {
            if (_screenshots.Count == 0) return;
            int nextIndex = Math.Max(0, Math.Min(_screenshots.Count - 1, _index + delta));
            if (nextIndex == _index) return;
            _index = nextIndex;
            ScreenshotSelected?.Invoke(_screenshots[_index]);
            await ShowCurrentAsync(true);
        }
        private async Task ShowCurrentAsync(bool fit)
        {
            if (_screenshots.Count == 0) { Close(); return; }
            ScreenshotItemViewModel item = _screenshots[_index];
            CounterText.Text = (_index + 1) + " / " + _screenshots.Count;
            TimeText.Text = item.TimeLabel;
            PreviewImage.Source = null;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            Bitmap image = await item.LoadFullImageAsync();
            if (_screenshots.Count == 0 || !ReferenceEquals(item, _screenshots[_index]))
            {
                image?.Dispose();
                return;
            }
            _displayedImage?.Dispose();
            _displayedImage = image;
            PreviewImage.Source = image;
            if (image == null)
            {
                CounterText.Text = (_index + 1) + " / " + _screenshots.Count + "  (截图读取失败)";
                return;
            }
            if (fit)
            {
                ResizeWindowForImage(image);
                double availableWidth = Math.Max(80, WindowFitWidth(image) - 32);
                double availableHeight = Math.Max(80, WindowFitHeight(image) - 94);
                double imageWidth = Math.Max(1, image.PixelSize.Width);
                double imageHeight = Math.Max(1, image.PixelSize.Height);
                _zoom = Math.Min(1, Math.Min(availableWidth / imageWidth, availableHeight / imageHeight));
                PreviewImage.Stretch = Avalonia.Media.Stretch.Uniform;
                PreviewImage.Width = imageWidth * _zoom;
                PreviewImage.Height = imageHeight * _zoom;
            }
        }

        private bool IsLandscapeImage(Bitmap image)
        {
            return image != null && image.PixelSize.Width > image.PixelSize.Height;
        }

        private double WindowFitWidth(Bitmap image)
        {
            return IsLandscapeImage(image) ? 1220 : 820;
        }

        private double WindowFitHeight(Bitmap image)
        {
            return IsLandscapeImage(image) ? 820 : 920;
        }

        private void ResizeWindowForImage(Bitmap image)
        {
            if (image == null) return;
            double workWidth = 1500;
            double workHeight = 1000;
            try
            {
                if (Screens != null && Screens.Primary != null)
                {
                    double scaling = Screens.Primary.Scaling > 0 ? Screens.Primary.Scaling : 1;
                    workWidth = Screens.Primary.WorkingArea.Width / scaling;
                    workHeight = Screens.Primary.WorkingArea.Height / scaling;
                }
            }
            catch
            {
            }

            double widthLimit = Math.Min(WindowFitWidth(image), Math.Max(640, workWidth * 0.92));
            double heightLimit = Math.Min(WindowFitHeight(image), Math.Max(480, workHeight * 0.90));
            double imageWidth = Math.Max(1, image.PixelSize.Width);
            double imageHeight = Math.Max(1, image.PixelSize.Height);
            double fitScale = Math.Min(1, Math.Min((widthLimit - 32) / imageWidth, (heightLimit - 94) / imageHeight));
            if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0) fitScale = 0.45;
            Width = Math.Max(MinWidth, Math.Min(widthLimit, imageWidth * fitScale + 32));
            Height = Math.Max(MinHeight, Math.Min(heightLimit, imageHeight * fitScale + 94));
        }
        private void SetZoom(double value)
        {
            if (_screenshots.Count == 0) return;
            _zoom = Math.Max(0.25, Math.Min(5, value));
            Bitmap image = _displayedImage;
            if (image == null) return;
            PreviewImage.Stretch = Avalonia.Media.Stretch.Fill;
            PreviewImage.Width = image.PixelSize.Width * _zoom;
            PreviewImage.Height = image.PixelSize.Height * _zoom;
        }
        protected override void OnClosed(EventArgs e)
        {
            _displayedImage?.Dispose();
            _displayedImage = null;
            base.OnClosed(e);
        }
    }
}
