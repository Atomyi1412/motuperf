using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Media;
using System.Windows.Media.Imaging;
#else
using SkiaSharp;
#endif

namespace CSharpIosPerfMonitor
{
    internal enum HarmonyScreenshotFailureKind
    {
        None,
        HdcUnavailable,
        HdcTimeout,
        DeviceUnavailable,
        AuthorizationRequired,
        PermissionDenied,
        UnsupportedCommand,
        RemoteFileMissing,
        EmptyImage,
        InvalidImage,
        BlackImage,
        CommandFailed
    }

    public sealed class ScreenshotService
    {
        private string _root;
        private readonly object _lock = new object();
        private CancellationTokenSource _sessionCts;
        private int _sessionGeneration;
        private bool _inflight;
        private double _lastElapsed = 0;
        private string _cachedAndroidDisplayId = "";
        private ScreenshotOrientation _cachedIosOrientation = ScreenshotOrientation.Unknown;

        public ScreenshotService(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
            Udid = "";
            Platform = "ios";
            ProductVersion = "";
            IntervalSec = 3;
        }

        public bool Enabled { get; set; }
        public int IntervalSec { get; set; }
        public string Udid { get; set; }
        public string Platform { get; set; }
        public string ProductVersion { get; set; }

        public void SetRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Screenshot root is required.", "root");
            Directory.CreateDirectory(root);
            lock (_lock) _root = root;
        }

        public event Action<ScreenshotInfo> ScreenshotReady;
        public event Action<string> Failed;

        public void Reset(CancellationToken parentToken)
        {
            CancellationTokenSource previous;
            lock (_lock)
            {
                previous = _sessionCts;
                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                _sessionGeneration++;
                _lastElapsed = 0;
                _cachedAndroidDisplayId = "";
                _cachedIosOrientation = ScreenshotOrientation.Unknown;
            }
            CancelAndDispose(previous);
        }

        public void Stop()
        {
            CancellationTokenSource previous;
            lock (_lock)
            {
                previous = _sessionCts;
                _sessionCts = null;
                _sessionGeneration++;
            }
            CancelAndDispose(previous);
        }

        public void CaptureDue(double elapsedSec, int index, CancellationToken token)
        {
            CancellationToken sessionToken;
            int generation;
            string root;
            lock (_lock)
            {
                if (token.IsCancellationRequested || _sessionCts == null || _sessionCts.IsCancellationRequested ||
                    !Enabled || _inflight || elapsedSec - _lastElapsed < Math.Max(3, IntervalSec))
                {
                    return;
                }
                generation = _sessionGeneration;
                sessionToken = _sessionCts.Token;
                _inflight = true;
                _lastElapsed = elapsedSec;
                root = _root;
            }
            Task.Run(async delegate
            {
                string path = "";
                try
                {
                    path = CreateScreenshotPath(root, index);
                    ScreenshotOrientation orientation = ScreenshotOrientation.Unknown;
                    ProcessResult result;
                    if (DeviceLookupService.IsHarmony(Platform))
                    {
                        result = await CaptureHarmonyAsync(path, sessionToken);
                    }
                    else if (DeviceLookupService.IsAndroid(Platform))
                    {
                        result = await CaptureAndroidAsync(path, sessionToken);
                    }
                    else
                    {
                        orientation = await ReadIosOrientationAsync(sessionToken);
                        result = await CaptureIosAsync(path, sessionToken);
                    }
                    if (!IsCurrentSession(generation))
                    {
                        TryDelete(path);
                        return;
                    }
                    if (result.ExitCode != 0)
                    {
                        Raise(Failed, "Screenshot failed: " + Clip(result.Stderr + result.Stdout));
                        TryDelete(path);
                        return;
                    }
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    {
                        Raise(Failed, "Screenshot file is empty.");
                        TryDelete(path);
                        return;
                    }
                    Raise(ScreenshotReady, new ScreenshotInfo
                    {
                        Timestamp = DateTime.Now,
                        ElapsedSec = elapsedSec,
                        Path = path,
                        Orientation = orientation
                    });
                }
                catch (OperationCanceledException)
                {
                    TryDelete(path);
                }
                catch (Exception ex)
                {
                    TryDelete(path);
                    if (IsCurrentSession(generation) && !sessionToken.IsCancellationRequested)
                    {
                        if (DeviceLookupService.IsHarmony(Platform))
                        {
                            HarmonyScreenshotFailureKind kind = ex is TimeoutException
                                ? HarmonyScreenshotFailureKind.HdcTimeout
                                : ex is FileNotFoundException
                                    ? HarmonyScreenshotFailureKind.HdcUnavailable
                                    : ClassifyHarmonyFailure(new ProcessResult(1, "", ex.Message), false, false, 0, false, false);
                            Raise(Failed, "Screenshot error[" + HarmonyScreenshotFailureCode(kind) + "]: " + HarmonyScreenshotFailureMessage(kind));
                        }
                        else
                        {
                            Raise(Failed, "Screenshot error: " + ex.Message);
                        }
                    }
                }
                finally
                {
                    lock (_lock)
                    {
                        // Keep the gate held until the cancelled process has
                        // actually returned, preventing overlapping captures
                        // across two consecutive sessions.
                        _inflight = false;
                    }
                }
            });
        }

