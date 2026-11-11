<#
.SYNOPSIS
    Downloads the Piper pt-BR voices in benchmarks/models/tts.manifest.json (SHA-256 verified) and
    creates the isolated Piper runtime environment in .tools/piper.
.DESCRIPTION
    Explicit download (~155 MB): two voices (CC0 chain, ~60 MB each) and piper-tts 1.8.0
    (GPL-3.0-or-later, ~33 MB) with its dependencies from a hash-pinned requirements file.
    Piper runs as a separate process from its own environment; the MIT workers never import it.
    Files already present with the right checksum are skipped. A wrong checksum deletes the file
    and fails the script. Uses the repo-local uv cache (.cache/uv).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $PSScriptRoot 'models\tts.manifest.json') -Raw | ConvertFrom-Json
$root = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $manifest.localRoot))

foreach ($voice in $manifest.voices) {
    Write-Host "$($voice.name) - license $($voice.license)"
    foreach ($file in $voice.files) {
        $target = [System.IO.Path]::GetFullPath((Join-Path $root (Split-Path -Leaf $file.path)))
        if ((Split-Path -Leaf $file.path) -eq 'MODEL_CARD') {
            $target = Join-Path $root "$($voice.name).MODEL_CARD"
        }
        if (-not $target.StartsWith($root)) { throw "Unsafe path: $($file.path)" }
        New-Item -ItemType Directory -Force $root | Out-Null
        if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $file.sha256)) {
            Write-Host "OK   $(Split-Path -Leaf $target) (cached)"
            continue
        }
        $url = "$($manifest.source)/resolve/$($manifest.revision)/$($file.path)"
        Write-Host ("GET  {0} ({1:N1} MB)" -f $url, ($file.bytes / 1MB))
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
        $hash = (Get-FileHash $target -Algorithm SHA256).Hash
        if ($hash -ne $file.sha256) {
            Remove-Item $target -Force
            throw "Checksum mismatch for $($file.path): $hash"
        }
        Write-Host "OK   $(Split-Path -Leaf $target)"
    }
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
Write-Host "PIP  $($manifest.runtime.name) $($manifest.runtime.version) ($($manifest.runtime.license)), hash-pinned"
& uv pip sync --python $python --require-hashes $requirements
if ($LASTEXITCODE -ne 0) { throw "uv pip sync failed ($LASTEXITCODE)" }
& $python -c "import importlib.metadata as m; print('piper-tts', m.version('piper-tts'))"
