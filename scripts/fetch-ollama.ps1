<#
.SYNOPSIS
    Downloads the portable Ollama build for Windows x64, verifies SHA-256 and the Authenticode
    signature, and extracts it to .tools/ (git-ignored).
.DESCRIPTION
    Explicit download (~1.5 GB, MIT). The portable build is used instead of OllamaSetup.exe because
    the installer writes to C:, runs a tray app and updates itself automatically (AGENTS.md section 2:
    downloads must be explicit). See docs/AI-RUNTIME.md.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = 'v0.40.16'
$url = "https://github.com/ollama/ollama/releases/download/$version/ollama-windows-amd64.zip"
$sha256 = '50fcf5ad54277f2fa5d173ebfc93bcb017adbc0c9ef2314daabc96ca9e6aa64e'  # GitHub release asset digest

$repoRoot = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $repoRoot '.tools'
$zip = Join-Path $tools "downloads\ollama-windows-amd64-$version.zip"
$destination = [System.IO.Path]::GetFullPath((Join-Path $tools "ollama-$version"))
New-Item -ItemType Directory -Force (Split-Path -Parent $zip) | Out-Null

if ((Test-Path $zip) -and ((Get-FileHash $zip -Algorithm SHA256).Hash -eq $sha256)) {
    Write-Host "OK   $zip (cached)"
} else {
    Write-Host "GET  $url (~1.5 GB, MIT)"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $sha256) {
        Remove-Item $zip -Force
        throw "Checksum mismatch: $hash"
    }
    Write-Host "OK   SHA-256 verified"
}

if (-not (Test-Path (Join-Path $destination 'ollama.exe'))) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $out = [System.IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $out.StartsWith($destination + [System.IO.Path]::DirectorySeparatorChar)) { throw "Unsafe zip path: $($entry.FullName)" }
            New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
        }
    } finally {
        $archive.Dispose()
    }
}

$signature = Get-AuthenticodeSignature (Join-Path $destination 'ollama.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Ollama Inc.*') {
    throw "Unexpected signature on ollama.exe: $($signature.Status) $($signature.SignerCertificate.Subject)"
}
Write-Host "OK   ollama.exe signed by Ollama Inc.; extracted to $destination"
