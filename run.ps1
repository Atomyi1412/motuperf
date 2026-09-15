$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$port = if ($env:IOS_PERF_PORT) { $env:IOS_PERF_PORT } else { "8770" }
python -m backend.server --port $port
