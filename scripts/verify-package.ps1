$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$checksumFile = Join-Path $root "SHA256SUMS.txt"
if (-not (Test-Path $checksumFile)) { throw "SHA256SUMS.txt not found" }

$failed = @()
foreach ($line in Get-Content $checksumFile) {
  if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid checksum line: $line" }
  $expected = $Matches[1]
  $relative = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
  $path = Join-Path $root $relative
  if (-not (Test-Path $path)) { $failed += "$relative (missing)"; continue }
  $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { $failed += "$relative (checksum mismatch)" }
}
if ($failed.Count) { throw "Package verification failed: $($failed -join ', ')" }
Write-Host "All package checksums are valid."

