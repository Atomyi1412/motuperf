using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class ReleaseUpdateServiceTests
    {
        [Fact]
        public void InstallerHandoffUsesCurrentDirectoryIncludingSpacesAndUnicode()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MoTuPerf 测试", "Program Files");
            string package = Path.Combine(Path.GetTempPath(), "downloads", "update.exe");
            var startInfo = ReleaseUpdateService.CreateInstallerStartInfo(package, directory);
            Assert.Equal(Path.GetFullPath(package), startInfo.FileName);
            Assert.True(startInfo.UseShellExecute);
            Assert.Equal(OperatingSystem.IsWindows() ? "/D=" + Path.GetFullPath(directory) : "", startInfo.Arguments);
        }

        [Theory]
        [InlineData("v0.21.6", "0.21.6", false)]
        [InlineData("0.21.6", "0.21.7", true)]
        [InlineData("0.21.7", "0.21.6", false)]
        public void ParsesStableThreePartVersions(string current, string candidate, bool candidateIsNewer)
        {
            Assert.True(ReleaseUpdateService.TryParseVersion(current, out Version currentVersion));
            Assert.True(ReleaseUpdateService.TryParseVersion(candidate, out Version candidateVersion));
            Assert.Equal(candidateIsNewer, candidateVersion > currentVersion);
        }

        [Fact]
        public void RejectsNonThreePartVersion()
        {
            Assert.False(ReleaseUpdateService.TryParseVersion("0.21", out _));
        }

        [Fact]
        public async Task ChecksManifestAndSelectsCurrentPlatformAsset()
        {
            string target = ReleaseUpdateService.GetCurrentTarget();
            string fileName = target == "win-x64" ? "MoTuPerf-Setup-v99.1.2.exe" : "MoTuPerf-v99.1.2-osx-arm64.dmg";
            string json = "{\"schema\":1,\"version\":\"99.1.2\",\"mandatory\":false,\"releaseNotesUrl\":\"https://github.com/Atomyi1412/motuperf/releases/tag/v99.1.2\",\"assets\":{\"" + target + "\":{\"fileName\":\"" + fileName + "\",\"url\":\"https://github.com/Atomyi1412/motuperf/releases/download/v99.1.2/" + fileName + "\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}}";
            ReleaseUpdateService service = new ReleaseUpdateService(new HttpClient(new JsonHandler(json)));

            UpdateCheckResult result = await service.CheckAsync("0.21.6", CancellationToken.None);

            Assert.True(result.HasUpdate);
            Assert.Equal("99.1.2", result.Manifest.Version);
            Assert.Equal(target, result.Manifest.Asset.Target);
        }

        [Theory]
        [InlineData("https://example.com/update.exe")]
        [InlineData("https://github.com/Atomyi1412/motuperf/archive/v1.zip")]
        [InlineData("http://github.com/Atomyi1412/motuperf/releases/download/v1/update.exe")]
        public void RejectsUntrustedDownloadUrls(string url)
        {
            Assert.Throws<InvalidDataException>(delegate
            {
                ReleaseUpdateService.ValidateAsset(new UpdateAsset { FileName = "update.exe", Url = url, Sha256 = new string('a', 64) });
            });
        }

        [Fact]
        public async Task HashMismatchDeletesTemporaryDownload()
        {
            byte[] content = Encoding.UTF8.GetBytes("package");
            string target = ReleaseUpdateService.GetCurrentTarget();
            string fileName = target == "win-x64" ? "update.exe" : "update.dmg";
            UpdateAsset asset = new UpdateAsset { Target = target, FileName = fileName, Url = "https://github.com/Atomyi1412/motuperf/releases/download/v99.1.2/" + fileName, Sha256 = new string('a', 64) };
            ReleaseUpdateService service = new ReleaseUpdateService(new HttpClient(new BytesHandler(content)));
            string directory = Directory.CreateTempSubdirectory("motuperf-update-").FullName;
            try
            {
                await Assert.ThrowsAsync<InvalidDataException>(delegate { return service.DownloadAsync(asset, directory, null, CancellationToken.None); });
                Assert.Empty(Directory.GetFiles(directory));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task ResumesFromExistingBytesAfterReadTimeout()
        {
            byte[] content = Encoding.UTF8.GetBytes("0123456789");
            string target = ReleaseUpdateService.GetCurrentTarget();
            string fileName = target == "win-x64" ? "update.exe" : "update.dmg";
            UpdateAsset asset = new UpdateAsset
            {
                Target = target,
                FileName = fileName,
                Url = "https://github.com/Atomyi1412/motuperf/releases/download/v99.1.2/" + fileName,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
            };
            RangeResumeHandler handler = new RangeResumeHandler(content, 4);
            ReleaseUpdateService service = new ReleaseUpdateService(
                new HttpClient(handler), TimeSpan.FromMilliseconds(20), 2);
            string directory = Directory.CreateTempSubdirectory("motuperf-update-").FullName;
            try
            {
                string path = await service.DownloadAsync(asset, directory, null, CancellationToken.None);

                Assert.Equal(content, File.ReadAllBytes(path));
                Assert.Equal(2, handler.Requests.Count);
                Assert.Null(handler.Requests[0]);
                Assert.Equal(4, handler.Requests[1].Ranges.Single().From);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task ThrottlesProgressReportsDuringFastDownload()
        {
            byte[] content = new byte[4 * 1024 * 1024];
            new Random(42).NextBytes(content);
            string target = ReleaseUpdateService.GetCurrentTarget();
            string fileName = target == "win-x64" ? "update.exe" : "update.dmg";
            UpdateAsset asset = new UpdateAsset
            {
                Target = target,
                FileName = fileName,
                Url = "https://github.com/Atomyi1412/motuperf/releases/download/v99.1.2/" + fileName,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
            };
            ReleaseUpdateService service = new ReleaseUpdateService(new HttpClient(new BytesHandler(content)));
            RecordingProgress progress = new RecordingProgress();
            string directory = Directory.CreateTempSubdirectory("motuperf-update-").FullName;
            try
            {
                await service.DownloadAsync(asset, directory, progress, CancellationToken.None);

                Assert.True(progress.Values.Count <= 7, "Fast downloads should not enqueue one UI update per 128KB read.");
                Assert.Equal(100, progress.Values.Last().Percent);
                Assert.Equal(content.Length, progress.Values.Last().BytesReceived);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("null", 0)]
        [InlineData("{}", 0)]
        [InlineData("[\"真实更新内容\",\"第二项\"]", 2)]
        [InlineData("[\"  真实更新内容  \",\"  \"]", 1)]
        [InlineData("[\"第一项\",123]", 0)]
        public void ReadsOptionalReleaseNotesWithoutBreakingLegacyManifests(string notesJson, int expectedCount)
        {
            UpdateManifest manifest = ParseWithNotes(notesJson);
            Assert.Equal(expectedCount, manifest.ReleaseNotes.Count);
            Assert.Equal("99.1.2", manifest.Version);
            if (expectedCount > 0) Assert.Equal("真实更新内容", manifest.ReleaseNotes[0]);
        }

        [Fact]
        public void OversizedNotesDoNotPreventUpdateChecks()
        {
            Assert.Empty(ParseWithNotes(System.Text.Json.JsonSerializer.Serialize(new[] { new string('x', 1001) })).ReleaseNotes);
            Assert.Empty(ParseWithNotes(System.Text.Json.JsonSerializer.Serialize(new string[25])).ReleaseNotes);
        }

        private static UpdateManifest ParseWithNotes(string notesJson)
        {
            string target = ReleaseUpdateService.GetCurrentTarget();
            string fileName = ReleaseUpdateService.ExpectedFileName("99.1.2", target);
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = 1,
                version = "99.1.2",
                assets = new System.Collections.Generic.Dictionary<string, object>
                {
                    [target] = new { fileName, url = "https://github.com/Atomyi1412/motuperf/releases/download/v99.1.2/" + fileName, sha256 = new string('a', 64) }
                }
            });
            if (notesJson != null) json = json.Substring(0, json.Length - 1) + ",\"releaseNotes\":" + notesJson + "}";
            return ReleaseUpdateService.ParseManifest(json);
        }

        private sealed class JsonHandler : HttpMessageHandler
        {
            private readonly string _json;
            public JsonHandler(string json) { _json = json; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class BytesHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;
            public BytesHandler(byte[] bytes) { _bytes = bytes; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) });
            }
        }

        private sealed class RecordingProgress : IProgress<UpdateDownloadProgress>
        {
            public List<UpdateDownloadProgress> Values { get; } = new List<UpdateDownloadProgress>();
            public void Report(UpdateDownloadProgress value) { Values.Add(value); }
        }

        private sealed class RangeResumeHandler : HttpMessageHandler
        {
            private readonly byte[] _content;
            private readonly int _prefixLength;
            public List<RangeHeaderValue> Requests { get; } = new List<RangeHeaderValue>();

            public RangeResumeHandler(byte[] content, int prefixLength)
            {
                _content = content;
                _prefixLength = prefixLength;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                RangeHeaderValue range = request.Headers.Range;
                Requests.Add(range);
                if (range == null)
                {
                    HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new StallingStream(_content, _prefixLength))
                    };
                    response.Content.Headers.ContentLength = _content.Length;
                    return Task.FromResult(response);
                }

                long start = range.Ranges.Single().From.GetValueOrDefault();
                byte[] remainder = _content.Skip((int)start).ToArray();
                HttpResponseMessage resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(remainder)
                };
                resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start, _content.Length - 1, _content.Length);
                return Task.FromResult(resumed);
            }
        }

        private sealed class StallingStream : Stream
        {
            private readonly byte[] _content;
            private readonly int _prefixLength;
            private int _position;

            public StallingStream(byte[] content, int prefixLength)
            {
                _content = content;
                _prefixLength = prefixLength;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _content.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position < _prefixLength)
                {
                    int count = Math.Min(buffer.Length, _prefixLength - _position);
                    _content.AsMemory(_position, count).CopyTo(buffer);
                    _position += count;
                    return count;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