        private bool IsCurrentSession(int generation)
        {
            lock (_lock)
            {
                return generation == _sessionGeneration &&
                    _sessionCts != null &&
                    !_sessionCts.IsCancellationRequested;
            }
        }

        private static void CancelAndDispose(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            cts.Dispose();
        }

        private Task<ProcessResult> CaptureIosAsync(string path, CancellationToken token)
        {
            if (IosLookupService.UsesRsd(ProductVersion))
            {
                return RuntimeTools.RunPythonAsync(
                    new[] { "-m", "pymobiledevice3", "developer", "dvt", "screenshot", path, "--userspace", "--udid", Udid },
                    20000,
                    token);
            }
            List<string> args = new List<string>();
            if (!string.IsNullOrWhiteSpace(Udid))
            {
                args.Add("-u");
                args.Add(Udid);
            }
            args.Add("screenshot");
            args.Add(path);
            return RuntimeTools.RunTideviceAsync(args, 15000, token);
        }

        private async Task<ProcessResult> CaptureHarmonyAsync(string path, CancellationToken token)
        {
            // Harmony's snapshot_display validates the suffix and only accepts
            // JPEG output on some vendor builds. Keep the local path stable;
            // the image loader validates the file content rather than its name.
            string remote = "/data/local/tmp/motuperf-" + Guid.NewGuid().ToString("N") + ".jpeg";
            ProcessResult lastResult = new ProcessResult(1, "", "鸿蒙截图命令尚未执行。");
            HarmonyScreenshotFailureKind lastFailure = HarmonyScreenshotFailureKind.CommandFailed;
            try
            {
                foreach (string[] captureCommand in HarmonyScreenshotCommands(remote))
                {
                    ProcessResult captured = await ProcessRunner.RunAsync(RuntimeTools.HdcExecutable,
                        HarmonyLookupService.TargetArgs(Udid, captureCommand), 15000, token);
                    lastResult = captured;
                    lastFailure = ClassifyHarmonyFailure(captured, false, false, 0, false, false);
                    if (lastFailure != HarmonyScreenshotFailureKind.None)
                    {
                        TryDelete(path);
                        continue;
                    }

                    // A failed receive must never leave a previous capture at the
                    // same path looking like a newly received screenshot.
                    TryDelete(path);
                    ProcessResult received = await ProcessRunner.RunAsync(RuntimeTools.HdcExecutable,
                        new[] { "-t", Udid, "file", "recv", remote, path }, 15000, token);
                    lastResult = received;
                    bool fileExists = File.Exists(path);
                    long fileLength = 0;
                    try { if (fileExists) fileLength = new FileInfo(path).Length; } catch { }
                    bool validImage = fileExists && IsValidHarmonyImage(path);
                    bool blackImage = validImage && IsNearSolidBlackImage(path);
                    lastFailure = ClassifyHarmonyFailure(received, true, fileExists, fileLength, validImage, blackImage);
                    if (lastFailure == HarmonyScreenshotFailureKind.None) return received;
                    TryDelete(path);
                }

                return new ProcessResult(
                    lastResult.ExitCode == 0 ? 1 : lastResult.ExitCode,
                    lastResult.Stdout,
                    "鸿蒙截图失败[" + HarmonyScreenshotFailureCode(lastFailure) + "]："
                    + HarmonyScreenshotFailureMessage(lastFailure)
                    + " 已尝试 snapshot_display 和 screencap -p。最后输出："
                    + Clip(lastResult.Stderr + lastResult.Stdout));
            }
            finally
            {
                try
                {
                    await ProcessRunner.RunAsync(RuntimeTools.HdcExecutable,
                        HarmonyLookupService.TargetArgs(Udid, "rm", "-f", remote), 3000, CancellationToken.None);
                }
                catch { }
            }
        }

