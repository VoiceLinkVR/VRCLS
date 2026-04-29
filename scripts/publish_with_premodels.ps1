param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$Output = "publish_with_premodels",

    [string]$NetSparklePublicKey = "",

    [switch]$Zip
)

$ErrorActionPreference = "Stop"

function Ensure-TranslatorRuntime {
    param(
        [string]$RepoRoot
    )

    $translatorBuildScript = Join-Path $RepoRoot "scripts\build_translator.ps1"
    $translatorRuntimeDir = Join-Path $RepoRoot "tools\translator"
    $translatorExePath = Join-Path $translatorRuntimeDir "translator.exe"
    $translatorCapabilitiesPath = Join-Path $translatorRuntimeDir "translator-capabilities.json"

    if (-not (Test-Path -LiteralPath $translatorBuildScript -PathType Leaf)) {
        throw "Translator build script not found: $translatorBuildScript"
    }

    Write-Host "Building translator runtime..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File $translatorBuildScript -ProjectRoot $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Translator runtime build failed with exit code $LASTEXITCODE"
    }

    foreach ($requiredPath in @($translatorExePath, $translatorCapabilitiesPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Translator runtime is incomplete after build: missing $requiredPath"
        }
    }
}

function Assert-PublishTranslatorFiles {
    param(
        [string]$PublishDir
    )

    $requiredFiles = @(
        (Join-Path $PublishDir "translator\translator.exe"),
        (Join-Path $PublishDir "translator\translator-capabilities.json")
    )

    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Publish output is missing required translator runtime file: $requiredFile"
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = if ([System.IO.Path]::IsPathRooted($Output)) {
    [System.IO.Path]::GetFullPath($Output)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
}
$project = Join-Path $repoRoot "03.VRCLS.UI\03.VRCLS.UI.csproj"

Write-Host "Publishing VRCLS (with pre-models)..." -ForegroundColor Cyan
Write-Host "  Project: $project"
Write-Host "  Config : $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Output : $outDir"

if (Test-Path -LiteralPath $outDir) {
    Remove-Item -LiteralPath $outDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Ensure-TranslatorRuntime -RepoRoot $repoRoot

$publishArgs = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained",
    "-o", $outDir
)

if (-not [string]::IsNullOrWhiteSpace($NetSparklePublicKey)) {
    $publishArgs += "/p:NetSparklePublicKey=$NetSparklePublicKey"
}

& dotnet $publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Assert-PublishTranslatorFiles -PublishDir $outDir

$modelDirs = @(
    (Join-Path $outDir "pre-models"),
    (Join-Path $outDir "models")
)
foreach ($d in $modelDirs) {
    if (Test-Path -LiteralPath $d) {
        Write-Host "Removing model dir from publish output: $d" -ForegroundColor Yellow
        Remove-Item -LiteralPath $d -Recurse -Force
    }
}

$modelExt = @("*.onnx", "tokens.txt")
foreach ($pat in $modelExt) {
    Get-ChildItem -Path $outDir -Recurse -Force -File -Filter $pat -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Removing model file from publish output: $($_.FullName)" -ForegroundColor Yellow
        Remove-Item -LiteralPath $_.FullName -Force
    }
}

$preModelsSrc = Join-Path $repoRoot "pre-models"
$preModelsDst = Join-Path $outDir "pre-models"
New-Item -ItemType Directory -Force -Path $preModelsDst | Out-Null

$bilingualDir = Join-Path $preModelsSrc "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"
$vadFile = Join-Path $preModelsSrc "silero_vad.onnx"

if (Test-Path -LiteralPath $bilingualDir) {
    Copy-Item -LiteralPath $bilingualDir -Destination $preModelsDst -Recurse -Force
    Write-Host "Copied bilingual model: $bilingualDir" -ForegroundColor Green
} else {
    Write-Warning "Bilingual model folder not found: $bilingualDir"
}

if (Test-Path -LiteralPath $vadFile) {
    Copy-Item -LiteralPath $vadFile -Destination $preModelsDst -Force
    Write-Host "Copied VAD model: $vadFile" -ForegroundColor Green
} else {
    Write-Warning "VAD model file not found: $vadFile"
}

if ($Zip) {
    $zipPath = Join-Path $repoRoot ("VRCLS-$Configuration-$Runtime-pre-models.zip")
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Write-Host "Creating zip: $zipPath" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath
}

Write-Host "Done." -ForegroundColor Green
