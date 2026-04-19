param(
    [string]$ArchivePath = "",
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist-sites"

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $repoRoot "deploy-site.tar"
}

if ($Build -or -not (Test-Path $distRoot)) {
    & (Join-Path $PSScriptRoot "build-all.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "build-all.ps1 failed"
    }
}

if (Test-Path $ArchivePath) {
    Remove-Item -LiteralPath $ArchivePath -Force
}

Push-Location $distRoot
try {
    $entries = Get-ChildItem -Force | ForEach-Object { $_.Name }
    if (-not $entries -or $entries.Count -eq 0) {
        throw "Nothing to archive in $distRoot"
    }

    & tar -cf $ArchivePath @entries
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create archive: $ArchivePath"
    }
} finally {
    Pop-Location
}

Write-Host "Packed deploy archive at $ArchivePath"
