param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$Output = "publish",

    [Parameter(Mandatory = $true)]
    [string]$InternalVersion,

    [string]$DisplayVersion = "",

    [string]$Channel = "stable",

    [string]$AppCastBaseUrl = "",

    [string]$InnoSetupCompiler = "",

    [string]$NetSparklePublicKey = "",

    [string]$NetSparklePrivateKey = "",

    [switch]$IncludePreModels
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRelativePath {
    param(
        [string]$RepoRoot,
        [string]$PathValue
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        throw "Path value cannot be empty."
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $PathValue))
}

function Ensure-CleanDirectory {
    param(
        [string]$DirectoryPath
    )

    if (Test-Path -LiteralPath $DirectoryPath) {
        Remove-Item -LiteralPath $DirectoryPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $DirectoryPath | Out-Null
}

function Assert-RequiredFiles {
    param(
        [string[]]$Paths,
        [string]$Context
    )

    foreach ($requiredPath in $Paths) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "$Context is missing required file: $requiredPath"
        }
    }
}

function Resolve-IsccPath {
    param(
        [string]$ExplicitPath = ""
    )

    $fromPath = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add($ExplicitPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_COMPILER)) {
        $candidates.Add($env:INNO_SETUP_COMPILER)
    }
    if ($fromPath -and -not [string]::IsNullOrWhiteSpace($fromPath.Source)) {
        $candidates.Add($fromPath.Source)
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"))
        $candidates.Add((Join-Path $env:ProgramFiles "Inno Setup 5\ISCC.exe"))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"))
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} "Inno Setup 5\ISCC.exe"))
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Inno Setup compiler not found. Install Inno Setup 6 or set INNO_SETUP_COMPILER to ISCC.exe."
}

function Resolve-AppCastGeneratorPath {
    $fromPath = Get-Command "netsparkle-generate-appcast" -ErrorAction SilentlyContinue
    $candidates = New-Object System.Collections.Generic.List[string]

    if ($fromPath -and -not [string]::IsNullOrWhiteSpace($fromPath.Source)) {
        $candidates.Add($fromPath.Source)
    }

    if ($env:USERPROFILE) {
        $candidates.Add((Join-Path $env:USERPROFILE ".dotnet\tools\netsparkle-generate-appcast.exe"))
        $candidates.Add((Join-Path $env:USERPROFILE ".dotnet\tools\netsparkle-generate-appcast"))
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    Write-Host "NetSparkle appcast generator not found. Attempting automatic install..." -ForegroundColor Yellow
    & dotnet tool install -g NetSparkleUpdater.Tools.AppCastGenerator --version 2.8.6
    if ($LASTEXITCODE -ne 0) {
        throw "NetSparkle appcast generator not found, and automatic install failed. Install it manually with: dotnet tool install -g NetSparkleUpdater.Tools.AppCastGenerator --version 2.8.6"
    }

    $postInstallCandidates = @(
        (Join-Path $env:USERPROFILE ".dotnet\tools\netsparkle-generate-appcast.exe"),
        (Join-Path $env:USERPROFILE ".dotnet\tools\netsparkle-generate-appcast")
    )
    foreach ($candidate in $postInstallCandidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "NetSparkle appcast generator was installed, but the executable could not be located."
}

function Resolve-NetSparkleKey {
    param(
        [string]$ExplicitValue,
        [string]$PrimaryName,
        [string]$FallbackName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitValue)) {
        return $ExplicitValue.Trim()
    }

    $primary = [Environment]::GetEnvironmentVariable($PrimaryName)
    if (-not [string]::IsNullOrWhiteSpace($primary)) {
        return $primary.Trim()
    }

    $fallback = [Environment]::GetEnvironmentVariable($FallbackName)
    if (-not [string]::IsNullOrWhiteSpace($fallback)) {
        return $fallback.Trim()
    }

    return ""
}

function Escape-InnoValue {
    param(
        [string]$Value
    )

    if ($null -eq $Value) {
        return ""
    }

    return $Value.Replace('"', '""')
}

function Normalize-FileNameSegment {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "unknown"
    }

    $normalized = $Value.Trim()
    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($char in $invalidChars) {
        $normalized = $normalized.Replace($char, '-')
    }

    $normalized = $normalized.Replace(' ', '-')
    while ($normalized.Contains('--')) {
        $normalized = $normalized.Replace('--', '-')
    }

    return $normalized.Trim('-')
}

