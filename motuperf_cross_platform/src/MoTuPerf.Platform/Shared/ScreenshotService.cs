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
                    path = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + index.ToString("00000") + ".png");
                    ScreenshotOrientation orientation = ScreenshotOrientation.Unknown;
                    ProcessResult result;
                    if (DeviceLookupService.IsAndroid(Platform))
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
                        Raise(Failed, "Screenshot error: " + ex.Message);
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

        private static bool IsNearSolidBlackPng(string path)
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
    }
}
