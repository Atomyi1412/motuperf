$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$backup = Join-Path $repo "dist\backups\MoTuPerf-v0.4.65-baseline"
$expected = @{
  "MoTuPerf-v0.4.65-source.zip" = "E87E99F0A8F1786CB795CFB9EDFFCF49EDAB07AFFD30BD48C880E5A012D2CDE6"
  "MoTuPerf-Setup-v0.4.65.exe" = "C1AFB9FF6C7CD732371B211B59D682F66DEFA16B50B158A7C9935E45869F3A75"
}

foreach ($name in $expected.Keys) {
  $path = Join-Path $backup $name
  if (!(Test-Path -LiteralPath $path)) { throw "Baseline file missing: $path" }
  $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  if ($actual -ne $expected[$name]) { throw "Baseline hash changed: $name`nExpected: $($expected[$name])`nActual:   $actual" }
}

Write-Host "MoTuPerf v0.4.65 baseline backup verified."
