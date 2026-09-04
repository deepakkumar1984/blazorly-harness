# blazorly installer — Windows (pi.dev style one-liner):
#   powershell -c "irm https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.ps1 | iex"
# Offline / local testing: $env:BLAZORLY_INSTALL_BASE = 'C:\path\to\dist'; iex installer/install.ps1
param(
    [string]$Repo = "deepakkumar1984/blazorly-harness",
    [string]$Base = ""
)
$ErrorActionPreference = "Stop"

if (-not $Base) { $Base = "https://github.com/$Repo/releases/latest/download" }

$rid = switch ($env:PROCESSOR_ARCHITECTURE) {
    "ARM64" { "win-arm64" }
    default { "win-x64" }
}
$archive = "blazorly-$rid.zip"

Write-Host "===> downloading $archive" -ForegroundColor Cyan
$tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP ("blazorly-install-" + [guid]::NewGuid().ToString("N").Substring(0, 8)))
try {
    $zipPath = Join-Path $tmp $archive
    try {
        Invoke-WebRequest -Uri "$Base/$archive" -OutFile $zipPath -UseBasicParsing
    } catch {
        throw "download failed - is a release published for $rid? ($_)"
    }

    if ($env:BLAZORLY_SKIP_VERIFY -ne "1") {
        Write-Host "===> verifying checksum" -ForegroundColor Cyan
        try {
            $sumPath = Join-Path $tmp "$archive.sha256"
            Invoke-WebRequest -Uri "$Base/$archive.sha256" -OutFile $sumPath -UseBasicParsing
            $want = (Get-Content $sumPath | Select-Object -First 1).Split(" ")[0].ToLower()
            $got = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
            if ($want -ne $got) { throw "checksum mismatch - aborting (set BLAZORLY_SKIP_VERIFY=1 to skip)" }
        } catch [System.Net.WebException] {
            Write-Host "     no checksum published; skipping verification"
        }
    }

    $dest = if ($env:BLAZORLY_INSTALL_DIR) { $env:BLAZORLY_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "blazorly\app" }
    $current = Join-Path $dest "current"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    if (Test-Path $current) { Remove-Item -Recurse -Force $current }
    Expand-Archive -Path $zipPath -DestinationPath $current -Force
    $version = if (Test-Path (Join-Path $current "VERSION")) { Get-Content (Join-Path $current "VERSION") } else { "?" }
    Write-Host "===> installed blazorly $version to $current" -ForegroundColor Cyan

    # shim + PATH
    $binDir = if ($env:BLAZORLY_BIN_DIR) { $env:BLAZORLY_BIN_DIR } else { Join-Path $env:USERPROFILE ".blazorly\bin" }
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    $exe = Join-Path $current "blazorly.exe"
    $cmd = Join-Path $binDir "blazorly.cmd"
    "@`"$exe`" %*" | Set-Content -Path $cmd -Encoding ASCII
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$binDir*") {
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$binDir", "User")
        Write-Host "===> added $binDir to your user PATH (new terminals only)" -ForegroundColor Cyan
    }

    Write-Host "===> run 'blazorly' to start the UI (http://localhost:5080), 'blazorly --help' for all modes" -ForegroundColor Cyan
    Write-Host "note: Windows has no Landlock sandbox: bash/run_code fail closed until you switch" -ForegroundColor Yellow
    Write-Host "      the session permission preset to danger-full-access (/permission)." -ForegroundColor Yellow
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
