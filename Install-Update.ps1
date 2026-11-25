param([string]$Target = 'G:\tools\LlmodelBox')
$ErrorActionPreference = 'Stop'
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $Target 'src\LlmodelBox.csproj'))) { throw 'Target is not the expected LlmodelBox workspace.' }
$changes = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'change-manifest.json') -Raw | ConvertFrom-Json
foreach ($change in $changes) {
    $destination = [IO.Path]::GetFullPath((Join-Path $Target $change.Path))
    if (!$destination.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
    $source = Join-Path $PSScriptRoot $change.Path
    if ((Get-FileHash -LiteralPath $source).Hash -ne $change.NewHash) { throw "Package checksum mismatch: $($change.Path)" }
    if (Test-Path -LiteralPath $destination) {
        $hash = (Get-FileHash -LiteralPath $destination).Hash
        if ($hash -ne $change.OriginalHash -and $hash -ne $change.NewHash) { throw "File changed since review: $($change.Path). No update applied." }
    } elseif ($change.OriginalHash) { throw "Expected file is missing: $($change.Path). No update applied." }
}
$backup = Join-Path $Target ('backups\before-improvements-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup | Out-Null
# Complete backups before updating any files.
$paths = @($changes | ForEach-Object { $_.Path }) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'app\improved') -File | ForEach-Object { 'app\improved\' + $_.Name })
$added = @()
foreach ($relative in $paths) {
    $destination = Join-Path $Target $relative
    if (Test-Path -LiteralPath $destination) {
        $copy = Join-Path $backup $relative
        New-Item -ItemType Directory -Force (Split-Path $copy) | Out-Null
        Copy-Item -LiteralPath $destination -Destination $copy
    } else { $added += $relative }
}
$added | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'new-files.json')
foreach ($relative in $paths) {
    $destination = Join-Path $Target $relative
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $relative)).Hash) { throw "Verification failed: $relative. Backup: $backup" }
}
Write-Host "Update installed. Launch using run.bat. Backup: $backup"
