param(
  [string]$ArtifactRoot = "D:\Codex\artifacts\MoTuPerf",
  [string]$RuntimeSource = "D:\Codex\artifacts\MoTuPerf\v0.57.0-AndroidTraceLab\app\runtime",
  [string]$TraceProcessor = "D:\Codex\artifacts\MoTuPerf\research\perfetto\trace_processor_shell.exe"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$repoRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "src\MoTuPerf.AndroidFrameExperiment\MoTuPerf.AndroidFrameExperiment.csproj"
$artifactRootFull = [IO.Path]::GetFullPath($ArtifactRoot)
$runtimeSourceFull = [IO.Path]::GetFullPath($RuntimeSource)
$traceProcessorFull = [IO.Path]::GetFullPath($TraceProcessor)

foreach ($required in @($projectFile, $runtimeSourceFull, $traceProcessorFull)) {
  if (!(Test-Path -LiteralPath $required)) { throw "Required path is missing: $required" }
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile
$version = @($projectXml.Project.PropertyGroup | ForEach-Object { if ($_.Version) { [string]$_.Version } } | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Experiment project version must be three numeric components: $version"
}

$artifactName = "v$version-AndroidFrameExperiment"
$artifactDir = [IO.Path]::GetFullPath((Join-Path $artifactRootFull $artifactName))
if (!$artifactDir.StartsWith($artifactRootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to clean a path outside the artifact root: $artifactDir"
}
if (Test-Path -LiteralPath $artifactDir) { Remove-Item -LiteralPath $artifactDir -Recurse -Force }
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$appDir = Join-Path $artifactDir "app"
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
  -p:UseAppHost=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
  -o $appDir --no-restore
if ($LASTEXITCODE -ne 0) { throw "Windows experiment publish failed." }
Get-ChildItem -LiteralPath $appDir -Recurse -File -Filter "*.pdb" | Remove-Item -Force

$toolsTarget = Join-Path $appDir "tools"
New-Item -ItemType Directory -Path $toolsTarget -Force | Out-Null
Get-ChildItem (Join-Path $repoRoot "csharp_perf_monitor\tools") -File |
  Where-Object { $_.Extension -in @('.py', '.txt', '.md') } |
  Copy-Item -Destination $toolsTarget -Force
Copy-Item -LiteralPath $traceProcessorFull -Destination (Join-Path $toolsTarget "trace_processor_shell.exe") -Force

$runtimeTarget = Join-Path $appDir "runtime"
New-Item -ItemType Directory -Path $runtimeTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $runtimeSourceFull "python") -Destination $runtimeTarget -Recurse -Force
Copy-Item -LiteralPath (Join-Path $runtimeSourceFull "android") -Destination $runtimeTarget -Recurse -Force

$info = @(
  "MoTuPerf Android device-side frame experiment",
  "Version: v$version",
  "BuiltAt: $([DateTime]::UtcNow.ToString('o'))",
  "Backend: device_perfetto",
  "Production collector changed by default: no",
  "TraceProcessorSHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $traceProcessorFull).Hash.ToLowerInvariant())"
)
$info | Set-Content -LiteralPath (Join-Path $artifactDir "EXPERIMENT-INFO.txt") -Encoding UTF8

$zipPath = Join-Path $artifactDir "MoTuPerf-AndroidFrameExperiment-v$version.zip"
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
$zipPath | Set-Content -LiteralPath (Join-Path $artifactDir "PACKAGE-PATH.txt") -Encoding UTF8
$zipHash | Set-Content -LiteralPath (Join-Path $artifactDir "PACKAGE-SHA256.txt") -Encoding ASCII

Write-Host "Experiment EXE: $(Join-Path $appDir 'MoTuPerf.AndroidFrameExperiment.exe')"
Write-Host "Experiment ZIP: $zipPath"
Write-Host "SHA256: $zipHash"
