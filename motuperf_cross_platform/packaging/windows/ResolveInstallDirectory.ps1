param([int]$InstallerProcessId, [string]$OutputPath)
$ErrorActionPreference = 'Stop'

function Get-MoTuPerfDirectory([string]$ExecutablePath) {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { return $null }
    try {
        $full = [IO.Path]::GetFullPath($ExecutablePath)
        if ([IO.Path]::GetFileName($full) -ine 'MoTuPerf.CrossPlatform.exe' -or !(Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
        $directory = [IO.Path]::GetDirectoryName($full)
        $package = Get-Content -LiteralPath (Join-Path $directory 'motuperf-package.json') -Raw | ConvertFrom-Json
        if ($package.schema -ne 1 -or $package.target -ne 'win-x64') { return $null }
        return $directory
    } catch { return $null }
}

function Resolve-MoTuPerfDirectory([object[]]$Processes, [int]$OriginProcessId) {
    $byId = @{}
    foreach ($entry in $Processes) { $byId[[int]$entry.ProcessId] = $entry }
    $current = $OriginProcessId
    $seen = @{}
    # Prefer the invoking client when several installed copies are running.
    for ($depth = 0; $depth -lt 16 -and $byId.ContainsKey($current) -and !$seen.ContainsKey($current); $depth++) {
        $seen[$current] = $true
        $entry = $byId[$current]
        $directory = Get-MoTuPerfDirectory $entry.ExecutablePath
        if ($directory) { return $directory }
        $parent = [int]$entry.ParentProcessId
        if ($byId.ContainsKey($parent) -and $entry.CreationDate -and $byId[$parent].CreationDate -and
            $byId[$parent].CreationDate -gt $entry.CreationDate) { break }
        $current = $parent
    }
    $directories = @($Processes | ForEach-Object { Get-MoTuPerfDirectory $_.ExecutablePath } | Sort-Object -Unique)
    if ($directories.Count -eq 1) { return $directories[0] }
    return $null
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $processes = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
        $directory = Resolve-MoTuPerfDirectory $processes $InstallerProcessId
        if (!$directory) { exit 2 }
        [IO.File]::WriteAllText($OutputPath, "[upgrade]`r`ndirectory=$directory`r`n", [Text.Encoding]::Unicode)
        exit 0
    } catch { exit 3 }
}
