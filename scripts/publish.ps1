<#
.SYNOPSIS
    Publishes StockPlatform.Fetcher and StockPlatform.Analyzer into publish/.
.DESCRIPTION
    Publishes both projects to an isolated temp folder first, then copies only the resulting
    .exe into publish/ — never runs `dotnet publish -o` directly into publish/, because that
    folder also holds the sibling exe and the local data/ directory, and a direct publish there
    can wipe out files it doesn't recognize as its own output. publish/data/ (real local
    watchlist/cache data) is never touched by this script.

    Run from anywhere; paths are resolved relative to this script's location.
.EXAMPLE
    .\scripts\publish.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"
$scratchRoot = Join-Path $env:TEMP ("stockpublish_" + [Guid]::NewGuid().ToString("N"))

$projects = @(
    @{ Name = "StockPlatform.Analyzer"; Csproj = Join-Path $repoRoot "src\StockPlatform.Analyzer\StockPlatform.Analyzer.csproj" },
    @{ Name = "StockPlatform.Fetcher";  Csproj = Join-Path $repoRoot "src\StockPlatform.Fetcher\StockPlatform.Fetcher.csproj" }
)

Write-Host "发布目录：$publishDir"
if (-not (Test-Path (Join-Path $publishDir "data"))) {
    Write-Warning "没找到 publish\data 目录，请确认这是不是正确的发布位置（脚本不会自动创建这个目录）"
}

New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null

try {
    foreach ($p in $projects) {
        $outDir = Join-Path $scratchRoot $p.Name
        Write-Host ""
        Write-Host "=== 正在编译发布 $($p.Name) ===" -ForegroundColor Cyan
        dotnet publish $p.Csproj -c Release -r win-x64 -o $outDir
        if ($LASTEXITCODE -ne 0) {
            throw "$($p.Name) 发布失败（exit code $LASTEXITCODE）——已停止，publish 目录里的文件没有被改动"
        }
        $exePath = Join-Path $outDir "$($p.Name).exe"
        if (-not (Test-Path $exePath)) {
            throw "$($p.Name) 发布过程没报错，但没找到 $exePath——已停止，publish 目录里的文件没有被改动"
        }
    }

    Write-Host ""
    Write-Host "=== 两个程序都编译成功，正在复制到 publish 目录（不会动 publish\data） ===" -ForegroundColor Cyan
    $copyFailed = @()
    foreach ($p in $projects) {
        $src = Join-Path (Join-Path $scratchRoot $p.Name) "$($p.Name).exe"
        $dst = Join-Path $publishDir "$($p.Name).exe"
        try {
            Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
            $info = Get-Item $dst
            Write-Host ("已更新 {0}（{1:N0} 字节，{2}）" -f $dst, $info.Length, $info.LastWriteTime)
        }
        catch {
            $copyFailed += $p.Name
            Write-Warning ("复制 {0} 失败：{1}" -f $p.Name, $_.Exception.Message)
            Write-Warning ("很可能是 {0}.exe 正在运行中占用了文件——请先完全关闭该程序，再单独重新执行这个脚本。" -f $p.Name)
        }
    }

    Write-Host ""
    if ($copyFailed.Count -gt 0) {
        Write-Host ("发布未完全完成——以下程序因为正在运行，没能更新：{0}" -f ($copyFailed -join "、")) -ForegroundColor Yellow
        exit 1
    }
    Write-Host "发布完成。如果 Fetcher/Analyzer 正在运行，请完全关闭再重新打开才能看到新版本（换掉磁盘上的exe不会影响已经在跑的进程）。" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $scratchRoot -ErrorAction SilentlyContinue
}
