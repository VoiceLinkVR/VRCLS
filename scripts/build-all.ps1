param(
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist-sites"
$portalRoot = Join-Path $repoRoot "portal"
$adminRoot = Join-Path $repoRoot "admin"

function Invoke-NpmBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    if ($Install -or -not (Test-Path (Join-Path $ProjectRoot "node_modules"))) {
        Write-Host "Installing dependencies in $ProjectRoot..."
        Push-Location $ProjectRoot
        try {
            npm ci
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed in $ProjectRoot"
            }
        } finally {
            Pop-Location
        }
    }

    Write-Host "Building $ProjectRoot..."
    Push-Location $ProjectRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed in $ProjectRoot"
        }
    } finally {
        Pop-Location
    }
}

if (Test-Path $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $distRoot | Out-Null

Invoke-NpmBuild -ProjectRoot $portalRoot
Invoke-NpmBuild -ProjectRoot $adminRoot

Copy-Item -LiteralPath (Join-Path $repoRoot "index.html") -Destination (Join-Path $distRoot "index.html")
Copy-Item -LiteralPath (Join-Path $repoRoot "assets") -Destination (Join-Path $distRoot "assets") -Recurse

New-Item -ItemType Directory -Path (Join-Path $distRoot "status") | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "status\index.html") -Destination (Join-Path $distRoot "status\index.html")

Copy-Item -Path (Join-Path $portalRoot "dist") -Destination (Join-Path $distRoot "app") -Recurse
Copy-Item -Path (Join-Path $adminRoot "dist") -Destination (Join-Path $distRoot "admin") -Recurse

Write-Host "All sites built successfully into $distRoot"