function Convert-ToNetSparkleOs {
    param(
        [string]$RuntimeValue
    )

    $safeRuntimeValue = if ($null -eq $RuntimeValue) { "" } else { $RuntimeValue }
    $normalized = $safeRuntimeValue.Trim().ToLowerInvariant()
    switch ($normalized) {
        "win-x64" { return "windows-x64" }
        "win-x86" { return "windows-x86" }
        "win-arm64" { return "windows-arm64" }
        "linux-x64" { return "linux-x64" }
        "linux-arm64" { return "linux-arm64" }
        "osx-x64" { return "macos-x64" }
        "osx-arm64" { return "macos-arm64" }
        default {
            if ($normalized.StartsWith("win-")) {
                return "windows-" + $normalized.Substring(4)
            }
            if ($normalized.StartsWith("osx-")) {
                return "macos-" + $normalized.Substring(4)
            }
            return $RuntimeValue
        }
    }
}

function Get-ReleaseArtifacts {
    param(
        [string]$ReleaseDirectory,
        [string]$InstallerFileName
    )

    $artifacts = New-Object System.Collections.Generic.List[object]
    $installerPath = Join-Path $ReleaseDirectory $InstallerFileName

    if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
        $artifacts.Add([pscustomobject]@{
                RelativePath = $InstallerFileName
                Kind = "installer"
                LocalPath = $installerPath
                RemoteKey = ("app-releases/{0}" -f $InstallerFileName)
            })
    }

    Get-ChildItem -LiteralPath $ReleaseDirectory -File | Where-Object {
        $_.Name -like "appcast*"
    } | Sort-Object Name | ForEach-Object {
        $artifacts.Add([pscustomobject]@{
                RelativePath = $_.Name
                Kind = if ($_.Name -eq "appcast.xml") { "appcast" } else { "signature" }
                LocalPath = $_.FullName
                RemoteKey = ("app-releases/{0}" -f $_.Name)
            })
    }

    return $artifacts
}

function Write-UploadManifest {
    param(
        [string]$ReleaseDirectory,
        [string]$InstallerFileName,
        [string]$InternalVersionValue,
        [string]$DisplayVersionValue,
        [string]$RuntimeValue,
        [string]$ChannelValue,
        [string]$AppCastBaseUrlValue
    )

    $artifacts = Get-ReleaseArtifacts -ReleaseDirectory $ReleaseDirectory -InstallerFileName $InstallerFileName
    if ($artifacts.Count -eq 0) {
        throw "No release artifacts found in $ReleaseDirectory"
    }

    $artifactPayload = foreach ($artifact in $artifacts) {
        $hash = Get-FileHash -LiteralPath $artifact.LocalPath -Algorithm SHA256
        $file = Get-Item -LiteralPath $artifact.LocalPath
        [pscustomobject]@{
            kind = $artifact.Kind
            relativePath = $artifact.RelativePath
            remoteKey = $artifact.RemoteKey
            sizeBytes = $file.Length
            sha256 = $hash.Hash.ToLowerInvariant()
        }
    }

    $manifest = [pscustomobject]@{
        schema = 1
        generatedUtc = [DateTime]::UtcNow.ToString("o")
        product = "VRCLS"
        internalVersion = $InternalVersionValue
        displayVersion = $DisplayVersionValue
        runtime = $RuntimeValue
        channel = $ChannelValue
        remotePrefix = "app-releases/"
        appCastBaseUrl = $AppCastBaseUrlValue
        artifacts = $artifactPayload
    }

    $manifestPath = Join-Path $ReleaseDirectory "upload-manifest.json"
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    return $manifestPath
}

