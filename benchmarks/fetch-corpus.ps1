<#
.SYNOPSIS
    Downloads the AMI test files listed in benchmarks/corpus/ami.manifest.json and verifies SHA-256.
.DESCRIPTION
    Explicit download (~83 MB, CC BY 4.0). Files already present with the right checksum are skipped.
    A file with a wrong checksum is deleted and the script fails.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $PSScriptRoot 'corpus\ami.manifest.json') -Raw | ConvertFrom-Json
$root = Join-Path $repoRoot $manifest.localRoot
Write-Host "$($manifest.corpus) - license $($manifest.license) ($($manifest.licenseUrl))"

foreach ($file in $manifest.files) {
    $target = Join-Path $root $file.path
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $file.sha256)) {
        Write-Host "OK   $($file.path) (cached)"
        continue
    }
    Write-Host ("GET  {0} ({1:N1} MB)" -f $file.url, ($file.bytes / 1MB))
    Invoke-WebRequest -Uri $file.url -OutFile $target -UseBasicParsing
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($hash -ne $file.sha256) {
        Remove-Item $target -Force
        throw "Checksum mismatch for $($file.path): $hash"
    }
    Write-Host "OK   $($file.path)"

    if ($file.extract) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $destination = Split-Path -Parent $target
        $zip = [System.IO.Compression.ZipFile]::OpenRead($target)
        try {
            foreach ($entry in $zip.Entries) {
                $wanted = $file.extract | Where-Object { $entry.FullName -like $_ }
                if (-not $wanted -or $entry.FullName.EndsWith('/')) { continue }
                $out = [System.IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
                if (-not $out.StartsWith([System.IO.Path]::GetFullPath($destination))) { throw "Unsafe zip path: $($entry.FullName)" }
                New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
            }
        } finally {
            $zip.Dispose()
        }
        Write-Host "     extracted: $($file.extract -join ', ')"
    }
}