        private async Task<ScreenshotOrientation> ReadIosOrientationAsync(CancellationToken token)
        {
            ScreenshotOrientation fallback;
            lock (_lock) fallback = _cachedIosOrientation;
            try
            {
                List<string> args = new List<string> { "-m", "pymobiledevice3", "springboard", "orientation" };
                if (!string.IsNullOrWhiteSpace(Udid))
                {
                    args.Add("--udid");
                    args.Add(Udid);
                }
                ProcessResult result = await RuntimeTools.RunPythonAsync(args, 2500, token);
                ScreenshotOrientation orientation = result.ExitCode == 0
                    ? ParseIosOrientation(result.Stdout)
                    : ScreenshotOrientation.Unknown;
                if (orientation == ScreenshotOrientation.Unknown) return fallback;
                lock (_lock) _cachedIosOrientation = orientation;
                return orientation;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return fallback;
            }
        }

        internal static ScreenshotOrientation ParseIosOrientation(string output)
        {
            Match match = Regex.Match(output ?? "", @"(?m)^\s*([1-4])\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) return ScreenshotOrientation.Unknown;
            int value;
            return int.TryParse(match.Groups[1].Value, out value)
                ? (ScreenshotOrientation)value
                : ScreenshotOrientation.Unknown;
        }

        private async Task<ProcessResult> CaptureAndroidAsync(string path, CancellationToken token)
        {
            List<string> displayIds = await AndroidDisplayIdsAsync(token);
            ProcessResult lastResult = new ProcessResult(1, "", "Android screenshot was not attempted.");
            string tried = "";
            string blackFrames = "";

            foreach (string displayId in displayIds)
            {
                ProcessResult direct = await CaptureAndroidExecOutAsync(path, displayId, token);
                lastResult = direct;
                AddTried(ref tried, displayId);
                if (direct.ExitCode == 0 && NormalizeAndroidPngFile(path))
                {
                    if (IsUsableAndroidPng(path))
                    {
                        _cachedAndroidDisplayId = displayId;
                        return direct;
                    }
                    AddTried(ref blackFrames, displayId);
                }
                TryDelete(path);
            }

            foreach (string displayId in displayIds)
            {
                ProcessResult fallback = await CaptureAndroidViaRemoteFileAsync(path, displayId, token);
                lastResult = fallback;
                if (fallback.ExitCode == 0 && NormalizeAndroidPngFile(path))
                {
                    if (IsUsableAndroidPng(path))
                    {
                        _cachedAndroidDisplayId = displayId;
                        return fallback;
                    }
                    AddTried(ref blackFrames, displayId);
                }
                TryDelete(path);
            }

            _cachedAndroidDisplayId = "";
            string error = Clip(lastResult.Stderr + lastResult.Stdout);
            string reason = string.IsNullOrWhiteSpace(blackFrames)
                ? "Android screenshot did not produce a decodable PNG."
                : "Android screenshot produced only black frames for display ids: " + blackFrames + ".";
            return new ProcessResult(
                lastResult.ExitCode == 0 ? 1 : lastResult.ExitCode,
                lastResult.Stdout,
                reason + " tried display ids: " + tried + ". last output: " + error);
        }

        private Task<ProcessResult> CaptureAndroidExecOutAsync(string path, string displayId, CancellationToken token)
        {
            return string.IsNullOrWhiteSpace(displayId)
                ? ProcessRunner.RunToFileAsync(RuntimeTools.AdbExecutable, AndroidLookupService.AdbExecOutArgs(Udid, "screencap", "-p"), path, 15000, token)
                : ProcessRunner.RunToFileAsync(RuntimeTools.AdbExecutable, AndroidLookupService.AdbExecOutArgs(Udid, "screencap", "-p", "-d", displayId), path, 15000, token);
        }

        private async Task<ProcessResult> CaptureAndroidViaRemoteFileAsync(string path, string displayId, CancellationToken token)
        {
            string remote = "/sdcard/motuperf-screenshot-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png";
            string capture = string.IsNullOrWhiteSpace(displayId)
                ? "shell screencap -p " + remote
                : "shell screencap -p -d " + displayId + " " + remote;
            ProcessResult captureResult = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, capture), 15000, token);
            if (captureResult.ExitCode != 0) return captureResult;
            ProcessResult pullResult = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, "pull " + remote + " \"" + path + "\""), 15000, token);
            await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, "shell rm " + remote), 5000, token);
            return pullResult;
        }

        private async Task<string> AndroidDisplayIdAsync(CancellationToken token)
        {
            List<string> ids = await AndroidDisplayIdsAsync(token);
            return ids.Count == 0 ? "" : ids[0];
        }

        private async Task<List<string>> AndroidDisplayIdsAsync(CancellationToken token)
        {
            List<string> ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(_cachedAndroidDisplayId))
            {
                AddDisplayId(ids, _cachedAndroidDisplayId);
            }
            try
            {
                ProcessResult help = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, "shell screencap -h"), 5000, token);
                AddDisplayId(ids, ParseScreencapDefaultDisplayId(help.Stdout + help.Stderr));
            }
            catch
            {
            }

            try
            {
                ProcessResult display = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, "shell dumpsys display"), 7000, token);
                foreach (string id in ParseActiveDisplayIds(display.Stdout))
                {
                    AddDisplayId(ids, id);
                }
            }
            catch
            {
            }

            try
            {
                ProcessResult surface = await ProcessRunner.RunAsync(RuntimeTools.AdbExecutable, AndroidLookupService.SerialArgs(Udid, "shell dumpsys SurfaceFlinger --display-id"), 5000, token);
                foreach (string id in ParseDisplayIds(surface.Stdout))
                {
                    AddDisplayId(ids, id);
                }
            }
            catch
            {
            }

            AddDisplayId(ids, "");
            return ids;
        }

        private static string ParseScreencapDefaultDisplayId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            Match match = Regex.Match(text, @"defaults\s+to\s+([0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static List<string> ParseActiveDisplayIds(string text)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return ids;
            foreach (Match viewport in Regex.Matches(text, @"DisplayViewport\{[^}]*\}", RegexOptions.IgnoreCase))
            {
                string body = viewport.Value;
                if (body.IndexOf("isActive=true", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match id = Regex.Match(body, @"uniqueId='local:([0-9]+)'", RegexOptions.IgnoreCase);
                if (id.Success) AddDisplayId(ids, id.Groups[1].Value);
            }
            return ids;
        }

        private static List<string> ParseDisplayIds(string text)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return ids;
            foreach (Match display in Regex.Matches(text, @"Display\s+([0-9]+)", RegexOptions.IgnoreCase))
            {
                AddDisplayId(ids, display.Groups[1].Value);
            }
            return ids;
        }

        private static bool NormalizeAndroidPngFile(string path)
        {
            if (AndroidLookupService.IsValidPng(path)) return true;
            int offset = PngOffset(path);
            if (offset < 0) return false;
            byte[] bytes = File.ReadAllBytes(path);
            byte[] normalized = new byte[bytes.Length - offset];
            Buffer.BlockCopy(bytes, offset, normalized, 0, normalized.Length);
            string temp = path + ".pngfix";
            File.WriteAllBytes(temp, normalized);
            if (!AndroidLookupService.IsValidPng(temp))
            {
                TryDelete(temp);
                return false;
            }
            TryDelete(path);
            File.Move(temp, path);
            return true;
        }

        private static bool IsUsableAndroidPng(string path)
        {
            return AndroidLookupService.IsValidPng(path) && !IsNearSolidBlackPng(path);
        }

        // Keep the Android-specific name as a source contract for existing
        // checks; Harmony uses the same image-content test after receiving a
        // JPEG even though its local capture path ends in .png.
        private static bool IsNearSolidBlackPng(string path)
        {
            return IsNearSolidBlackImage(path);
        }

        private static bool IsValidHarmonyImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
#if WINDOWS
                using (FileStream stream = File.OpenRead(path))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames.Count > 0
                        && decoder.Frames[0].PixelWidth > 0
                        && decoder.Frames[0].PixelHeight > 0;
                }