function Update-AppCastMetadata {
    param(
        [string]$AppCastPath,
        [string]$InternalVersionValue,
        [string]$DisplayVersionValue
    )

    if (-not (Test-Path -LiteralPath $AppCastPath -PathType Leaf)) {
        throw "App cast not found: $AppCastPath"
    }

    [xml]$appCast = Get-Content -LiteralPath $AppCastPath -Raw
    $sparkleNamespace = "http://www.andymatuschak.org/xml-namespaces/sparkle"
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($appCast.NameTable)
    $namespaceManager.AddNamespace("sparkle", $sparkleNamespace)

    $itemNode = $appCast.SelectSingleNode("/rss/channel/item", $namespaceManager)
    if ($null -eq $itemNode) {
        throw "App cast item node not found in $AppCastPath"
    }

    $titleNode = $itemNode.SelectSingleNode("title", $namespaceManager)
    if ($null -ne $titleNode) {
        $titleNode.InnerText = "VRCLS $DisplayVersionValue"
    }

    $versionNode = $itemNode.SelectSingleNode("sparkle:version", $namespaceManager)
    if ($null -ne $versionNode) {
        $versionNode.InnerText = $InternalVersionValue
    }

    $shortVersionNode = $itemNode.SelectSingleNode("sparkle:shortVersionString", $namespaceManager)
    if ($null -ne $shortVersionNode) {
        $shortVersionNode.InnerText = $DisplayVersionValue
    }

    $enclosureNode = $itemNode.SelectSingleNode("enclosure", $namespaceManager)
    if ($null -ne $enclosureNode) {
        $enclosureNode.SetAttribute("version", $sparkleNamespace, $InternalVersionValue)
        $enclosureNode.SetAttribute("shortVersionString", $sparkleNamespace, $DisplayVersionValue)
    }

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $false

    $writer = [System.Xml.XmlWriter]::Create($AppCastPath, $settings)
    try {
        $appCast.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Write-AppCastSignatureFile {
    param(
        [string]$AppCastGeneratorPathValue,
        [string]$AppCastPath,
        [string]$PrivateKeyValue
    )

    $output = & $AppCastGeneratorPathValue --generate-signature $AppCastPath --private-key-override $PrivateKeyValue 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to generate appcast signature for $AppCastPath"
    }

    $signatureLine = $output | Where-Object { $_ -match '^Signature:\s*(.+)$' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($signatureLine)) {
        throw "Appcast signature was not returned by netsparkle-generate-appcast."
    }

    $signatureValue = ([regex]::Match($signatureLine, '^Signature:\s*(.+)$')).Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($signatureValue)) {
        throw "Appcast signature value is empty."
    }

    $signaturePath = "$AppCastPath.signature"
    Set-Content -LiteralPath $signaturePath -Value $signatureValue -Encoding UTF8
    return $signaturePath
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScriptName = if ($IncludePreModels) { "publish_with_premodels.ps1" } else { "publish.ps1" }
$publishScriptPath = Join-Path $PSScriptRoot $publishScriptName
$publishDir = Resolve-RepoRelativePath -RepoRoot $repoRoot -PathValue $Output
$releaseOutputDir = Join-Path $repoRoot "Output\app-release"
$outputRootDir = Join-Path $repoRoot "Output"
$iconPath = Join-Path $repoRoot "03.VRCLS.UI\Assets\vrcls_logo.ico"
$tempIssPath = Join-Path $env:TEMP ("vrcls-netsparkle-installer-{0}.iss" -f [guid]::NewGuid().ToString("N"))
$isccPath = Resolve-IsccPath -ExplicitPath $InnoSetupCompiler
$appCastGeneratorPath = Resolve-AppCastGeneratorPath

if (-not (Test-Path -LiteralPath $publishScriptPath -PathType Leaf)) {
    throw "Publish script not found: $publishScriptPath"
}

if ([string]::IsNullOrWhiteSpace($InternalVersion)) {
    throw "InternalVersion is required."
}

$normalizedDisplayVersion = if ([string]::IsNullOrWhiteSpace($DisplayVersion)) {
    $InternalVersion.Trim()
} else {
    $DisplayVersion.Trim()
}

$normalizedChannel = if ([string]::IsNullOrWhiteSpace($Channel)) {
    ""
} else {
    $Channel.Trim()
}

$publicKey = Resolve-NetSparkleKey -ExplicitValue $NetSparklePublicKey -PrimaryName "VRCLS_NETSPARKLE_PUBLIC_KEY" -FallbackName "SPARKLE_PUBLIC_KEY"
if ([string]::IsNullOrWhiteSpace($publicKey)) {
    throw "NetSparkle public key is required. Set VRCLS_NETSPARKLE_PUBLIC_KEY or SPARKLE_PUBLIC_KEY."
}

$privateKey = Resolve-NetSparkleKey -ExplicitValue $NetSparklePrivateKey -PrimaryName "VRCLS_NETSPARKLE_PRIVATE_KEY" -FallbackName "SPARKLE_PRIVATE_KEY"
if ([string]::IsNullOrWhiteSpace($privateKey)) {
    throw "NetSparkle private key is required. Set VRCLS_NETSPARKLE_PRIVATE_KEY or SPARKLE_PRIVATE_KEY."
}

$resolvedAppCastBaseUrl = if ([string]::IsNullOrWhiteSpace($AppCastBaseUrl)) {
    $env:VRCLS_APPCAST_BASE_URL
} else {
    $AppCastBaseUrl
}
if (-not [string]::IsNullOrWhiteSpace($resolvedAppCastBaseUrl)) {
    $resolvedAppCastBaseUrl = $resolvedAppCastBaseUrl.Trim().TrimEnd('/')
}

$displayVersionFileSegment = Normalize-FileNameSegment $normalizedDisplayVersion
$netSparkleOs = Convert-ToNetSparkleOs -RuntimeValue $Runtime
$installerBaseName = "VRCLS-$displayVersionFileSegment-$Runtime-setup"
$installerFileName = "$installerBaseName.exe"
$releaseInstallerPath = Join-Path $releaseOutputDir $installerFileName
$topLevelInstallerPath = Join-Path $outputRootDir $installerFileName

Write-Host "Building VRCLS installer and NetSparkle release..." -ForegroundColor Cyan
Write-Host "  Publish Script   : $publishScriptPath"
Write-Host "  Config           : $Configuration"
Write-Host "  Runtime          : $Runtime"
Write-Host "  Publish Output   : $publishDir"
Write-Host "  Release Output   : $releaseOutputDir"
Write-Host "  Internal Version : $InternalVersion"
Write-Host "  Display Version  : $normalizedDisplayVersion"
Write-Host "  Channel          : $normalizedChannel"
Write-Host "  AppCast OS       : $netSparkleOs"
Write-Host "  Inno Compiler    : $isccPath"
Write-Host "  AppCast Tool     : $appCastGeneratorPath"
if (-not [string]::IsNullOrWhiteSpace($resolvedAppCastBaseUrl)) {
    Write-Host "  AppCast Base URL : $resolvedAppCastBaseUrl"
} else {
    Write-Host "  AppCast Base URL : <not set; appcast will use relative download URLs>" -ForegroundColor Yellow
}

Ensure-CleanDirectory -DirectoryPath $publishDir
Ensure-CleanDirectory -DirectoryPath $releaseOutputDir
New-Item -ItemType Directory -Force -Path $outputRootDir | Out-Null
if (Test-Path -LiteralPath $topLevelInstallerPath -PathType Leaf) {
    Remove-Item -LiteralPath $topLevelInstallerPath -Force
}

$publishCommand = @(
    "powershell",
    "-ExecutionPolicy", "Bypass",
    "-File", $publishScriptPath,
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-Output", $publishDir,
    "-NetSparklePublicKey", $publicKey
)

Write-Host "Publishing VRCLS..." -ForegroundColor Cyan
& $publishCommand[0] $publishCommand[1..($publishCommand.Length - 1)]
if ($LASTEXITCODE -ne 0) {
    throw "Publish script failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    throw "Publish output not found: $publishDir"
}

Assert-RequiredFiles -Context "Publish output" -Paths @(
    (Join-Path $publishDir "translator\translator.exe"),
    (Join-Path $publishDir "translator\translator-capabilities.json")
)

$issLines = @(
    '; Auto-generated by scripts/build-installer.ps1',
    '[Setup]',
    'AppId={{D8F6BB4A-3C4A-4B36-9A2E-0D5E9D4A6C31}',
    'AppName=VRCLS - VRChat LinguaSync',
    ('AppVersion="{0}"' -f (Escape-InnoValue $normalizedDisplayVersion)),
    ('AppVerName="VRCLS - VRChat LinguaSync {0}"' -f (Escape-InnoValue $normalizedDisplayVersion)),
    'AppPublisher=VoiceLinkVR',
    'DefaultDirName={localappdata}\Programs\VRCLS',
    'DefaultGroupName=VRCLS',
    'DisableProgramGroupPage=yes',
    'PrivilegesRequired=admin',
    'Compression=lzma2',
    'SolidCompression=yes',
    'WizardStyle=modern',
    'UsePreviousAppDir=yes',
    'UsePreviousTasks=yes',
    'CloseApplications=yes',
    'RestartApplications=no',
    'SetupLogging=yes',
    ('OutputDir="{0}"' -f (Escape-InnoValue $releaseOutputDir)),
    ('OutputBaseFilename="{0}"' -f (Escape-InnoValue $installerBaseName)),
    ('VersionInfoVersion="{0}"' -f (Escape-InnoValue $InternalVersion.Trim())),
    ('VersionInfoTextVersion="{0}"' -f (Escape-InnoValue $normalizedDisplayVersion)),
    'UninstallDisplayIcon={app}\VRCLS.exe'
)

if ($Runtime -like "*x64*") {
    $issLines += 'ArchitecturesInstallIn64BitMode=x64compatible'
}

if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
    $issLines += ('SetupIconFile="{0}"' -f (Escape-InnoValue $iconPath))
}

