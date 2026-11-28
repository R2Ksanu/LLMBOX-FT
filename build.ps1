<#
.SYNOPSIS
    LlmodelBox Dual Builder for Windows (supports Windows builds and Linux-style local download web hosting).
#>
[CmdletBinding()]
param(
    [ValidateSet("", "windows", "linux")]
    [string]$Mode = "",

    [ValidateSet("", "standalone", "light")]
    [string]$Package = "",

    [switch]$Serve,
    [switch]$NoServe,
    [switch]$ServeOnly,
    [int]$Port = 8080,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

$ProjectFile = Join-Path $RepoRoot "src\LlmodelBox.csproj"
$TestProject = Join-Path $RepoRoot "tests\LlmodelBox.Tests\LlmodelBox.Tests.csproj"
$ServerScript = Join-Path $RepoRoot "tools\local-download-server.py"
$ReleaseDir = Join-Path $RepoRoot "dist\release"

if (-not $ServeOnly -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK 8.0+ was not found on PATH."
    exit 1
}

if (-not $ServeOnly -and [string]::IsNullOrWhiteSpace($Mode)) {
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "  LlmodelBox Dual Builder (Windows)" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Choose build profile:"
    Write-Host "  [1] Windows Build (Test + Publish .exe)"
    Write-Host "  [2] Release Package + Local Download Website (win-x64 .exe + .zip)"
    Write-Host "  [3] Serve Existing Local Download Website (dist\release)"
    $modeChoice = Read-Host "Select [1/2/3] (default 1)"
    if ([string]::IsNullOrWhiteSpace($modeChoice)) { $modeChoice = "1" }
    switch ($modeChoice) {
        "1" { $Mode = "windows" }
        "2" { $Mode = "linux" }
        "3" { $ServeOnly = $true; $shouldServe = $true }
        default { Write-Error "Invalid choice: $modeChoice"; exit 1 }
    }
}

if (-not $ServeOnly) {
    if ([string]::IsNullOrWhiteSpace($Package)) {
        Write-Host ""
        Write-Host "Choose packaging mode:"
        Write-Host "  [1] Portable Single-File .exe (Self-Contained win-x64, no .NET runtime needed)"
        Write-Host "  [2] Lightweight Framework-Dependent (app\improved, requires .NET 8 Desktop Runtime)"
        $pkgChoice = Read-Host "Select [1/2] (default 1)"
        if ([string]::IsNullOrWhiteSpace($pkgChoice)) { $pkgChoice = "1" }
        switch ($pkgChoice) {
            "1" { $Package = "standalone" }
            "2" { $Package = "light" }
            default { Write-Error "Invalid choice: $pkgChoice"; exit 1 }
        }
    }

    $shouldServe = $false
    if ($Serve) {
        $shouldServe = $true
    } elseif ($NoServe) {
        $shouldServe = $false
    } elseif ($Mode -eq "linux") {
        $ans = Read-Host "Start local download website after build? [Y/n]"
        $shouldServe = [string]::IsNullOrWhiteSpace($ans) -or ($ans -notmatch "^[Nn]$")
    } else {
        $ans = Read-Host "Start local download website after build? [y/N]"
        $shouldServe = ($ans -match "^[Yy]$")
    }
} else {
    $shouldServe = $true
}

if (-not $ServeOnly) {
    if (-not $SkipTests) {
        Write-Host ""
        Write-Host "[1/3] Running automated tests..." -ForegroundColor Yellow
        & dotnet test $TestProject
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Write-Host ""
    Write-Host "[2/3] Publishing LlmodelBox (win-x64, package=$Package)..." -ForegroundColor Yellow

    if ($Package -eq "standalone") {
        $OutDir = Join-Path $RepoRoot "dist\win-x64-standalone"
        $PkgLabel = "Portable Single-File (Self-Contained win-x64)"
        & dotnet publish $ProjectFile `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableWindowsTargeting=true `
            -o $OutDir
    } else {
        $OutDir = Join-Path $RepoRoot "app\improved"
        $PkgLabel = "Framework-Dependent (win-x64)"
        & dotnet publish $ProjectFile `
            -c Release `
            -r win-x64 `
            --no-self-contained `
            -p:EnableWindowsTargeting=true `
            -o $OutDir
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$PythonCmd = $null
if (Get-Command python -ErrorAction SilentlyContinue) {
    $PythonCmd = @("python")
} elseif (Get-Command py -ErrorAction SilentlyContinue) {
    $PythonCmd = @("py", "-3")
} else {
    $discoveredPy = Get-ChildItem -Path "$env:LOCALAPPDATA\Programs\Python","$env:ProgramFiles\Python*" -Filter "python.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if ($discoveredPy) {
        $PythonCmd = @($discoveredPy)
    }
}

if (-not $ServeOnly) {
    Write-Host ""
    Write-Host "[3/3] Staging release artifacts..." -ForegroundColor Yellow

    if ($null -ne $PythonCmd) {
        $pyExe = $PythonCmd[0]
        $pyArgs = @()
        if ($PythonCmd.Count -gt 1) { $pyArgs += $PythonCmd[1] }
        $pyArgs += @(
            $ServerScript,
            "--build-dir", $OutDir,
            "--release-dir", $ReleaseDir,
            "--repo-root", $RepoRoot,
            "--package-label", $PkgLabel,
            "--port", "$Port"
        )
        if (-not $shouldServe) {
            $pyArgs += "--prepare-only"
        }
        & $pyExe @pyArgs
        exit $LASTEXITCODE
    }

    # Fallback without Python: stage zip + checksums + metadata in pure PowerShell
    $ExeSource = Join-Path $OutDir "LlmodelBox.exe"
    $ExeTarget = Join-Path $ReleaseDir "LlmodelBox.exe"
    $ZipTarget = Join-Path $ReleaseDir "LlmodelBox-win-x64.zip"
    $SumTarget = Join-Path $ReleaseDir "SHA256SUMS.txt"
    $MetaTarget = Join-Path $ReleaseDir "release_meta.json"

    Copy-Item -Path $ExeSource -Destination $ExeTarget -Force
    if (Test-Path $ZipTarget) { Remove-Item $ZipTarget -Force }

    $StageZip = Join-Path $ReleaseDir "_zip_stage"
    if (Test-Path $StageZip) { Remove-Item $StageZip -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $StageZip | Out-Null

    Get-ChildItem -Path $OutDir -Recurse -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
        $rel = $_.FullName.Substring($OutDir.Length).TrimStart('\', '/')
        $dest = Join-Path $StageZip $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
    foreach ($extra in @("models.json", "README.md")) {
        $p = Join-Path $RepoRoot $extra
        if (Test-Path $p) { Copy-Item $p (Join-Path $StageZip $extra) -Force }
    }
    Compress-Archive -Path (Join-Path $StageZip "*") -DestinationPath $ZipTarget -Force
    Remove-Item $StageZip -Recurse -Force

    $exeSize = (Get-Item $ExeTarget).Length
    $zipSize = (Get-Item $ZipTarget).Length
    $exeHash = (Get-FileHash $ExeTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = (Get-FileHash $ZipTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path $SumTarget -Value "$exeHash  LlmodelBox.exe`n$zipHash  LlmodelBox-win-x64.zip" -Encoding UTF8

    $metaObj = [ordered]@{
        package_label  = $PkgLabel
        is_single_file = -not (Test-Path (Join-Path $OutDir "LlmodelBox.dll"))
        build_host     = "Windows ($env:PROCESSOR_ARCHITECTURE)"
        target_rid     = "win-x64"
        branch         = "main"
        commit         = "local"
        built_at       = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
        exe_name       = "LlmodelBox.exe"
        exe_size       = $exeSize
        exe_size_human = "{0:N2} MB" -f ($exeSize / 1MB)
        exe_sha256     = $exeHash
        zip_name       = "LlmodelBox-win-x64.zip"
        zip_size       = $zipSize
        zip_size_human = "{0:N2} MB" -f ($zipSize / 1MB)
        zip_sha256     = $zipHash
    }
    $metaObj | ConvertTo-Json -Depth 4 | Set-Content -Path $MetaTarget -Encoding UTF8

    Write-Host "Prepared release in: $ReleaseDir"
    Write-Host "  - LlmodelBox.exe ($exeHash)"
    Write-Host "  - LlmodelBox-win-x64.zip ($zipHash)"
} else {
    $ExeTarget = Join-Path $ReleaseDir "LlmodelBox.exe"
    $ZipTarget = Join-Path $ReleaseDir "LlmodelBox-win-x64.zip"
    $SumTarget = Join-Path $ReleaseDir "SHA256SUMS.txt"
    $MetaTarget = Join-Path $ReleaseDir "release_meta.json"
    if (-not (Test-Path $ExeTarget)) {
        Write-Error "No staged release found at $ExeTarget. Run .\build.bat without -ServeOnly first."
        exit 1
    }
    if ($null -ne $PythonCmd) {
        $pyExe = $PythonCmd[0]
        $pyArgs = @()
        if ($PythonCmd.Count -gt 1) { $pyArgs += $PythonCmd[1] }
        $pyArgs += @($ServerScript, "--release-dir", $ReleaseDir, "--port", "$Port", "--serve-only")
        & $pyExe @pyArgs
        exit $LASTEXITCODE
    }
    $PkgLabel = "win-x64 Release"
    if (Test-Path $MetaTarget) {
        try {
            $parsed = Get-Content $MetaTarget -Raw | ConvertFrom-Json
            if ($parsed.package_label) { $PkgLabel = $parsed.package_label }
        } catch {}
    }
    $exeHash = (Get-FileHash $ExeTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = if (Test-Path $ZipTarget) { (Get-FileHash $ZipTarget -Algorithm SHA256).Hash.ToLowerInvariant() } else { "unavailable" }
}

if (-not $shouldServe) { exit 0 }

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
try {
    $listener.Start()
} catch {
    Write-Error "Failed to start local download website on port $Port : $_"
    exit 1
}
Write-Host ""
Write-Host "Local Download Website running at http://localhost:$Port/ and http://127.0.0.1:$Port/ (Press Ctrl+C to stop)" -ForegroundColor Green

function Send-FileResponse {
    param($Response, [string]$FilePath, [string]$ContentType, [string]$FileName, [bool]$IncludeBody)
    if (-not (Test-Path $FilePath)) {
        $Response.StatusCode = 404
        return
    }
    $fi = Get-Item $FilePath
    $Response.ContentType = $ContentType
    $Response.AddHeader("Content-Disposition", "attachment; filename=`"$FileName`"")
    $Response.ContentLength64 = $fi.Length
    if ($IncludeBody) {
        $fs = [System.IO.File]::OpenRead($FilePath)
        try {
            $fs.CopyTo($Response.OutputStream, 262144)
        } finally {
            $fs.Dispose()
        }
    }
}

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request
        $reqPath = $req.Url.AbsolutePath
        $includeBody = ($req.HttpMethod -ne "HEAD")
        $resp = $ctx.Response

        try {
            if ($reqPath -eq "/" -or $reqPath -eq "/index.html") {
                $exeBytes = if (Test-Path $ExeTarget) { (Get-Item $ExeTarget).Length } else { 0 }
                $zipBytes = if (Test-Path $ZipTarget) { (Get-Item $ZipTarget).Length } else { 0 }
                $html = @"
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>LlmodelBox // Local Release Portal</title>
<style>body{font-family:Segoe UI,sans-serif;background:#0b0e14;color:#e6edf3;padding:40px;max-width:720px;margin:auto}
.card{background:#121722;border:1px solid #232b3b;border-radius:10px;padding:16px;margin:12px 0;display:flex;justify-content:space-between;align-items:center}
a.btn{background:#3b82f6;color:#fff;text-decoration:none;padding:10px 16px;border-radius:8px;font-weight:600}code{color:#93c5fd}</style></head>
<body><h1>LlmodelBox Release Download</h1><p>Packaging: <code>$PkgLabel</code></p>
<div class="card"><div><strong>LlmodelBox.exe</strong><br><small>$exeBytes bytes | SHA-256: $exeHash</small></div><a class="btn" href="/download/LlmodelBox.exe">Download .exe</a></div>
<div class="card"><div><strong>LlmodelBox-win-x64.zip</strong><br><small>$zipBytes bytes | SHA-256: $zipHash</small></div><a class="btn" href="/download/LlmodelBox-win-x64.zip">Download .zip</a></div>
<p><a href="/download/SHA256SUMS.txt" style="color:#93c5fd">SHA256SUMS.txt</a> &middot; <a href="/health" style="color:#93c5fd">/health</a></p></body></html>
"@
                $buf = [System.Text.Encoding]::UTF8.GetBytes($html)
                $resp.ContentType = "text/html; charset=utf-8"
                $resp.ContentLength64 = $buf.Length
                if ($includeBody) { $resp.OutputStream.Write($buf, 0, $buf.Length) }
            } elseif ($reqPath -eq "/health") {
                $jsonText = if (Test-Path $MetaTarget) { Get-Content $MetaTarget -Raw } else { (@{ status = "ok"; exe_sha256 = $exeHash; zip_sha256 = $zipHash } | ConvertTo-Json) }
                $buf = [System.Text.Encoding]::UTF8.GetBytes($jsonText)
                $resp.ContentType = "application/json; charset=utf-8"
                $resp.ContentLength64 = $buf.Length
                if ($includeBody) { $resp.OutputStream.Write($buf, 0, $buf.Length) }
            } elseif ($reqPath -eq "/download/LlmodelBox.exe") {
                Send-FileResponse -Response $resp -FilePath $ExeTarget -ContentType "application/vnd.microsoft.portable-executable" -FileName "LlmodelBox.exe" -IncludeBody $includeBody
            } elseif ($reqPath -eq "/download/LlmodelBox-win-x64.zip") {
                Send-FileResponse -Response $resp -FilePath $ZipTarget -ContentType "application/zip" -FileName "LlmodelBox-win-x64.zip" -IncludeBody $includeBody
            } elseif ($reqPath -eq "/download/SHA256SUMS.txt") {
                Send-FileResponse -Response $resp -FilePath $SumTarget -ContentType "text/plain; charset=utf-8" -FileName "SHA256SUMS.txt" -IncludeBody $includeBody
            } else {
                $resp.StatusCode = 404
            }
        } catch {
            # Ignore client-aborted transfers
        } finally {
            try { $resp.Close() } catch {}
        }
    }
} finally {
    $listener.Stop()
}
