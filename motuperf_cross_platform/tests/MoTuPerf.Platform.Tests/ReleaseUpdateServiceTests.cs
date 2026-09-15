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