$publishSourceLine = ('Source: "{0}\*"; DestDir: "{{app}}"; Flags: ignoreversion recursesubdirs createallsubdirs' -f (Escape-InnoValue $publishDir.TrimEnd('\')))

$issLines += @(
    '',
    '[Tasks]',
    'Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce',
    '',
    '[Files]',
    $publishSourceLine,
    '',
    '[Icons]',
    'Name: "{group}\VRCLS"; Filename: "{app}\VRCLS.exe"; WorkingDir: "{app}"',
    'Name: "{autodesktop}\VRCLS"; Filename: "{app}\VRCLS.exe"; WorkingDir: "{app}"; Tasks: desktopicon',
    'Name: "{group}\Uninstall VRCLS"; Filename: "{uninstallexe}"',
    '',
    '[Run]',
    'Filename: "{app}\VRCLS.exe"; Description: "Launch VRCLS"; Flags: nowait postinstall skipifsilent'
)

Set-Content -LiteralPath $tempIssPath -Value ($issLines -join [Environment]::NewLine) -Encoding UTF8

try {
    Write-Host "Compiling Inno Setup installer..." -ForegroundColor Cyan
    & $isccPath $tempIssPath
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $tempIssPath -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $releaseInstallerPath -PathType Leaf)) {
    throw "Installer build succeeded but output file not found: $releaseInstallerPath"
}

Copy-Item -LiteralPath $releaseInstallerPath -Destination $topLevelInstallerPath -Force

$appCastCommand = @(
    $appCastGeneratorPath,
    "--single-file", $releaseInstallerPath,
    "--appcast-output-directory", $releaseOutputDir,
    "--file-version", $InternalVersion.Trim(),
    "--os", $netSparkleOs,
    "--product-name", "VRCLS",
    "--output-file-name", "appcast",
    "--human-readable",
    "--public-key-override", $publicKey,
    "--private-key-override", $privateKey
)

if (-not [string]::IsNullOrWhiteSpace($resolvedAppCastBaseUrl)) {
    $appCastCommand += @("--base-url", $resolvedAppCastBaseUrl)
    $appCastCommand += @("--link-tag", ($resolvedAppCastBaseUrl.TrimEnd('/') + "/appcast.xml"))
}

if (-not [string]::IsNullOrWhiteSpace($normalizedChannel) -and
    -not $normalizedChannel.Equals("stable", [System.StringComparison]::OrdinalIgnoreCase) -and
    -not $normalizedChannel.Equals("release", [System.StringComparison]::OrdinalIgnoreCase)) {
    $appCastCommand += @("--channel", $normalizedChannel)
}

Write-Host "Generating NetSparkle appcast..." -ForegroundColor Cyan
& $appCastCommand[0] $appCastCommand[1..($appCastCommand.Length - 1)]
if ($LASTEXITCODE -ne 0) {
    throw "NetSparkle appcast generation failed with exit code $LASTEXITCODE"
}

$appCastPath = Join-Path $releaseOutputDir "appcast.xml"
if (-not (Test-Path -LiteralPath $appCastPath -PathType Leaf)) {
    throw "NetSparkle appcast generation succeeded but appcast.xml was not found: $appCastPath"
}

Update-AppCastMetadata -AppCastPath $appCastPath -InternalVersionValue $InternalVersion.Trim() -DisplayVersionValue $normalizedDisplayVersion
$appCastSignaturePath = Write-AppCastSignatureFile -AppCastGeneratorPathValue $appCastGeneratorPath -AppCastPath $appCastPath -PrivateKeyValue $privateKey

$manifestPath = Write-UploadManifest `
    -ReleaseDirectory $releaseOutputDir `
    -InstallerFileName $installerFileName `
    -InternalVersionValue $InternalVersion.Trim() `
    -DisplayVersionValue $normalizedDisplayVersion `
    -RuntimeValue $Runtime `
    -ChannelValue $normalizedChannel `
    -AppCastBaseUrlValue $resolvedAppCastBaseUrl

Write-Host "Installer created: $releaseInstallerPath" -ForegroundColor Green
Write-Host "Top-level installer copy: $topLevelInstallerPath" -ForegroundColor Green
Write-Host "NetSparkle appcast: $appCastPath" -ForegroundColor Green
Write-Host "NetSparkle appcast signature: $appCastSignaturePath" -ForegroundColor Green
Write-Host "Upload manifest: $manifestPath" -ForegroundColor Green
