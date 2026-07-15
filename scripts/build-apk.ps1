# Build a distributable, release-signed Android APK for the stock-analyzer app.
#
# Usage (in PowerShell):
#     .\scripts\build-apk.ps1
# Run from anywhere; paths are resolved relative to this script's location.
#
# Layout (unified 2026-07-15): the mobile project tree lives under src\StockPlatform.Mobile\
# (a sibling of the other src projects); this script + the signing keystore live under scripts\;
# the built APK is copied into publish\ next to the desktop exes.
#
# The signing keystore (scripts\stockanalyzer.keystore) is auto-created on first run.
# IMPORTANT: keep that keystore safe and never change its password. Every future release MUST be
# signed with the SAME keystore, or users cannot update/install (Android treats a differently-
# signed APK as a different app). It is gitignored (*.keystore) — do not commit it, but back it up.
#
# Requirements: .NET 9 + android workload, JDK (keytool), Android SDK. Auto-located below.
#
# ASCII-only on purpose: a .ps1 with non-ASCII text is mis-decoded by Windows PowerShell 5.1
# unless saved with a BOM, which is fragile. English keeps it robust.

# Native tools (keytool) write progress to stderr; with 'Stop' PowerShell 5.1 would treat that as
# fatal. Use Continue + explicit exit-code checks instead.
$ErrorActionPreference = 'Continue'

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

$scriptDir = $PSScriptRoot
$repoRoot  = Split-Path -Parent $scriptDir
$mobileDir = Join-Path $repoRoot 'src\StockPlatform.Mobile'
$proj      = Join-Path $mobileDir 'StockPlatform.Mobile.Android\StockPlatform.Mobile.Android.csproj'
$keystore  = Join-Path $scriptDir 'stockanalyzer.keystore'
$publishDir = Join-Path $repoRoot 'publish'
$alias     = 'stockanalyzer'
$storePass = 'stockanalyzer'
$keyPass   = 'stockanalyzer'

if (-not (Test-Path $proj)) { Fail "Android project not found at $proj" }

# --- Locate Android SDK ---
if (-not $env:ANDROID_HOME) {
    $candidates = @('C:\Program Files (x86)\Android\android-sdk', (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))
    $sdk = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sdk) { Fail 'Android SDK not found. Set ANDROID_HOME and retry.' }
    $env:ANDROID_HOME = $sdk
    $env:ANDROID_SDK_ROOT = $sdk
}
Write-Host "Android SDK: $env:ANDROID_HOME"

# --- Locate keytool ---
$kt = Get-Command keytool -ErrorAction SilentlyContinue
$keytool = if ($kt) { $kt.Source } else { $null }
if (-not $keytool) {
    $j = Get-ChildItem 'C:\Program Files\Microsoft\jdk-*\bin\keytool.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($j) { $keytool = $j.FullName }
}
if (-not $keytool) { Fail 'keytool (JDK) not found. Install a JDK.' }

# --- Create keystore on first run ---
if (-not (Test-Path $keystore)) {
    Write-Host "Creating signing keystore: $keystore"
    & $keytool -genkeypair -keystore $keystore -alias $alias -keyalg RSA -keysize 2048 `
        -validity 10000 -storepass $storePass -keypass $keyPass `
        -dname 'CN=Chingli, OU=Personal, O=Personal, L=NA, S=NA, C=CN' 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $keystore)) { Fail 'keytool failed to create keystore.' }
} else {
    Write-Host "Using existing keystore: $keystore"
}

# --- Publish release, signed APK ---
Write-Host 'Building release signed APK (first build is slow)...'
& dotnet publish $proj -c Release -f net9.0-android `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore=$keystore `
    -p:AndroidSigningStorePass=$storePass `
    -p:AndroidSigningKeyAlias=$alias `
    -p:AndroidSigningKeyPass=$keyPass 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed (see output above).' }

# --- Collect artifact ---
$outRoot = Join-Path $mobileDir 'StockPlatform.Mobile.Android\bin\Release\net9.0-android'
$apk = Get-ChildItem $outRoot -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { Fail 'Signed APK not found under bin\Release. Check the build output.' }

# --- Copy into publish\ (only add one file; NEVER remove anything, esp. publish\data with real data) ---
if (-not (Test-Path $publishDir)) { New-Item -ItemType Directory -Force -Path $publishDir | Out-Null }
if (-not (Test-Path (Join-Path $publishDir 'data'))) {
    Write-Warning "publish\data not found here — double-check this is the right publish folder (not created by this script)."
}
$stamp = Get-Date -Format 'yyyyMMdd'
$out = Join-Path $publishDir "stockanalyzer-$stamp.apk"
Copy-Item $apk.FullName $out -Force

$mb = [math]::Round(((Get-Item $out).Length / 1MB), 1)
Write-Host ''
Write-Host "DONE: $out  ($mb MB)" -ForegroundColor Green
Write-Host 'Send this .apk to a phone, allow install from unknown sources, then tap to install.'
