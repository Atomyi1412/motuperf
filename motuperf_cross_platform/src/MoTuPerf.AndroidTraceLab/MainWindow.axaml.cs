using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MoTuPerf.AndroidTraceLab;

public sealed class ProcessItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Package { get; init; } = "";
    public override string ToString() => $"{Package}  (PID {Pid})";
}

public partial class MainWindow : Window
{
    private ComboBox DeviceCombo = null!;
    private Button RefreshButton = null!;
    private TextBox TargetText = null!;
    private Button StartButton = null!;
    private Button StopButton = null!;
    private TextBlock ProcessHint = null!;
    private ListBox ProcessList = null!;
    private TextBlock ConnectionState = null!;
    private TextBlock FpsValue = null!;
    private TextBlock JankValue = null!;
    private TextBlock FrameTimeValue = null!;
    private TextBlock WindowValue = null!;
    private TextBlock LogText = null!;

    private readonly ObservableCollection<string> _devices = new();
    private readonly ObservableCollection<ProcessItem> _processes = new();
    private Process? _runner;
    private CancellationTokenSource? _runnerCts;
    private int _validWindows;

    public MainWindow()
    {
        InitializeComponent();
        DeviceCombo.ItemsSource = _devices;
        ProcessList.ItemsSource = _processes;
        Closing += WindowClosing;
        Opened += async (_, _) => await RefreshAsync();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        DeviceCombo = this.FindControl<ComboBox>("DeviceCombo");
        RefreshButton = this.FindControl<Button>("RefreshButton");
        TargetText = this.FindControl<TextBox>("TargetText");
        StartButton = this.FindControl<Button>("StartButton");
        StopButton = this.FindControl<Button>("StopButton");
        ProcessHint = this.FindControl<TextBlock>("ProcessHint");
        ProcessList = this.FindControl<ListBox>("ProcessList");
        ConnectionState = this.FindControl<TextBlock>("ConnectionState");
        FpsValue = this.FindControl<TextBlock>("FpsValue");
        JankValue = this.FindControl<TextBlock>("JankValue");
        FrameTimeValue = this.FindControl<TextBlock>("FrameTimeValue");
        WindowValue = this.FindControl<TextBlock>("WindowValue");
        LogText = this.FindControl<TextBlock>("LogText");
    }

    private string BaseDirectory => AppContext.BaseDirectory;

    private string AdbPath => ExistingPath(
        Path.Combine(BaseDirectory, "runtime", "android", "adb.exe"), "adb.exe");

    private string PythonPath => ExistingPath(
        Path.Combine(BaseDirectory, "runtime", "python", "python.exe"), "python.exe");

    private string TraceProcessorPath => ExistingPath(
        Path.Combine(BaseDirectory, "trace_processor_shell.exe"),
        Path.Combine(BaseDirectory, "research", "trace_processor_shell.exe"));

