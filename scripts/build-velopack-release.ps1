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

    [switch]$IncludePreModels
)

$ErrorActionPreference = "Stop"

$buildInstallerScriptPath = Join-Path $PSScriptRoot "build-installer.ps1"
if (-not (Test-Path -LiteralPath $buildInstallerScriptPath -PathType Leaf)) {
    throw "build-installer.ps1 not found: $buildInstallerScriptPath"
}

Write-Warning "build-velopack-release.ps1 has been deprecated. Forwarding to build-installer.ps1 (Inno Setup + NetSparkle)."

$command = @(
    "powershell",
    "-ExecutionPolicy", "Bypass",
    "-File", $buildInstallerScriptPath,
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-Output", $Output,
    "-InternalVersion", $InternalVersion,
    "-DisplayVersion", $DisplayVersion,
    "-Channel", $Channel
)

if (-not [string]::IsNullOrWhiteSpace($AppCastBaseUrl)) {
    $command += @("-AppCastBaseUrl", $AppCastBaseUrl)
}

if ($IncludePreModels) {
    $command += "-IncludePreModels"
}

& $command[0] $command[1..($command.Length - 1)]
exit $LASTEXITCODE
