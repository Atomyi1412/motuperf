using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MoTuPerf.Platform;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class AppleDriverDownloadServiceTests
    {
        [Theory]
        [InlineData(0, "STATE              : 4  RUNNING", true)]
        [InlineData(0, "STATE              : 1  STOPPED", false)]
        [InlineData(1060, "OpenService FAILED", false)]
        public void DetectsAppleMobileDeviceServiceState(int exitCode, string output, bool expected)
        {
            Assert.Equal(expected, CSharpIosPerfMonitor.RuntimeTools.IsAppleMobileDeviceServiceRunningOutput(exitCode, output));
        }

        [Theory]
        [InlineData("Instance ID: USB\\VID_05AC&PID_12A8\\0001", true)]
        [InlineData("实例 ID: USB\\VID_18D1&PID_4EE7\\0001", false)]
        [InlineData("", false)]
        public void DetectsPhysicalAppleUsbHardwareId(string output, bool expected)
        {
            Assert.Equal(expected, CSharpIosPerfMonitor.RuntimeTools.ContainsAppleUsbHardwareId(output));
        }

        [Fact]
        public async Task DownloadsOnlyAppleExeResponseAndReportsCompletion()
        {
            byte[] payload = Encoding.ASCII.GetBytes("fake apple installer");
            FakeHandler handler = new FakeHandler(payload, new Uri("https://secure-appldnld.apple.com/itunes/iTunes64Setup.exe"), "application/octet-stream");
            AppleDriverDownloadService service = new AppleDriverDownloadService(new HttpClient(handler), new AcceptingAppleSignatureVerifier());
            string directory = Directory.CreateTempSubdirectory("motuperf-apple-driver-").FullName;
            RecordingProgress progress = new RecordingProgress();
            try
            {
                AppleDriverDownloadResult result = await service.DownloadAsync(directory, progress, CancellationToken.None);

                Assert.Equal(Path.Combine(directory, AppleDriverDownloadService.InstallerFileName), result.FilePath);
                Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
                Assert.Equal(1.0, progress.LastValue);
                Assert.Equal(new Uri(AppleDriverDownloadService.OfficialDownloadUrl), handler.RequestUri);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task RejectsInstallerWhenSignatureVerificationFails()
        {
            byte[] payload = Encoding.ASCII.GetBytes("fake apple installer");
            FakeHandler handler = new FakeHandler(payload, new Uri("https://secure-appldnld.apple.com/itunes/iTunes64Setup.exe"), "application/octet-stream");
            AppleDriverDownloadService service = new AppleDriverDownloadService(new HttpClient(handler), new RejectingAppleSignatureVerifier());
            string directory = Directory.CreateTempSubdirectory("motuperf-apple-driver-").FullName;
            try
            {
                await Assert.ThrowsAsync<InvalidDataException>(delegate
                {
                    return service.DownloadAsync(directory, null, CancellationToken.None);
                });
                Assert.False(File.Exists(Path.Combine(directory, AppleDriverDownloadService.InstallerFileName)));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private sealed class RecordingProgress : IProgress<double>
        {
            public double LastValue { get; private set; } = -2;
            public void Report(double value) { LastValue = value; }
        }

        [Theory]
        [InlineData("http://secure-appldnld.apple.com/itunes/iTunes64Setup.exe", "application/octet-stream")]
        [InlineData("https://example.com/iTunes64Setup.exe", "application/octet-stream")]
        [InlineData("https://secure-appldnld.apple.com/itunes/iTunes64Setup.html", "application/octet-stream")]
        [InlineData("https://secure-appldnld.apple.com/itunes/iTunes64Setup.exe", "text/html")]
        public void RejectsUntrustedOrNonInstallerResponse(string url, string mediaType)
        {
            Assert.Throws<InvalidDataException>(delegate
            {
                AppleDriverDownloadService.ValidateAppleInstallerResponse(new Uri(url), mediaType);
            });
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly byte[] _payload;
            private readonly Uri _responseUri;
            private readonly string _mediaType;
            public Uri RequestUri { get; private set; }

            public FakeHandler(byte[] payload, Uri responseUri, string mediaType)
            {
                _payload = payload;
                _responseUri = responseUri;
                _mediaType = mediaType;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_payload),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, _responseUri)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
                return Task.FromResult(response);
            }
        }

        private sealed class AcceptingAppleSignatureVerifier : IAppleInstallerSignatureVerifier
        {
            public bool IsValid(string path, out string reason) { reason = ""; return true; }
        }

        private sealed class RejectingAppleSignatureVerifier : IAppleInstallerSignatureVerifier
        {
            public bool IsValid(string path, out string reason) { reason = "测试签名无效"; return false; }
        }
    }
}
