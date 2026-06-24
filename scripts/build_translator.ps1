param(
    [string]$Python = "python",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [ValidateSet("CN", "EN")]
    [string]$TranslatorDefaultRegion = "CN"
)

$ErrorActionPreference = "Stop"

$src = Join-Path $ProjectRoot "scripts\\translator.py"
$req = Join-Path $ProjectRoot "scripts\\requirements_translator.txt"
$capabilitiesScript = Join-Path $ProjectRoot "scripts\\generate_translator_capabilities.py"
$outDir = Join-Path $ProjectRoot "tools"
$outOnedir = Join-Path $outDir "translator"

if (!(Test-Path $src)) { throw "Missing $src" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ([string]::IsNullOrWhiteSpace($env:translators_default_region)) {
    $env:translators_default_region = $TranslatorDefaultRegion
}
Write-Host "Using translators_default_region=$env:translators_default_region"

Write-Host "Installing translator Python dependencies..."
& $Python -m pip install -r $req

$tmp = Join-Path $env:TEMP ("vrcls_translator_build_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    Push-Location $tmp
    # Use onedir to reduce startup overhead (onefile extracts to temp on each start).
    Write-Host "Running PyInstaller for translator runtime..."
    & $Python -m PyInstaller --onedir --name translator --python-option "X utf8" --collect-all translators --collect-submodules translators $src

    # Copy dist\translator\* to tools\translator\*
    Write-Host "Copying translator runtime to $outOnedir..."
    if (Test-Path $outOnedir) { Remove-Item -Recurse -Force $outOnedir }
    Copy-Item -Recurse -Force (Join-Path $tmp "dist\\translator") $outOnedir

    Write-Host "Generating translator capabilities..."
    & $Python $capabilitiesScript --project-root $ProjectRoot --output (Join-Path $outOnedir "translator-capabilities.json")
    Write-Host ("Built (onedir): " + $outOnedir)
}
finally {
    Pop-Location
}
