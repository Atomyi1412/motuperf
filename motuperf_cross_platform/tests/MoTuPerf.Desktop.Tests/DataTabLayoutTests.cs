using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class DataTabLayoutTests
    {
        [Theory]
        [InlineData(330)]
        [InlineData(358)]
        public void VisibleTabsShareEntireRowAndRestoreWithoutBlankColumns(double width)
        {
            UniformGrid row = new UniformGrid { Rows = 1 };
            Border live = new Border();
            Border selected = new Border();
            Border analysis = new Border();
            row.Children.Add(live);
            row.Children.Add(selected);
            row.Children.Add(analysis);

            for (int cycle = 0; cycle < 3; cycle++)
            {
                analysis.IsVisible = true;
                Layout(row, width);
                Assert.InRange(live.Bounds.Width, width / 3 - 1, width / 3 + 1);
                Assert.InRange(selected.Bounds.Width, width / 3 - 1, width / 3 + 1);
                Assert.InRange(analysis.Bounds.Width, width / 3 - 1, width / 3 + 1);
                Assert.Equal(live.Bounds.Right, selected.Bounds.Left, 6);
                Assert.Equal(selected.Bounds.Right, analysis.Bounds.Left, 6);
                // Pixel rounding may leave at most one physical pixel, never an empty tab column.
                Assert.InRange(analysis.Bounds.Right, width - 1, width);

                analysis.IsVisible = false;
                Layout(row, width);
                Assert.Equal(width / 2, live.Bounds.Width, 6);
                Assert.Equal(width / 2, selected.Bounds.Width, 6);
                Assert.Equal(live.Bounds.Right, selected.Bounds.Left, 6);
                Assert.Equal(width, selected.Bounds.Right, 6);
            }
        }

        private static void Layout(UniformGrid row, double width)
        {
            row.InvalidateMeasure();
            row.Measure(new Size(width, 36));
            row.Arrange(new Rect(0, 0, width, 36));
        }
    }
}
