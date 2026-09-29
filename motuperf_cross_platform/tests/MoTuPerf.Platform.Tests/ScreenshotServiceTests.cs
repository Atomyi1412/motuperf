using CSharpIosPerfMonitor;
using System.IO;
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

        [Fact]
        public void HarmonyScreenshotTriesNativeAndPortableCommands()
        {
            var commands = ScreenshotService.HarmonyScreenshotCommands("/data/local/tmp/test.jpeg");

            Assert.Equal(new[] { "snapshot_display", "-f", "/data/local/tmp/test.jpeg" }, commands[0]);
            Assert.Equal(new[] { "screencap", "-p", "/data/local/tmp/test.jpeg" }, commands[1]);
        }

        [Fact]
        public void ScreenshotPathsStayUniqueWhenElapsedIndexRepeats()
        {
            string first = ScreenshotService.CreateScreenshotPath("screenshots", 3);
            string second = ScreenshotService.CreateScreenshotPath("screenshots", 3);

            Assert.NotEqual(first, second);
            Assert.Contains("-00003-", Path.GetFileName(first));
            Assert.EndsWith(".png", second);
        }

        [Theory]
        [InlineData(1, "", "permission denied", false, 0, false, false, 5)]
        [InlineData(1, "", "device offline", false, 0, false, false, 3)]
        [InlineData(1, "", "unknown command", false, 0, false, false, 6)]
        [InlineData(1, "", "no such file", true, 0, false, false, 7)]
        [InlineData(0, "", "", true, 0, true, false, 8)]
        [InlineData(0, "", "", true, 12, true, false, 9)]
        [InlineData(0, "", "", true, 12, true, true, 10)]
        public void ClassifiesHarmonyScreenshotFailuresWithoutTurningThemIntoValidImages(
            int exitCode,
            string stdout,
            string stderr,
            bool receivingRemoteFile,
            long fileLength,
            bool fileExists,
            bool blackImage,
            int expected)
        {
            HarmonyScreenshotFailureKind actual = ScreenshotService.ClassifyHarmonyFailure(
                new ProcessResult(exitCode, stdout, stderr),
                receivingRemoteFile,
                fileExists,
                fileLength,
                fileExists && fileLength > 0 && expected != 9,
                blackImage);

            Assert.Equal((HarmonyScreenshotFailureKind)expected, actual);
        }

        [Theory]
        [InlineData(1, "", "")]
        [InlineData(0, "[Fail] snapshot failed", "")]
        [InlineData(0, "", "[Fail] snapshot failed")]
        [InlineData(0, "permission denied", "")]
        [InlineData(0, "", "unknown command")]
        [InlineData(0, "", "no such file")]
        [InlineData(0, "capture failed", "")]
        public void RejectsHarmonyCaptureFailureFromExitCodeOrEitherOutput(int exitCode, string stdout, string stderr)
        {
            Assert.True(ScreenshotService.IsHarmonyCaptureFailure(new ProcessResult(exitCode, stdout, stderr)));
        }

        [Fact]
        public void AcceptsHarmonyCaptureWhenExitCodeAndBothOutputsAreClean()
        {
            Assert.False(ScreenshotService.IsHarmonyCaptureFailure(
                new ProcessResult(0, "snapshot saved", "")));
        }
    }
}
