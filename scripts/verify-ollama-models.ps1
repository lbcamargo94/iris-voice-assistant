<#
.SYNOPSIS
    Checks installed Ollama models against benchmarks/models/llm.manifest.json.
.DESCRIPTION
    Ollama tags can be re-pointed upstream, so the digest must match what was measured. Queries the
    local server on loopback only, bypassing any system proxy. Fails on a digest mismatch or a
    missing model; warns for models that are not in the manifest. Called by start-ollama.ps1 -Pull.
.EXAMPLE
    powershell -File scripts\verify-ollama-models.ps1 -Models qwen3:4b-instruct
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Models,
    [string]$ManifestPath = '',
    [string]$Url = 'http://127.0.0.1:11434'
)

$ErrorActionPreference = 'Stop'
# "-File script.ps1 -Models a,b" passes one string "a,b"; accept both forms.
$Models = @($Models | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (-not $ManifestPath) {  # $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1
    $ManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'benchmarks\models\llm.manifest.json'
}
if ($Url -notmatch '^http://(127\.0\.0\.1|localhost)(:\d+)?$') { throw "Refusing non-loopback Ollama URL: $Url" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$http = New-Object System.Net.Http.HttpClient($handler)
try {
    $installed = ($http.GetStringAsync("$Url/api/tags").GetAwaiter().GetResult() | ConvertFrom-Json).models
} finally {
    $http.Dispose()
}

$failures = 0
foreach ($model in $Models) {
    $pinned = $manifest.models | Where-Object { $_.name -eq $model }
    $digest = ($installed | Where-Object { $_.name -eq $model }).digest
    if (-not $digest) {
        Write-Warning "$model is not installed."
        $failures++
    } elseif (-not $pinned) {
        Write-Warning "$model is not in llm.manifest.json (unpinned); benchmarks report it as unpinned."
    } elseif ($digest -ne $pinned.digest) {
        Write-Warning "Digest mismatch for ${model}: expected $($pinned.digest), got $digest. Remove it with 'ollama rm $model'."
        $failures++
    } else {
        Write-Host "OK   $model digest $($digest.Substring(0, 12)) matches llm.manifest.json"
    }
}
if ($failures -gt 0) { throw "$failures model(s) failed verification" }
