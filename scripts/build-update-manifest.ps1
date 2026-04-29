param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$DisplayVersion = "",

    [string]$BaseUrl = "https://cloudflarestorage.boyqiu001.top/",

    [string]$Channel = "stable",

    [string]$Platform = "windows-x64",

    [string]$OutputRoot = "bucket-update-starter",

    [string]$ReleaseNotes = "",

    [string]$MinimumSupportedVersion = "",

    [string]$ForceUpdateBelow = "",

    [ValidateRange(0, 100)]
    [int]$RolloutPercent = 100,

    [string]$RolloutSeed = "",

    [string[]]$BlockedVersions = @(),

    [switch]$CopyPackageToOutput
)

$ErrorActionPreference = "Stop"

throw "scripts/build-update-manifest.ps1 is deprecated for app binary updates. Use scripts/build-velopack-release.ps1 for app releases, and keep static manifests only for resource hot updates."

function Test-VersionText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $parsed = $null
    return [System.Version]::TryParse($Value, [ref]$parsed)
}

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$PathValue
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $PathValue))
}

function Normalize-BaseUrl {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $trimmed = $Value.Trim()
    if (-not $trimmed.EndsWith("/")) {
        $trimmed += "/"
    }

    return $trimmed
}

function Join-Url {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Base,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $normalizedBase = Normalize-BaseUrl $Base
    $normalizedRelative = $RelativePath.TrimStart('/').Replace('\', '/')
    return ([System.Uri]::new([System.Uri]::new($normalizedBase), $normalizedRelative)).AbsoluteUri
}

function Write-Utf8JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        $Data
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $json = $Data | ConvertTo-Json -Depth 10
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $utf8NoBom)
}

if (-not (Test-VersionText $Version)) {
    throw "Invalid version format: '$Version'. Expected numeric values like 0.8.0.101 or 0.7.0.1000."
}

if (-not [string]::IsNullOrWhiteSpace($MinimumSupportedVersion) -and -not (Test-VersionText $MinimumSupportedVersion)) {
    throw "Invalid minimumSupportedVersion: '$MinimumSupportedVersion'."
}

if (-not [string]::IsNullOrWhiteSpace($ForceUpdateBelow) -and -not (Test-VersionText $ForceUpdateBelow)) {
    throw "Invalid forceUpdateBelow: '$ForceUpdateBelow'."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageFullPath = Resolve-AbsolutePath -BasePath (Get-Location).Path -PathValue $PackagePath

if (-not (Test-Path -LiteralPath $packageFullPath -PathType Leaf)) {
    throw "Package file not found: $packageFullPath"
}

$outputRootFullPath = Resolve-AbsolutePath -BasePath $repoRoot -PathValue $OutputRoot
$manifestDir = Join-Path $outputRootFullPath "manifests\update"
$releaseDir = Join-Path $outputRootFullPath "releases"
$manifestFileName = "$Platform-$Channel.json"
$manifestPath = Join-Path $manifestDir $manifestFileName
$indexPath = Join-Path $manifestDir "index.json"

$packageItem = Get-Item -LiteralPath $packageFullPath
$packageHash = (Get-FileHash -LiteralPath $packageFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageFileName = $packageItem.Name
$effectiveDisplayVersion = if ([string]::IsNullOrWhiteSpace($DisplayVersion)) { $Version } else { $DisplayVersion.Trim() }
$effectiveReleaseNotes = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) { "VRCLS $effectiveDisplayVersion" } else { $ReleaseNotes }
$effectiveMinimumSupportedVersion = if ([string]::IsNullOrWhiteSpace($MinimumSupportedVersion)) { $Version } else { $MinimumSupportedVersion }
$effectiveRolloutSeed = if ([string]::IsNullOrWhiteSpace($RolloutSeed)) { "vrcls-$Version" } else { $RolloutSeed }

if ($CopyPackageToOutput) {
    [System.IO.Directory]::CreateDirectory($releaseDir) | Out-Null
    $copiedPackagePath = Join-Path $releaseDir $packageFileName
    Copy-Item -LiteralPath $packageFullPath -Destination $copiedPackagePath -Force
    $packageFullPath = $copiedPackagePath
    $packageItem = Get-Item -LiteralPath $packageFullPath
}

$releaseUrl = if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    "../../releases/$packageFileName"
} else {
    Join-Url -Base $BaseUrl -RelativePath "releases/$packageFileName"
}

$index = [ordered]@{
    schema = 1
    defaultChannel = $Channel
    manifests = [ordered]@{
        $Platform = [ordered]@{
            $Channel = $manifestFileName
        }
    }
}

$manifest = [ordered]@{
    latestVersion = $Version
    displayVersion = $effectiveDisplayVersion
    minimumSupportedVersion = $effectiveMinimumSupportedVersion
    forceUpdateBelow = $ForceUpdateBelow
    rolloutPercent = $RolloutPercent
    rolloutSeed = $effectiveRolloutSeed
    releaseNotes = $effectiveReleaseNotes
    blockedVersions = @($BlockedVersions)
    release = [ordered]@{
        url = $releaseUrl
        fileSize = $packageItem.Length
        checksum = "sha256:$packageHash"
    }
}

Write-Host "Building static update manifest..." -ForegroundColor Cyan
Write-Host "  Package : $packageFullPath"
Write-Host "  Version : $Version"
Write-Host "  Display : $effectiveDisplayVersion"
Write-Host "  Platform: $Platform"
Write-Host "  Channel : $Channel"
Write-Host "  Output  : $outputRootFullPath"

Write-Utf8JsonFile -Path $indexPath -Data $index
Write-Utf8JsonFile -Path $manifestPath -Data $manifest

Write-Host "Generated files:" -ForegroundColor Green
Write-Host "  $indexPath"
Write-Host "  $manifestPath"
Write-Host "Release package info:" -ForegroundColor Green
Write-Host "  FileName: $packageFileName"
Write-Host "  FileSize: $($packageItem.Length)"
Write-Host "  SHA256  : $packageHash"
Write-Host "  URL     : $releaseUrl"
