<#
.SYNOPSIS
    Verifies the development prerequisites for Iris Voice Assistant (read-only; installs nothing).
.DESCRIPTION
    Checks Windows build (process loopback, ADR-007), .NET SDK (global.json), uv + Python 3.12 (ADR-012),
    NVIDIA driver, Ollama (optional until POC-001A) and free disk space on the repository drive.
    Exit code 0 when all required checks pass, 1 otherwise.
#>
[CmdletBinding()]
param(
    [int]$MinFreeGb = 20
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = 0

function Write-Check([string]$Name, [bool]$Ok, [string]$Detail, [switch]$Optional) {
    if ($Ok) { $status = 'OK  '; $color = 'Green' }
    elseif ($Optional) { $status = 'WARN'; $color = 'Yellow' }
    else { $status = 'FAIL'; $color = 'Red'; $script:failures++ }
    Write-Host ("[{0}] {1,-22} {2}" -f $status, $Name, $Detail) -ForegroundColor $color
}

function Find-Command([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { return $cmd.Source }
    return $null
}

# Windows build: process loopback requires 20348+.
$build = [Environment]::OSVersion.Version.Build
Write-Check 'Windows build' ($build -ge 20348) "build $build (process loopback requires 20348+)"

# .NET SDK pinned by global.json.
$globalJson = Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
$wantedSdk = $globalJson.sdk.version
if (Find-Command 'dotnet') {
    $sdks = & dotnet --list-sdks
    $wantedFeatureBand = $wantedSdk.Substring(0, $wantedSdk.LastIndexOf('.') + 2)
    $hasSdk = [bool]($sdks | Where-Object { $_ -like "$wantedFeatureBand*" })
    Write-Check '.NET SDK' $hasSdk "wanted $wantedSdk (latestFeature); found: $(($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', ')"
} else {
    Write-Check '.NET SDK' $false 'dotnet not found'
}

# uv and the workers' Python.
$uv = Find-Command 'uv'
Write-Check 'uv' ($null -ne $uv) $(if ($uv) { (& uv --version) } else { 'not found (winget install astral-sh.uv)' })
$venvPython = Join-Path $repoRoot 'workers\.venv\Scripts\python.exe'
if (Test-Path $venvPython) {
    $pyVersion = (& $venvPython --version) -replace 'Python ', ''
    Write-Check 'Workers Python' ($pyVersion -like '3.12.*') "workers/.venv uses $pyVersion"
} else {
    Write-Check 'Workers Python' $false 'workers/.venv missing (run: cd workers; uv sync)'
}

# NVIDIA GPU (optional: CPU fallback is possible, but benchmarks target the GPU).
if (Find-Command 'nvidia-smi') {
    $gpu = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader
    Write-Check 'NVIDIA GPU' $true ($gpu -join '; ')
} else {
    Write-Check 'NVIDIA GPU' $false 'nvidia-smi not found' -Optional
}

# Ollama (optional until POC-001A step 4). Warn if configured to listen beyond loopback.
if (Find-Command 'ollama') {
    $hostSetting = [Environment]::GetEnvironmentVariable('OLLAMA_HOST')
    $exposed = $hostSetting -and ($hostSetting -notmatch '^(https?://)?(127\.0\.0\.1|localhost|\[::1\])(:\d+)?/?$')
    Write-Check 'Ollama' (-not $exposed) $(if ($exposed) { "OLLAMA_HOST=$hostSetting is not loopback" } else { 'installed, loopback only' })
} else {
    Write-Check 'Ollama' $false 'not installed (needed from POC-001A step 4)' -Optional
}

# Free space on the repository drive (models, venvs and uv cache live here).
$drive = (Get-Item $repoRoot).PSDrive
$freeGb = [math]::Round($drive.Free / 1GB, 1)
Write-Check "Free space ($($drive.Name):)" ($freeGb -ge $MinFreeGb) "$freeGb GB free (minimum $MinFreeGb GB)"

if ($failures -gt 0) {
    Write-Host "`n$failures required check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "`nAll required checks passed." -ForegroundColor Green
exit 0