#else
                using (SKCodec codec = SKCodec.Create(path))
                {
                    return codec != null && codec.Info.Width > 0 && codec.Info.Height > 0;
                }
#endif
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNearSolidBlackImage(string path)
        {
            try
            {
#if WINDOWS
                using (FileStream stream = File.OpenRead(path))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return true;
                    BitmapSource source = decoder.Frames[0];
                    if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return true;
                    if (source.Format != PixelFormats.Bgra32)
                    {
                        source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    }

                    int width = source.PixelWidth;
                    int height = source.PixelHeight;
                    int stride = width * 4;
                    byte[] pixels = new byte[stride * height];
                    source.CopyPixels(pixels, stride, 0);

                    int totalPixels = width * height;
                    int step = Math.Max(1, totalPixels / 20000);
                    int samples = 0;
                    int blackish = 0;
                    int nonDark = 0;
                    long brightness = 0;
                    for (int pixel = 0; pixel < totalPixels; pixel += step)
                    {
                        int offset = pixel * 4;
                        byte b = pixels[offset];
                        byte g = pixels[offset + 1];
                        byte r = pixels[offset + 2];
                        byte a = pixels[offset + 3];
                        if (a < 16) continue;
                        samples++;
                        int max = Math.Max(r, Math.Max(g, b));
                        brightness += r + g + b;
                        if (r <= 3 && g <= 3 && b <= 3) blackish++;
                        if (max > 8) nonDark++;
                    }
                    if (samples == 0) return true;
                    double meanBrightness = brightness / (samples * 3.0);
                    double blackishRatio = blackish / (double)samples;
                    double nonDarkRatio = nonDark / (double)samples;
                    return meanBrightness < 0.75 && blackishRatio > 0.995 && nonDarkRatio < 0.003;
                }
#else
                using (SKBitmap bitmap = SKBitmap.Decode(path))
                {
                    if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return true;
                    long totalPixels = (long)bitmap.Width * bitmap.Height;
                    int step = (int)Math.Max(1, totalPixels / 20000);
                    int samples = 0;
                    int blackish = 0;
                    int nonDark = 0;
                    long brightness = 0;
                    for (long pixel = 0; pixel < totalPixels; pixel += step)
                    {
                        int x = (int)(pixel % bitmap.Width);
                        int y = (int)(pixel / bitmap.Width);
                        SKColor color = bitmap.GetPixel(x, y);
                        if (color.Alpha < 16) continue;
                        samples++;
                        int max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
                        brightness += color.Red + color.Green + color.Blue;
                        if (color.Red <= 3 && color.Green <= 3 && color.Blue <= 3) blackish++;
                        if (max > 8) nonDark++;
                    }
                    if (samples == 0) return true;
                    double meanBrightness = brightness / (samples * 3.0);
                    double blackishRatio = blackish / (double)samples;
                    double nonDarkRatio = nonDark / (double)samples;
                    return meanBrightness < 0.75 && blackishRatio > 0.995 && nonDarkRatio < 0.003;
                }
#endif
            }
            catch
            {
                return true;
            }
        }

        private static int PngOffset(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return -1;
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i <= bytes.Length - 8; i++)
            {
                if (bytes[i] == 0x89 && bytes[i + 1] == 0x50 && bytes[i + 2] == 0x4E && bytes[i + 3] == 0x47 &&
                    bytes[i + 4] == 0x0D && bytes[i + 5] == 0x0A && bytes[i + 6] == 0x1A && bytes[i + 7] == 0x0A)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static void AddDisplayId(List<string> ids, string id)
        {
            id = (id ?? "").Trim();
            foreach (string existing in ids)
            {
                if (string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)) return;
            }
            ids.Add(id);
        }

        private static void AddTried(ref string text, string id)
        {
            string label = string.IsNullOrWhiteSpace(id) ? "<default>" : id.Trim();
            if (text.Length > 0) text += ", ";
            text += label;
        }

        private static string Clip(string text)
        {
            text = string.Join(" ", (text ?? "").Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 180 ? text : text.Substring(0, 180) + "...";
        }

        private static void Raise<T>(Action<T> handler, T value)
        {
            if (handler != null) handler(value);
        }

        internal static IReadOnlyList<string[]> HarmonyScreenshotCommands(string remote)
        {
            return new[]
            {
                new[] { "snapshot_display", "-f", remote ?? "" },
                new[] { "screencap", "-p", remote ?? "" }
            };
        }

        internal static bool IsHarmonyCaptureFailure(ProcessResult result)
        {
            return ClassifyHarmonyFailure(result, false, false, 0, false, false)
                != HarmonyScreenshotFailureKind.None;
        }

        internal static string CreateScreenshotPath(string root, int index)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            return Path.Combine(root ?? "", stamp + "-" + index.ToString("00000", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".png");
        }

        internal static HarmonyScreenshotFailureKind ClassifyHarmonyFailure(
            ProcessResult result,
            bool receivingRemoteFile,
            bool fileExists,
            long fileLength,
            bool validImage,
            bool blackImage)
        {
            string output = (result == null ? "" : (result.Stdout ?? "") + "\n" + (result.Stderr ?? ""));
            HarmonyScreenshotFailureKind outputFailure = ClassifyHarmonyOutput(output, receivingRemoteFile);
            if (outputFailure != HarmonyScreenshotFailureKind.None) return outputFailure;
            if (result == null || result.ExitCode != 0) return HarmonyScreenshotFailureKind.CommandFailed;
            if (!receivingRemoteFile) return HarmonyScreenshotFailureKind.None;
            if (!fileExists) return HarmonyScreenshotFailureKind.RemoteFileMissing;
            if (fileLength <= 0) return HarmonyScreenshotFailureKind.EmptyImage;
            if (!validImage) return HarmonyScreenshotFailureKind.InvalidImage;
            if (blackImage) return HarmonyScreenshotFailureKind.BlackImage;
            return HarmonyScreenshotFailureKind.None;
        }

        private static HarmonyScreenshotFailureKind ClassifyHarmonyOutput(string output, bool receivingRemoteFile)
        {
            string value = output ?? "";
            if (Regex.IsMatch(value, @"(?i)(permission\s+denied|access\s+denied|not\s+permitted)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.PermissionDenied;
            if (Regex.IsMatch(value, @"(?i)(unauthori[sz]ed|not\s+authori[sz]ed|auth(?:entication)?\s+(?:failed|required)|not\s+paired)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.AuthorizationRequired;
            if (Regex.IsMatch(value, @"(?i)(?:device|target)\s+(?:not\s+found|not\s+founded|not\s+connected|offline)|no\s+(?:device|target)s?\b|unable\s+to\s+enumerate", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.DeviceUnavailable;
            if (receivingRemoteFile && Regex.IsMatch(value, @"(?i)(no\s+such\s+file|cannot\s+stat|file\s+not\s+found|remote\s+file|does\s+not\s+exist|not\s+exist)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.RemoteFileMissing;
            if (!receivingRemoteFile && Regex.IsMatch(value, @"(?i)(no\s+such\s+file|cannot\s+stat|file\s+not\s+found|does\s+not\s+exist|not\s+exist)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.CommandFailed;
            if (Regex.IsMatch(value, @"(?i)(command\s+not\s+found|unknown\s+command|unsupported|not\s+support|invalid\s+(?:option|command)|unrecognized\s+option)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.UnsupportedCommand;
            if (Regex.IsMatch(value, @"(?i)(?:\[fail\]|snapshot\s+failed|capture\s+failed)", RegexOptions.CultureInvariant))
                return HarmonyScreenshotFailureKind.CommandFailed;
            return HarmonyScreenshotFailureKind.None;
        }

        private static string HarmonyScreenshotFailureCode(HarmonyScreenshotFailureKind kind)
        {
            switch (kind)
            {
                case HarmonyScreenshotFailureKind.HdcUnavailable: return "hdc_missing";
                case HarmonyScreenshotFailureKind.HdcTimeout: return "hdc_timeout";
                case HarmonyScreenshotFailureKind.DeviceUnavailable: return "device_unavailable";
                case HarmonyScreenshotFailureKind.AuthorizationRequired: return "authorization_required";
                case HarmonyScreenshotFailureKind.PermissionDenied: return "permission_denied";
                case HarmonyScreenshotFailureKind.UnsupportedCommand: return "unsupported_command";
                case HarmonyScreenshotFailureKind.RemoteFileMissing: return "remote_file_missing";
                case HarmonyScreenshotFailureKind.EmptyImage: return "empty_image";
                case HarmonyScreenshotFailureKind.InvalidImage: return "invalid_image";
                case HarmonyScreenshotFailureKind.BlackImage: return "black_image";
                default: return "command_failed";
            }
        }

        private static string HarmonyScreenshotFailureMessage(HarmonyScreenshotFailureKind kind)
        {
            switch (kind)
            {
                case HarmonyScreenshotFailureKind.HdcUnavailable:
                    return "未找到 HDC，请在设备选择页点击“下载鸿蒙连接工具”，只下载官方 Command Line Tools，解压后选择其中的 HDC 文件。";
                case HarmonyScreenshotFailureKind.HdcTimeout:
                    return "HDC 响应超时，请检查设备连接和调试授权。";
                case HarmonyScreenshotFailureKind.DeviceUnavailable:
                    return "设备未连接、离线或 HDC 未建立通信，请检查 USB 连接后重试。";
                case HarmonyScreenshotFailureKind.AuthorizationRequired:
                    return "设备未授权，请解锁设备并允许当前电脑进行调试。";
                case HarmonyScreenshotFailureKind.PermissionDenied:
                    return "设备拒绝截图权限，请确认开发者调试权限后重试。";
                case HarmonyScreenshotFailureKind.UnsupportedCommand:
                    return "设备不支持当前截图命令，已尝试兼容命令仍未成功。";
                case HarmonyScreenshotFailureKind.RemoteFileMissing:
                    return "设备端截图文件未生成或接收失败。";
                case HarmonyScreenshotFailureKind.EmptyImage:
                    return "设备端返回了空截图文件。";
                case HarmonyScreenshotFailureKind.InvalidImage:
                    return "收到的文件不是有效截图图像。";
                case HarmonyScreenshotFailureKind.BlackImage:
                    return "收到的截图为纯黑图，未将其作为有效截图。";
                default:
                    return "HDC 截图命令执行失败。";
            }
        }
    }
}
