param(
    [string]$InputPath = "",
    [string]$OutputPath = ""
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$voiceBaseName = [string]::Concat([char]0x97F3, [char]0x8272) # 音色

if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = Join-Path $repoRoot "$voiceBaseName.md"
}
elseif (-not [System.IO.Path]::IsPathRooted($InputPath)) {
    $InputPath = Join-Path (Get-Location) $InputPath
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "$voiceBaseName.json"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path (Get-Location) $OutputPath
}

if (-not (Test-Path -LiteralPath $InputPath)) {
    throw "Input file not found: $InputPath"
}

$raw = Get-Content -LiteralPath $InputPath -Raw
$pattern = '\b[a-z]{2,3}(?:-[A-Za-z0-9:]+){2,10}Neural\b'

$voices = [regex]::Matches($raw, $pattern) |
    ForEach-Object { $_.Value.Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique

$payload = [ordered]@{
    source         = $InputPath
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    count          = $voices.Count
    voices         = $voices
}

$dir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Generated $OutputPath with $($voices.Count) voices."
