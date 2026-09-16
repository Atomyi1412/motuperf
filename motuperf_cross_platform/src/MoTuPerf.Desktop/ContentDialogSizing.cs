using System;
using Avalonia.Controls;

namespace MoTuPerf.Desktop
{
    internal static class ContentDialogSizing
    {
        public static void Attach(Window window)
        {
            window.Opened += delegate
            {
                var screen = window.Screens.ScreenFromWindow(window.Owner as Window ?? window) ?? window.Screens.Primary;
                if (screen == null) return;
                double scale = screen.Scaling > 0 ? screen.Scaling : 1;
                double width = Math.Max(240, screen.WorkingArea.Width / scale - 48);
                window.MinWidth = Math.Min(window.MinWidth, width);
                window.MaxWidth = Math.Min(window.MaxWidth, width);
                window.Width = Math.Min(window.Width, width);
                window.MaxHeight = Math.Min(window.MaxHeight, Math.Max(180, screen.WorkingArea.Height / scale - 48));
            };
        }
    }
}
