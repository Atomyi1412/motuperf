param(
  [Parameter(Mandatory = $true)]
  [string]$InstallDirectory,
  [switch]$DetectOnly
)

$ErrorActionPreference = "Stop"
$targetNames = @(
  "CSharpIosPerfMonitor.exe",
  "python.exe",
  "adb.exe"
)

try {
  $root = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
} catch {
  exit 3
}

function Get-MoTuPerfProcesses {
  $filter = "Name='CSharpIosPerfMonitor.exe' OR Name='python.exe' OR Name='adb.exe'"
  @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction Stop | Where-Object {
    $executablePath = $_.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
      return $false
    }

    try {
      $fullPath = [System.IO.Path]::GetFullPath($executablePath)
      return $targetNames -contains [System.IO.Path]::GetFileName($fullPath) -and
        $fullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
      return $false
    }
  })
}

try {
  $processes = @(Get-MoTuPerfProcesses)
  if ($DetectOnly) {
    exit $(if ($processes.Count -gt 0) { 10 } else { 0 })
  }

  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  while ($processes.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
    foreach ($process in $processes) {
      Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 250
    $processes = @(Get-MoTuPerfProcesses)
  }

  exit $(if ($processes.Count -eq 0) { 0 } else { 11 })
} catch {
  exit 3
}
