<#
.SYNOPSIS
    POC-001A step 2: runs the faster-whisper STT benchmark matrix on the AMI test meetings.
.DESCRIPTION
    Requires the AMI files listed in benchmarks/corpus/ami.manifest.json under data/corpus/ami and
    the bench dependency group (cd workers; uv sync --group bench).
    -AllowDownload lets faster-whisper download missing models (sizes/licenses: benchmarks/README.md).
    Results (metrics only) are written to benchmarks/results/stt.
#>
[CmdletBinding()]
param(
    [switch]$AllowDownload,
    [switch]$Rerun,
    [double]$MaxSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:UV_CACHE_DIR = Join-Path $repoRoot '.cache\uv'
$env:HF_HUB_DISABLE_TELEMETRY = '1'

$matrix = @(
    @{ Model = 'small.en';        Compute = 'float16';      Beam = 5 },
    @{ Model = 'medium.en';       Compute = 'float16';      Beam = 5 },
    @{ Model = 'distil-large-v3'; Compute = 'float16';      Beam = 5 },
    @{ Model = 'large-v3-turbo';  Compute = 'float16';      Beam = 5 },
    @{ Model = 'large-v3-turbo';  Compute = 'int8_float16'; Beam = 5 },
    @{ Model = 'large-v3-turbo';  Compute = 'float16';      Beam = 1 }
)

Push-Location (Join-Path $repoRoot 'workers')
try {
    $resultsDir = Join-Path $repoRoot 'benchmarks\results\stt'
    foreach ($run in $matrix) {
        $pattern = "*_$($run.Model)_$($run.Compute)_b$($run.Beam).json"
        if (-not $Rerun -and (Test-Path $resultsDir) -and (Get-ChildItem $resultsDir -Filter $pattern)) {
            Write-Host "=== $($run.Model) $($run.Compute) beam=$($run.Beam): result exists, skipping (use -Rerun) ==="
            continue
        }
        Write-Host "=== $($run.Model) $($run.Compute) beam=$($run.Beam) ===" -ForegroundColor Cyan
        $cliArgs = @('run', '--group', 'bench', 'python', '-m', 'iris_workers.bench.stt',
            '--model', $run.Model, '--compute-type', $run.Compute, '--beam-size', $run.Beam,
            '--max-seconds', $MaxSeconds)
        if ($AllowDownload) { $cliArgs += '--allow-download' }
        # Windows PowerShell 5.1 turns native stderr (e.g. Hugging Face warnings) into errors under
        # 'Stop' when output is redirected; rely on the exit code instead.
        $ErrorActionPreference = 'Continue'
        & uv @cliArgs 2>&1 | ForEach-Object { "$_" }
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Benchmark failed for $($run.Model) $($run.Compute)" }
    }
} finally {
    Pop-Location
}