    private static string ExistingPath(params string[] paths) =>
        paths.FirstOrDefault(File.Exists) ?? paths[0];

    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_runner is not null && !_runner.HasExited)
        {
            AppendLog("采集中不能刷新目标进程，请先停止实验。", "warning");
            return;
        }

        RefreshButton.IsEnabled = false;
        ProcessHint.Text = "正在读取 adb 设备和进程...";
        try
        {
            var devices = await RunCommandAsync(AdbPath, new[] { "devices" }, CancellationToken.None);
            if (devices.ExitCode != 0) throw new InvalidOperationException(devices.ErrorOrOutput("adb devices 失败"));
            _devices.Clear();
            foreach (var line in devices.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device") _devices.Add(parts[0]);
            }
            if (_devices.Count == 0)
            {
                _processes.Clear();
                ProcessHint.Text = "未发现已授权设备，请检查 USB 调试授权。";
                AppendLog("没有检测到已授权的 Android 设备。", "error");
                return;
            }
            DeviceCombo.SelectedIndex = Math.Clamp(DeviceCombo.SelectedIndex, 0, _devices.Count - 1);
            var serial = (string?)DeviceCombo.SelectedItem ?? _devices[0];
            var packageResult = await RunCommandAsync(AdbPath, new[] { "-s", serial, "shell", "pm", "list", "packages", "-3" }, CancellationToken.None);
            var userPackages = ParseUserPackages(packageResult.Output);
            var result = await RunCommandAsync(AdbPath, new[] { "-s", serial, "shell", "ps", "-A", "-o", "PID,NAME" }, CancellationToken.None);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.ErrorOrOutput("读取进程失败"));
            _processes.Clear();
            foreach (var row in ParseProcesses(result.Output, userPackages)) _processes.Add(row);
            ProcessHint.Text = $"已读取 {_processes.Count} 个可选进程；双击目标进程填入。";
            AppendLog($"设备 {serial} 已连接，读取 {_processes.Count} 个候选进程。", "status");
        }
        catch (Exception ex)
        {
            ProcessHint.Text = "读取失败";
            AppendLog(ex.Message, "error");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static HashSet<string> ParseUserPackages(string output) => output.Split('\n')
        .Select(line => line.Trim().StartsWith("package:", StringComparison.Ordinal)
            ? line.Trim()["package:".Length..] : "")
        .Where(package => package.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<ProcessItem> ParseProcesses(string output, HashSet<string> userPackages)
    {
        var seen = new HashSet<(int, string)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) continue;
            var name = parts[1].Trim();
            if (pid <= 0 || name.Length == 0 || name.StartsWith("[") || !name.Contains('.')) continue;
            var package = name.Split(':', 2)[0];
            if (!userPackages.Contains(package)) continue;
            if (seen.Add((pid, name))) yield return new ProcessItem { Pid = pid, Name = name, Package = package };
        }
    }

    private void ProcessDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessItem item)
        {
            TargetText.Text = $"{item.Pid} {item.Package}";
            AppendLog($"已选择 {item.Package} (PID {item.Pid})。请确认游戏仍在前台，再开始采集。", "status");
        }
    }

    private async void StartClicked(object? sender, RoutedEventArgs e)
    {
        if (_runner is not null && !_runner.HasExited) return;
        var target = (TargetText.Text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (target.Length < 2 || !int.TryParse(target[0], out var pid) || pid <= 0)
        {
            AppendLog("请先双击进程列表中的目标，或填写“PID 包名”。", "error");
            return;
        }
        var package = target[1];
        if (string.IsNullOrWhiteSpace(TraceProcessorPath) || !File.Exists(TraceProcessorPath))
        {
            AppendLog("缺少 trace_processor_shell.exe，请把官方文件放在实验 EXE 同目录。", "error");
            return;
        }
        if (!File.Exists(Path.Combine(BaseDirectory, "tools", "android_frame_experiment.py")))
        {
            AppendLog("缺少设备侧实验脚本，请重新解压完整测试包。", "error");
            return;
        }

        _validWindows = 0;
        WindowValue.Text = "0";
        FpsValue.Text = JankValue.Text = FrameTimeValue.Text = "--";
        _runnerCts = new CancellationTokenSource();
        var work = Path.Combine(BaseDirectory, "data", "trace");
        Directory.CreateDirectory(work);
        var info = new ProcessStartInfo(PythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        AddArgument(info, Path.Combine(BaseDirectory, "tools", "android_frame_experiment.py"));
        AddArgument(info, "--adb"); AddArgument(info, AdbPath);
        AddArgument(info, "--serial"); AddArgument(info, (string?)DeviceCombo.SelectedItem ?? "");
        AddArgument(info, "--pid"); AddArgument(info, pid.ToString());
        AddArgument(info, "--package"); AddArgument(info, package);
        AddArgument(info, "--trace-processor"); AddArgument(info, TraceProcessorPath);
        AddArgument(info, "--work-directory"); AddArgument(info, work);
        AddArgument(info, "--window-seconds"); AddArgument(info, "5");
        try
        {
            _runner = Process.Start(info) ?? throw new InvalidOperationException("无法启动实验进程");
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            RefreshButton.IsEnabled = false;
            ConnectionState.Text = "设备侧采集中";
            AppendLog($"已启动新方案：目标 {package} / PID {pid}。", "status");
            _ = ReadRunnerAsync(_runner, _runnerCts.Token);
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败：{ex.Message}", "error");
            StopRunnerState();
        }
        await Task.CompletedTask;
    }

    private static void AddArgument(ProcessStartInfo info, string value) => info.ArgumentList.Add(value);

    private async Task ReadRunnerAsync(Process process, CancellationToken token)
    {
        var output = PumpLinesAsync(process.StandardOutput, token);
        var error = PumpLinesAsync(process.StandardError, token);
        await Task.WhenAll(output, error);
        try { await process.WaitForExitAsync(token); } catch { }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AppendLog($"设备侧实验进程已结束，退出码 {process.ExitCode}。", process.ExitCode == 0 ? "status" : "error");
            StopRunnerState();
        });
    }

    private async Task PumpLinesAsync(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            await Dispatcher.UIThread.InvokeAsync(() => HandleRunnerLine(line));
        }
    }

    private void HandleRunnerLine(string line)
    {
        const string prefix = "android_frame_experiment ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) { AppendLog(line, "error"); return; }
        try
        {
            using var doc = JsonDocument.Parse(line[prefix.Length..]);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var kindNode) ? kindNode.GetString() : "";
            var message = root.TryGetProperty("message", out var msgNode) ? msgNode.GetString() : "";
            switch (kind)
            {
                case "status":
                    ConnectionState.Text = "设备侧采集中";
                    AppendLog(message ?? "", "status");
                    break;
                case "sample":
                    _validWindows++;
                    WindowValue.Text = _validWindows.ToString();
                    FpsValue.Text = Number(root, "fps", "--");
                    JankValue.Text = Number(root, "jank", "--");
                    FrameTimeValue.Text = Number(root, "frame_time_p95_ms", Number(root, "frame_time_ms", "--")) + " ms";
                    AppendLog($"窗口 #{Number(root, "window", "?")} 有效：FPS {FpsValue.Text}，Jank {JankValue.Text}，FrameTime {FrameTimeValue.Text}", "sample");
                    break;
                case "unavailable":
                    var codeText = root.TryGetProperty("code", out var code) ? code.GetString() : "unknown";
                    AppendLog($"窗口 #{Number(root, "window", "?")} 未计入：{message ?? "数据不可用"}（{codeText}）", "warning");
                    break;
                case "fatal":
                    ConnectionState.Text = "采集失败";
                    AppendLog(message ?? "实验失败", "error");
                    break;
                case "stopped":
                    AppendLog(message ?? "已停止", "status");
                    break;
            }
        }
        catch (JsonException) { AppendLog(line, "error"); }
    }

    private static string Number(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.String => value.GetString() ?? fallback,
            _ => fallback,
        };
    }

    private void StopClicked(object? sender, RoutedEventArgs e) => StopRunner();

    private void StopRunner()
    {
        try
        {
            _runnerCts?.Cancel();
            if (_runner is { HasExited: false }) _runner.Kill(entireProcessTree: true);
        }
        catch { }
        StopRunnerState();
        AppendLog("已请求停止设备侧实验。", "status");
    }

    private void StopRunnerState()
    {
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        RefreshButton.IsEnabled = true;
        ConnectionState.Text = "未开始";
        _runner = null;
        _runnerCts?.Dispose();
        _runnerCts = null;
    }

    private void AppendLog(string message, string level)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var prefix = level switch { "error" => "[错误]", "warning" => "[提示]", "sample" => "[数据]", _ => "[状态]" };
        LogText.Text += $"{DateTime.Now:HH:mm:ss} {prefix} {message}\n";
    }

    private void WindowClosing(object? sender, WindowClosingEventArgs e) => StopRunner();

    private static async Task<CommandResult> RunCommandAsync(string file, IEnumerable<string> args, CancellationToken token)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {file}");
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return new CommandResult(process.ExitCode, output, error);
    }

    private readonly record struct CommandResult(int ExitCode, string Output, string Error)
    {
        public string ErrorOrOutput(string fallback) => string.IsNullOrWhiteSpace(Error) ? (string.IsNullOrWhiteSpace(Output) ? fallback : Output.Trim()) : Error.Trim();
    }
}
