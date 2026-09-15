param([Parameter(Mandatory=$true)][string]$InstallDirectory)
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$names = @("MoTuPerf.CrossPlatform.exe", "python.exe", "adb.exe")
try {
  $processes = @(Get-CimInstance Win32_Process -Filter "Name='MoTuPerf.CrossPlatform.exe' OR Name='python.exe' OR Name='adb.exe'" | Where-Object {
    if ([string]::IsNullOrWhiteSpace($_.ExecutablePath)) { return $false }
    $full = [IO.Path]::GetFullPath($_.ExecutablePath)
    return $names -contains [IO.Path]::GetFileName($full) -and $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
  })
  foreach ($process in $processes) { Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue }
  exit 0
} catch { exit 3 }
