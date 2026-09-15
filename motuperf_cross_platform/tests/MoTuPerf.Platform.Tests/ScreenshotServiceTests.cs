using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class ScreenshotServiceTests
    {
        [Theory]
        [InlineData("1\n", ScreenshotOrientation.Portrait)]
        [InlineData("2\r\n", ScreenshotOrientation.PortraitUpsideDown)]
        [InlineData("3", ScreenshotOrientation.LandscapeHomeToRight)]
        [InlineData("4\n", ScreenshotOrientation.LandscapeHomeToLeft)]
        public void ParsesSpringBoardOrientation(string output, ScreenshotOrientation expected)
        {
            Assert.Equal(expected, ScreenshotService.ParseIosOrientation(output));
        }

        [Theory]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("5")]
        [InlineData("device 3 ready")]
        public void RejectsUnknownSpringBoardOrientation(string output)
        {
            Assert.Equal(ScreenshotOrientation.Unknown, ScreenshotService.ParseIosOrientation(output));
        }
    }
}
