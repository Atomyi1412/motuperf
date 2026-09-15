using System;
using System.IO;
using System.IO.Compression;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Core.Tests
{
    public sealed class SessionArchiveServiceTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("screenshots/missing.png")]
        public void ExternalManifestCannotReadLocalFilesOnResave(string reference)
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string local = Path.Combine(root, "private.png");
                File.WriteAllText(local, "private bytes");
                string archive = Path.Combine(root, "external.motuperf");
                using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                using (StreamWriter writer = new StreamWriter(zip.CreateEntry("session.json").Open()))
                    writer.Write(System.Text.Json.JsonSerializer.Serialize(new { Version = 5, Screenshots = new[] { new { OriginalPath = local, ArchivePath = reference } } }));
                if (reference.Length > 0)
                    Assert.Throws<InvalidDataException>(() => SessionArchiveService.Load(archive, Path.Combine(root, "opened")));
                else
                {
                    SessionDocument loaded = SessionArchiveService.Load(archive, Path.Combine(root, "opened"));
                    Assert.Equal("", loaded.Screenshots[0].OriginalPath);
                    Assert.Throws<InvalidDataException>(() => SessionArchiveService.Save(Path.Combine(root, "resaved.motuperf"), loaded));
                }
                Assert.Equal("private bytes", File.ReadAllText(local));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void MissingScreenshotDoesNotReplaceExistingArchiveOrLeaveTemporaryFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string archive = Path.Combine(root, "saved.motuperf");
                File.WriteAllText(archive, "existing archive");
                SessionDocument document = new SessionDocument();
                document.Screenshots.Add(new SessionScreenshot { OriginalPath = Path.Combine(root, "missing.png"), ArchivePath = "screenshots/missing.png" });
                Assert.Throws<InvalidDataException>(() => SessionArchiveService.Save(archive, document));
                Assert.Equal("existing archive", File.ReadAllText(archive));
                Assert.Single(Directory.GetFiles(root));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SessionRoundTripPreservesSamplesAndScreenshot()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string screenshot = Path.Combine(root, "screen.png");
                File.WriteAllBytes(screenshot, new byte[] { 1, 2, 3, 4 });
                string archive = Path.Combine(root, "roundtrip.motuperf");
                SessionDocument document = new SessionDocument { Format = "motuperf-session", Version = 5, FollowLatest = false, ViewStartTime = 1.5, ViewEndTime = 4.5 };
                document.Samples.Add(new PerfSample { ElapsedSec = 3, HasFps = true, Fps = 59 });
                document.Screenshots.Add(new SessionScreenshot
                {
                    ElapsedSec = 3,
                    OriginalPath = screenshot,
                    ArchivePath = "screenshots/00000-screen.png",
                    Orientation = ScreenshotOrientation.LandscapeHomeToLeft
                });

                SessionArchiveService.Save(archive, document);
                using (ZipArchive zip = ZipFile.OpenRead(archive))
                using (StreamReader reader = new StreamReader(zip.GetEntry("session.json").Open()))
                    Assert.DoesNotContain("OriginalPath", reader.ReadToEnd());
                SessionDocument loaded = SessionArchiveService.Load(archive, Path.Combine(root, "opened"));

                Assert.Single(loaded.Samples);
                Assert.Equal(59, loaded.Samples[0].Fps);
                Assert.False(loaded.FollowLatest);
                Assert.Equal(1.5, loaded.ViewStartTime);
                Assert.Equal(4.5, loaded.ViewEndTime);
                Assert.Single(loaded.Screenshots);
                Assert.Equal(ScreenshotOrientation.LandscapeHomeToLeft, loaded.Screenshots[0].Orientation);
                Assert.True(File.Exists(loaded.Screenshots[0].OriginalPath));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(loaded.Screenshots[0].OriginalPath));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void OlderSessionWithoutScreenshotOrientationDefaultsToUnknown()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string archive = Path.Combine(root, "legacy.motuperf");
                using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                using (StreamWriter writer = new StreamWriter(zip.CreateEntry("session.json").Open()))
                {
                    writer.Write("{\"Format\":\"motuperf-session\",\"Version\":5,\"Screenshots\":[{\"ElapsedSec\":3}]}" );
                }

                SessionDocument loaded = SessionArchiveService.Load(archive, Path.Combine(root, "opened"));

                Assert.Single(loaded.Screenshots);
                Assert.Equal(ScreenshotOrientation.Unknown, loaded.Screenshots[0].Orientation);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RejectsUnsupportedFutureSessionVersion()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string archive = Path.Combine(root, "future.motuperf");
                using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                using (StreamWriter writer = new StreamWriter(zip.CreateEntry("session.json").Open())) writer.Write("{\"Format\":\"motuperf-session\",\"Version\":99}");
                Assert.Throws<InvalidDataException>(delegate { SessionArchiveService.Load(archive, Path.Combine(root, "opened")); });
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RejectsDuplicateScreenshotArchiveNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string screenshot = Path.Combine(root, "screen.png");
                File.WriteAllBytes(screenshot, new byte[] { 1, 2, 3 });
                SessionDocument document = new SessionDocument { Format = "motuperf-session", Version = 5 };
                document.Screenshots.Add(new SessionScreenshot { OriginalPath = screenshot, ArchivePath = "screenshots/same.png" });
                document.Screenshots.Add(new SessionScreenshot { OriginalPath = screenshot, ArchivePath = "same.png" });

                Assert.Throws<InvalidDataException>(delegate { SessionArchiveService.Save(Path.Combine(root, "duplicate.motuperf"), document); });
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RemovesExtractedScreenshotsWhenLoadingFailsAfterExtraction()
        {
            string root = Path.Combine(Path.GetTempPath(), "motuperf-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string extraction = Path.Combine(root, "opened");
            try
            {
                string archive = Path.Combine(root, "broken.motuperf");
                using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    using (StreamWriter writer = new StreamWriter(zip.CreateEntry("session.json").Open()))
                    {
                        writer.Write("{\"Format\":\"motuperf-session\",\"Version\":5,\"Screenshots\":[{\"ArchivePath\":\"screenshots/first.png\"},{\"ArchivePath\":\"screenshots/\"}]}");
                    }
                    using (Stream stream = zip.CreateEntry("screenshots/first.png").Open()) stream.WriteByte(7);
                }

                Assert.Throws<InvalidDataException>(delegate { SessionArchiveService.Load(archive, extraction); });
                Assert.True(!Directory.Exists(extraction) || Directory.GetFiles(extraction).Length == 0);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
