<#
.SYNOPSIS
    Runs the portable Ollama server for local benchmarks: loopback only, cloud features off,
    models on the repo drive.
.DESCRIPTION
    Foreground process; stop it with Ctrl+C. Fails if OLLAMA_HOST would expose the server
    beyond loopback (docs/SECURITY.md). Use -Pull to download models explicitly before serving.
.EXAMPLE
    powershell -File scripts\start-ollama.ps1 -Pull qwen3:4b-instruct,phi4-mini:3.8b
#>
[CmdletBinding()]
param(
    [string[]]$Pull = @(),
    [string]$Version = 'v0.40.16'
)

$ErrorActionPreference = 'Stop'
# "-File start-ollama.ps1 -Pull a,b" passes one string "a,b"; accept both forms.
$Pull = @($Pull | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot ".tools\ollama-$Version\ollama.exe"
if (-not (Test-Path $exe)) { throw "Ollama not found; run scripts\fetch-ollama.ps1 first." }

$env:OLLAMA_HOST = '127.0.0.1:11434'
$env:OLLAMA_NO_CLOUD = '1'
$env:OLLAMA_MODELS = Join-Path $repoRoot 'models\ollama'
New-Item -ItemType Directory -Force $env:OLLAMA_MODELS | Out-Null

foreach ($scope in 'User', 'Machine') {
    $configured = [Environment]::GetEnvironmentVariable('OLLAMA_HOST', $scope)
    if ($configured -and $configured -notmatch '^(127\.0\.0\.1|localhost)(:\d+)?$') {
        Write-Warning "OLLAMA_HOST is set to '$configured' ($scope scope): another Ollama started outside this script would be reachable from the network."
    }
}

Write-Host "Ollama $Version | host $env:OLLAMA_HOST | cloud disabled | models $env:OLLAMA_MODELS"
if ($Pull.Count -gt 0) {
    $server = Start-Process -FilePath $exe -ArgumentList 'serve' -NoNewWindow -PassThru
    try {
        Start-Sleep -Seconds 3
        foreach ($model in $Pull) {
            Write-Host "PULL $model"
            & $exe pull $model
            if ($LASTEXITCODE -ne 0) { throw "ollama pull $model failed ($LASTEXITCODE)" }
        }
        # Tags can be re-pointed upstream: the digest must match what was measured (llm.manifest.json).
        & (Join-Path $PSScriptRoot 'verify-ollama-models.ps1') -Models $Pull
    } finally {
        Stop-Process -Id $server.Id
    }
}
& $exe serve
