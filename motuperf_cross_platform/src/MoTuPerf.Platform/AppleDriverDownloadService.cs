using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MoTuPerf.Platform
{
    public sealed class AppleDriverDownloadResult
    {
        public string FilePath { get; set; }
        public long BytesDownloaded { get; set; }
        public long? ContentLength { get; set; }
    }

    public sealed class AppleDriverDownloadService
    {
        public const string OfficialDownloadUrl = "https://www.apple.com/itunes/download/win64";
        public const string InstallerFileName = "AppleDevicesSetup.exe";
        private const long MaxInstallerBytes = 512L * 1024L * 1024L;
        private readonly HttpClient _httpClient;
        private readonly IAppleInstallerSignatureVerifier _signatureVerifier;

        public AppleDriverDownloadService()
            : this(CreateHttpClient())
        {
        }

        internal AppleDriverDownloadService(HttpClient httpClient)
            : this(httpClient, new AppleInstallerSignatureVerifier())
        {
        }

        internal AppleDriverDownloadService(HttpClient httpClient, IAppleInstallerSignatureVerifier signatureVerifier)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException("httpClient");
            _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException("signatureVerifier");
        }

        public async Task<AppleDriverDownloadResult> DownloadAsync(string targetDirectory, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("苹果设备驱动下载仅适用于 Windows。");
            }

            string directory = Path.GetFullPath(targetDirectory ?? "");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, InstallerFileName + ".download");
            string installerPath = Path.Combine(directory, InstallerFileName);
            try
            {
                using (HttpResponseMessage response = await _httpClient.GetAsync(
                    new Uri(OfficialDownloadUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    Uri finalUri = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
                    ValidateAppleInstallerResponse(finalUri, response.Content.Headers.ContentType == null
                        ? ""
                        : response.Content.Headers.ContentType.MediaType);
                    long? contentLength = response.Content.Headers.ContentLength;
                    if (contentLength.HasValue && contentLength.Value > MaxInstallerBytes)
                    {
                        throw new InvalidDataException("苹果驱动安装包大小异常，已停止下载。");
                    }

                    long total = 0;
                    using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (FileStream destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        byte[] buffer = new byte[65536];
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            total += read;
                            if (total > MaxInstallerBytes)
                            {
                                throw new InvalidDataException("苹果驱动安装包大小异常，已停止下载。");
                            }
                            await destination.WriteAsync(buffer, 0, read, cancellationToken);
                            progress?.Report(contentLength.HasValue && contentLength.Value > 0
                                ? Math.Min(1.0, (double)total / contentLength.Value)
                                : -1.0);
                        }
                        await destination.FlushAsync(cancellationToken);
                    }
                    if (total == 0) throw new InvalidDataException("苹果驱动安装包为空，已停止下载。");
                    if (contentLength.HasValue && total != contentLength.Value)
                    {
                        throw new InvalidDataException("苹果驱动安装包下载不完整，已停止安装。");
                    }
                    string signatureReason;
                    if (!_signatureVerifier.IsValid(temporaryPath, out signatureReason))
                    {
                        throw new InvalidDataException("苹果驱动安装包签名校验失败：" + signatureReason);
                    }
                    File.Move(temporaryPath, installerPath, true);
                    progress?.Report(1.0);
                    return new AppleDriverDownloadResult
                    {
                        FilePath = installerPath,
                        BytesDownloaded = total,
                        ContentLength = contentLength
                    };
                }
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        internal static void ValidateAppleInstallerResponse(Uri finalUri, string mediaType)
        {
            if (finalUri == null || !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !IsAppleHost(finalUri.Host)
                || !finalUri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("苹果驱动下载地址不是受信任的 Apple 官方安装包。");
            }
            if (!string.IsNullOrWhiteSpace(mediaType)
                && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/x-msdownload", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/vnd.microsoft.portable-executable", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("苹果驱动下载响应不是 Windows 安装包。");
            }
        }

        private static bool IsAppleHost(string host)
        {
            string normalized = (host ?? "").TrimEnd('.').ToLowerInvariant();
            return normalized == "apple.com" || normalized.EndsWith(".apple.com", StringComparison.Ordinal);
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // The next download reuses the same temporary path.
            }
        }
    }
}
