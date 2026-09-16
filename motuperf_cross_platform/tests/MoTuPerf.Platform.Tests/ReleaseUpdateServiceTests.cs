using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
    }
}
