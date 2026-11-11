<#
.SYNOPSIS
    Downloads Kokoro-82M v1.0 (ONNX) and its voices (benchmarks/models/kokoro.manifest.json, SHA-256
    verified) and creates the isolated runtime environment in .tools/kokoro.
.DESCRIPTION
    Explicit download (~355 MB model + voices, Apache-2.0 weights) plus kokoro-onnx 0.6.1 and its
    dependencies from a hash-pinned requirements file. phonemizer/espeak-ng are GPL, so Kokoro runs
    as a separate process from its own environment, like Piper.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $PSScriptRoot 'models\kokoro.manifest.json') -Raw | ConvertFrom-Json
$root = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $manifest.localRoot))
New-Item -ItemType Directory -Force $root | Out-Null
Write-Host "Kokoro-82M - weights license $($manifest.weightsLicense)"

foreach ($file in $manifest.files) {
    $target = Join-Path $root $file.name
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $file.sha256)) {
        Write-Host "OK   $($file.name) (cached)"
        continue
    }
    Write-Host ("GET  {0} ({1:N0} MB)" -f $file.url, ($file.bytes / 1MB))
    Invoke-WebRequest -Uri $file.url -OutFile $target -UseBasicParsing
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($hash -ne $file.sha256) {
        Remove-Item $target -Force
        throw "Checksum mismatch for $($file.name): $hash"
    }
    Write-Host "OK   $($file.name)"
}

$venv = Join-Path $repoRoot $manifest.runtime.venv
$python = Join-Path $venv 'Scripts\python.exe'
$requirements = Join-Path $repoRoot $manifest.runtime.requirements
if (-not $env:UV_CACHE_DIR) { $env:UV_CACHE_DIR = Join-Path $repoRoot '.cache\uv' }
# uv reports progress on stderr; Windows PowerShell 5.1 would turn that into a terminating error.
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $python)) {
    Write-Host "VENV $($manifest.runtime.venv) (Python 3.12)"
    & uv venv --python 3.12 $venv
    if ($LASTEXITCODE -ne 0) { throw "uv venv failed ($LASTEXITCODE)" }
}
Write-Host "PIP  $($manifest.runtime.name) $($manifest.runtime.version), hash-pinned"
& uv pip sync --python $python --require-hashes $requirements
if ($LASTEXITCODE -ne 0) { throw "uv pip sync failed ($LASTEXITCODE)" }
& $python -c "import importlib.metadata as m; print('kokoro-onnx', m.version('kokoro-onnx'))"
