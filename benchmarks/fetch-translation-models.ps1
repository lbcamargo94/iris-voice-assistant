<#
.SYNOPSIS
    Downloads the translation models in benchmarks/models/translation.manifest.json, verifies SHA-256,
    extracts them and converts Opus-MT to CTranslate2.
.DESCRIPTION
    Explicit download (~930 MB, CC BY 4.0). Files already present with the right checksum are skipped.
    A file with a wrong checksum is deleted and the script fails. The conversion uses
    ct2-opus-mt-converter from the workers 'bench' environment (no PyTorch needed).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $PSScriptRoot 'models\translation.manifest.json') -Raw | ConvertFrom-Json
$root = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $manifest.localRoot))
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($file in $manifest.files) {
    $target = Join-Path $root $file.path
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    Write-Host "$($file.name) - license $($file.license)"
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $file.sha256)) {
        Write-Host "OK   $($file.path) (cached)"
    } else {
        Write-Host ("GET  {0} ({1:N0} MB)" -f $file.url, ($file.bytes / 1MB))
        Invoke-WebRequest -Uri $file.url -OutFile $target -UseBasicParsing
        $hash = (Get-FileHash $target -Algorithm SHA256).Hash
        if ($hash -ne $file.sha256) {
            Remove-Item $target -Force
            throw "Checksum mismatch for $($file.path): $hash"
        }
        Write-Host "OK   $($file.path)"
    }

    $destination = [System.IO.Path]::GetFullPath((Join-Path $root $file.extractTo))
    $zip = [System.IO.Compression.ZipFile]::OpenRead($target)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if ($file.exclude | Where-Object { $entry.FullName -like $_ }) { continue }
            $out = [System.IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $out.StartsWith($destination + [System.IO.Path]::DirectorySeparatorChar)) { throw "Unsafe zip path: $($entry.FullName)" }
            New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
        }
    } finally {
        $zip.Dispose()
    }
    Write-Host "     extracted to $($file.extractTo)"
}

foreach ($conversion in $manifest.conversions) {
    $from = Join-Path $root $conversion.from
    $to = Join-Path $root $conversion.to
    $modelBin = Join-Path $to 'model.bin'
    if ((Test-Path $modelBin) -and ((Get-FileHash $modelBin -Algorithm SHA256).Hash -eq $conversion.modelBinSha256)) {
        Write-Host "OK   $($conversion.to) (converted)"
        continue
    }
    Write-Host "CONV $($conversion.from) -> $($conversion.to) ($($conversion.quantization))"
    Push-Location (Join-Path $repoRoot 'workers')
    try {
        & uv run --group bench ct2-opus-mt-converter --model_dir $from --output_dir $to --quantization $conversion.quantization --force
        if ($LASTEXITCODE -ne 0) { throw "ct2-opus-mt-converter failed ($LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
    $hash = (Get-FileHash $modelBin -Algorithm SHA256).Hash
    if ($hash -ne $conversion.modelBinSha256) {
        Write-Warning "Converted model.bin SHA-256 differs from the pinned value ($hash); converter version may differ."
    }
}
