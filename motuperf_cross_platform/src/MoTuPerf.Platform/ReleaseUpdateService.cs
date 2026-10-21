using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MoTuPerf.Platform
{
    public sealed class UpdateAsset
    {
        public string FileName { get; internal set; }
        public string Url { get; internal set; }
        public string Sha256 { get; internal set; }
        public string Target { get; internal set; }
    }

    public sealed class UpdateManifest
    {
        public string Version { get; internal set; }
        public bool Mandatory { get; internal set; }
        public string ReleaseNotesUrl { get; internal set; }
        public IReadOnlyList<string> ReleaseNotes { get; internal set; } = Array.Empty<string>();
        public DateTimeOffset? PublishedAtUtc { get; internal set; }
        public UpdateAsset Asset { get; internal set; }
    }

    public sealed class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public UpdateManifest Manifest { get; set; }
        public string Message { get; set; }
    }

    public sealed class UpdateDownloadProgress
    {
        public long BytesReceived { get; internal set; }
        public long? TotalBytes { get; internal set; }
        public double? Percent { get; internal set; }
        public string Status { get; internal set; }
    }

    public sealed class ReleaseUpdateService
    {
        public const string RepositoryOwner = "Atomyi1412";
        public const string RepositoryName = "motuperf";
        public const string LatestManifestUrl = "https://github.com/Atomyi1412/motuperf/releases/latest/download/latest.json";

        private static readonly Regex VersionPattern = new Regex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
        private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
        private const long ProgressReportByteInterval = 1024 * 1024;
        private const int DefaultMaxRetryAttempts = 5;
        private readonly HttpClient _httpClient;
        private readonly TimeSpan _readTimeout;
        private readonly int _maxRetryAttempts;

        public ReleaseUpdateService(HttpClient httpClient = null)
            : this(httpClient, DefaultReadTimeout, DefaultMaxRetryAttempts)
        {
        }

        internal ReleaseUpdateService(HttpClient httpClient, TimeSpan readTimeout, int maxRetryAttempts)
        {
            _httpClient = httpClient ?? CreateHttpClient();
            if (readTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readTimeout));
            if (maxRetryAttempts < 0) throw new ArgumentOutOfRangeException(nameof(maxRetryAttempts));
            _readTimeout = readTimeout;
            _maxRetryAttempts = maxRetryAttempts;
        }

        public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken)
        {
            if (!TryParseVersion(currentVersion, out Version current))
                return new UpdateCheckResult { Message = "当前版本号格式无法识别" };

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using (HttpResponseMessage response = await _httpClient.GetAsync(LatestManifestUrl, timeout.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                UpdateManifest manifest = ParseManifest(json);
                if (!TryParseVersion(manifest.Version, out Version latest))
                    throw new InvalidDataException("更新清单中的版本号无效。");
                if (latest <= current)
                    return new UpdateCheckResult { Message = "当前已是最新版本" };

                string target = GetCurrentTarget();
                UpdateAsset asset = manifest.Asset;
                if (asset == null || !string.Equals(asset.Target, target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新清单没有当前平台的安装包。");
                return new UpdateCheckResult { HasUpdate = true, Manifest = manifest, Message = "发现新版本 v" + manifest.Version };
            }
        }

        public async Task<string> DownloadAsync(UpdateAsset asset, string directory, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
        {
            ValidateAsset(asset);
            cancellationToken.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(directory ?? "");
            Directory.CreateDirectory(root);
            string finalPath = Path.Combine(root, asset.FileName);
            string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                long received = 0;
                int retryAttempts = 0;
                long? totalBytes = null;
                byte[] buffer = new byte[128 * 1024];
                long lastProgressBytes = -1;
                DateTime lastProgressAt = DateTime.MinValue;
                void ReportProgress(string status = null, bool force = false)
                {
                    if (progress == null) return;
                    DateTime now = DateTime.UtcNow;
                    if (!force && lastProgressBytes >= 0
                        && received - lastProgressBytes < ProgressReportByteInterval
                        && now - lastProgressAt < ProgressReportInterval)
                        return;
                    lastProgressBytes = received;
                    lastProgressAt = now;
                    progress.Report(new UpdateDownloadProgress
                    {
                        BytesReceived = received,
                        TotalBytes = totalBytes,
                        Percent = totalBytes.HasValue && totalBytes.Value > 0 ? (double?)received * 100d / totalBytes.Value : null,
                        Status = status
                    });
                }
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(asset.Url)))
                        {
                            if (received > 0)
                                request.Headers.Range = new RangeHeaderValue(received, null);
                            using (HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                            {
                                ValidateDownloadResponse(response, received);
                                totalBytes = GetTotalBytes(response, received, totalBytes);
                                using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                                using (FileStream destination = new FileStream(temporaryPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                                {
                                    destination.Position = received;
                                    int count;
                                    while (true)
                                    {
                                        using (CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                                        {
                                            readTimeout.CancelAfter(_readTimeout);
                                            try
                                            {
                                                count = await source.ReadAsync(buffer, 0, buffer.Length, readTimeout.Token).ConfigureAwait(false);
                                            }
                                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                            {
                                                throw new TimeoutException("更新包下载暂时没有数据，正在重新连接。");
                                            }
                                            catch (IOException exception)
                                            {
                                                throw new DownloadReadException("更新包下载流读取失败，正在重新连接。", exception);
                                            }
                                        }

                                        if (count == 0) break;
                                        await destination.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                                        received += count;
                                        retryAttempts = 0;
                                        ReportProgress();
                                    }
                                }
                            }
                        }

                        if (!totalBytes.HasValue || received >= totalBytes.Value) break;
                        throw new EndOfStreamException("更新包下载提前结束，正在重新连接。");
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        retryAttempts = await RetryDownloadAsync(retryAttempts, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        retryAttempts = await RetryDownloadAsync(retryAttempts, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException)
                    {
                        retryAttempts = await RetryDownloadAsync(retryAttempts, cancellationToken).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        retryAttempts = await RetryDownloadAsync(retryAttempts, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DownloadReadException)
                    {
                        retryAttempts = await RetryDownloadAsync(retryAttempts, cancellationToken).ConfigureAwait(false);
                    }

                    ReportProgress("网络暂时无数据，正在重新连接（" + retryAttempts + "/" + _maxRetryAttempts + "）", true);
                }

                string actualHash;
                using (FileStream hashStream = File.OpenRead(temporaryPath))
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败。");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, finalPath, true);
                long finalLength = new FileInfo(finalPath).Length;
                progress?.Report(new UpdateDownloadProgress { BytesReceived = finalLength, TotalBytes = finalLength, Percent = 100 });
                return finalPath;
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        private async Task<int> RetryDownloadAsync(int retryAttempts, CancellationToken cancellationToken)
        {
            retryAttempts++;
            if (retryAttempts > _maxRetryAttempts)
                throw new IOException("更新包下载多次中断，请检查网络后重试。");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            return retryAttempts;
        }

        private sealed class DownloadReadException : IOException
        {
            public DownloadReadException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private static void ValidateDownloadResponse(HttpResponseMessage response, long received)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException("更新服务器返回 HTTP " + (int)response.StatusCode + "。");
            }

            if (received == 0)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.PartialContent
                    && response.Content.Headers.ContentRange?.From != 0)
                    throw new InvalidDataException("更新服务器返回的下载范围无效。");
                return;
            }

            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent
                || response.Content.Headers.ContentRange?.From != received)
                throw new InvalidDataException("更新服务器不支持从断点继续下载。");
        }

        private static long? GetTotalBytes(HttpResponseMessage response, long received, long? previousTotal)
        {
            long? rangeLength = response.Content.Headers.ContentRange?.Length;
            if (rangeLength.HasValue) return rangeLength;
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue) return received + contentLength.Value;
            return previousTotal;
        }

        public static string GetCurrentTarget()
        {
            if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
                return "win-x64";
            if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
                return "osx-arm64";
            return "";
        }

        public static ProcessStartInfo CreateInstallerStartInfo(string installerPath, string installationDirectory)
        {
            var startInfo = new ProcessStartInfo { FileName = Path.GetFullPath(installerPath), UseShellExecute = true };
            if (OperatingSystem.IsWindows())
            {
                string directory = Path.GetFullPath(installationDirectory);
                if (directory.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                    throw new ArgumentException("安装目录无效。", nameof(installationDirectory));
                // NSIS consumes everything after the final, unquoted /D=, including spaces.
                startInfo.Arguments = "/D=" + directory;
            }
            return startInfo;
        }

        public static bool TryParseVersion(string value, out Version version)
        {
            version = null;
            string normalized = (value ?? "").Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            if (!VersionPattern.IsMatch(normalized)) return false;
            string[] parts = normalized.Split('.');
            if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) || !int.TryParse(parts[2], out int patch)) return false;
            version = new Version(major, minor, patch);
            return true;
        }

        internal static UpdateManifest ParseManifest(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json ?? ""))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out JsonElement schema) || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 1)
                    throw new InvalidDataException("更新清单版本不受支持。");
                string version = RequiredString(root, "version");
                if (!TryParseVersion(version, out Version parsedVersion)) throw new InvalidDataException("更新清单中的版本号无效。");
                version = parsedVersion.ToString(3);
                string notes = OptionalString(root, "releaseNotesUrl");
                if (!string.IsNullOrWhiteSpace(notes) && !IsGithubReleaseNotesUrl(notes)) throw new InvalidDataException("更新说明地址不受信任。");
                JsonElement assets = RequiredObject(root, "assets");
                string target = GetCurrentTarget();
                if (string.IsNullOrWhiteSpace(target)) throw new PlatformNotSupportedException("当前系统或 CPU 架构不支持自动更新。");
                JsonElement assetElement;
                if (!assets.TryGetProperty(target, out assetElement)) throw new InvalidDataException("缺少当前平台的更新包。");
                UpdateAsset asset = new UpdateAsset
                {
                    Target = target,
                    FileName = RequiredString(assetElement, "fileName"),
                    Url = RequiredString(assetElement, "url"),
                    Sha256 = RequiredString(assetElement, "sha256").ToLowerInvariant()
                };
                ValidateAsset(asset);
                if (asset.FileName != ExpectedFileName(version, target)
                    || asset.Url != ReleaseBaseUrl + "/download/v" + version + "/" + asset.FileName)
                    throw new InvalidDataException("更新包文件名、下载标签和版本不一致。");
                if (!string.IsNullOrWhiteSpace(notes) && notes != ReleaseBaseUrl + "/tag/v" + version)
                    throw new InvalidDataException("更新说明与版本不一致。");
                DateTimeOffset published;
                return new UpdateManifest
                {
                    Version = version,
                    Mandatory = root.TryGetProperty("mandatory", out JsonElement mandatory) && mandatory.ValueKind == JsonValueKind.True,
                    ReleaseNotesUrl = notes,
                    ReleaseNotes = ReadReleaseNotes(root),
                    PublishedAtUtc = DateTimeOffset.TryParse(OptionalString(root, "publishedAtUtc"), out published) ? (DateTimeOffset?)published : null,
                    Asset = asset
                };
            }
        }

        internal static void ValidateAsset(UpdateAsset asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.FileName) || Path.GetFileName(asset.FileName) != asset.FileName)
                throw new InvalidDataException("更新包文件名无效。");
            if (string.IsNullOrWhiteSpace(asset.Target) || !string.Equals(asset.Target, GetCurrentTarget(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包平台与当前系统不匹配。");
            if (!IsGithubReleaseAssetUrl(asset.Url)) throw new InvalidDataException("更新包地址不受信任。");
            if (string.Equals(asset.Target, "win-x64", StringComparison.OrdinalIgnoreCase) && !asset.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows 更新包格式无效。");
            if (string.Equals(asset.Target, "osx-arm64", StringComparison.OrdinalIgnoreCase) && !asset.FileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("macOS 更新包格式无效。");
            if (!Regex.IsMatch(asset.Sha256 ?? "", "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("更新包 SHA-256 无效。");
        }

        private static IReadOnlyList<string> ReadReleaseNotes(JsonElement root)
        {
            if (!root.TryGetProperty("releaseNotes", out JsonElement notes) || notes.ValueKind != JsonValueKind.Array
                || notes.GetArrayLength() > 24) return Array.Empty<string>();
            var lines = new List<string>();
            foreach (JsonElement line in notes.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String) return Array.Empty<string>();
                string text = line.GetString().Trim();
                if (text.Length > 1000) return Array.Empty<string>();
                if (text.Length > 0) lines.Add(text);
            }
            return lines.AsReadOnly();
        }

        private const string ReleaseBaseUrl = "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases";

        internal static string ExpectedFileName(string version, string target)
        {
            return target == "win-x64" ? "MoTuPerf-Setup-v" + version + ".exe" : "MoTuPerf-v" + version + "-osx-arm64.dmg";
        }

        internal static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MoTuPerf-Updater/1.0");
            return client;
        }

        private static bool IsGithubHttpsUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGithubReleaseAssetUrl(string value)
        {
            if (!IsGithubHttpsUrl(value)) return false;
            Uri uri = new Uri(value);
            string prefix = "/" + RepositoryOwner + "/" + RepositoryName + "/releases/download/";
            return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGithubReleaseNotesUrl(string value)
        {
            if (!IsGithubHttpsUrl(value)) return false;
            Uri uri = new Uri(value);
            string prefix = "/" + RepositoryOwner + "/" + RepositoryName + "/releases/";
            return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string RequiredString(JsonElement element, string name)
        {
            string value = OptionalString(element, name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("更新清单缺少 " + name + "。");
            return value;
        }

        private static string OptionalString(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
        }

        private static JsonElement RequiredObject(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("更新清单缺少 " + name + "。");
            return value;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
