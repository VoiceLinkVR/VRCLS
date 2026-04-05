param(
    [string]$Target = "root@192.168.2.53",
    [string]$RemoteSiteDir = "/srv/vrclswebsite",
    [string]$RemoteStatusProbeDir = "/srv/vrcls-status-probe",
    [string]$RemoteArchivePath = "/tmp/vrclswebsite-deploy.tar",
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$archivePath = Join-Path $repoRoot "deploy-site.tar"
$statusProbeRoot = Join-Path $repoRoot "status-api-demo"
$statusProbeSource = Join-Path $statusProbeRoot "*"

& (Join-Path $PSScriptRoot "pack-deploy.ps1") -ArchivePath $archivePath -Build:$Build
if ($LASTEXITCODE -ne 0) {
    throw "pack-deploy.ps1 failed"
}

ssh $Target "mkdir -p '$RemoteSiteDir' '$RemoteStatusProbeDir'"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to prepare remote directories"
}

scp $archivePath "${Target}:$RemoteArchivePath"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to upload site archive"
}

ssh $Target "tar -xf '$RemoteArchivePath' -C '$RemoteSiteDir'"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to extract site archive"
}

scp -r $statusProbeSource "${Target}:$RemoteStatusProbeDir/"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to sync status probe source"
}

Write-Host "Synchronized static site to $Target:$RemoteSiteDir"
Write-Host "Synchronized status probe source to $Target:$RemoteStatusProbeDir"
