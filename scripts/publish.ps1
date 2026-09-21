<#
.SYNOPSIS
    Publishes StockPlatform.Fetcher and StockPlatform.Analyzer into publish/.
.DESCRIPTION
    Publishes both projects to an isolated temp folder first, then copies only the resulting
    .exe into publish/ — never runs `dotnet publish -o` directly into publish/, because that
    folder also holds the sibling exe and the local data/ directory, and a direct publish there
    can wipe out files it doesn't recognize as its own output. publish/data/ (real local
    watchlist/cache data) is never touched by this script.

    If the target .exe is locked because the program is running, the copy is NOT abandoned:
    the exe is published alongside it as <Name>1.exe (a running process only locks its own
    file name, so a different name still writes fine). Close the program later, delete the old
    exe and rename that one back. The next run of this script cleans up the leftover <Name>1.exe
    automatically once the real name becomes writable again.

    Run from anywhere; paths are resolved relative to this script's location.
.EXAMPLE
    .\scripts\publish.ps1        # 两个都发
.EXAMPLE
    .\scripts\publish.ps1 -a     # 只发分析程序 Analyzer（Fetcher 正在运行、占用 exe 时用）
.EXAMPLE
    .\scripts\publish.ps1 -f     # 只发抓取程序 Fetcher（Analyzer 正在运行时用）
#>

param(
    # 不带参数=两个都发；-a 只发分析程序(Analyzer)；-f 只发抓取程序(Fetcher)。用于其中一个正在
    # 运行（exe 被占用）、只想发另一个的场景。同时给 -a -f 等于都发。
    [Alias("a")][switch]$Analyzer,
    [Alias("f")][switch]$Fetcher
)

# 一个都没指定 → 默认两个都发
if (-not $Analyzer -and -not $Fetcher) { $Analyzer = $true; $Fetcher = $true }

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"
$scratchRoot = Join-Path $env:TEMP ("stockpublish_" + [Guid]::NewGuid().ToString("N"))

$projects = @(
    @{ Name = "StockPlatform.Analyzer"; Csproj = Join-Path $repoRoot "src\StockPlatform.Desktop\StockPlatform.Analyzer\StockPlatform.Analyzer.csproj" },
    @{ Name = "StockPlatform.Fetcher";  Csproj = Join-Path $repoRoot "src\StockPlatform.Desktop\StockPlatform.Fetcher\StockPlatform.Fetcher.csproj" }
)
# 按 -a/-f 只保留要发的（其余逻辑不变：仍发到临时目录再拷 exe，不动 publish\data）
$projects = @($projects | Where-Object {
    ($Analyzer -and $_.Name -eq "StockPlatform.Analyzer") -or
    ($Fetcher -and $_.Name -eq "StockPlatform.Fetcher")
})

Write-Host ("发布目录：{0}（本次发布：{1}）" -f $publishDir, (($projects | ForEach-Object { $_.Name }) -join "、"))
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
    Write-Host ("=== Compiled {0}），Copying to publish folder（不会动 publish\data） ===" -f (($projects | ForEach-Object { $_.Name }) -join "、")) -ForegroundColor Cyan
    $suffixed = @()
    $copyFailed = @()
    foreach ($p in $projects) {
        $src = Join-Path (Join-Path $scratchRoot $p.Name) "$($p.Name).exe"
        $dst = Join-Path $publishDir "$($p.Name).exe"
        $alt = Join-Path $publishDir "$($p.Name)1.exe"
        try {
            Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
            $info = Get-Item $dst
            Write-Host ("已更新 {0}（{1:N0} 字节，{2}）" -f $dst, $info.Length, $info.LastWriteTime)

            # 正名写成功 ⇒ 上一次因占用留下的 <Name>1.exe 已经过时了，清掉。
            # 不清的话它会一直躺在 publish 里，而且跟正名的 exe 长得一样，很容易误开旧版本。
            if (Test-Path $alt) {
                Remove-Item -Path $alt -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path $alt)) {
                    Write-Host ("  顺手删掉了上次遗留的 {0}（已被这次的正名 exe 取代）" -f (Split-Path -Leaf $alt))
                }
            }
        }
        catch {
            # 程序正在跑 ⇒ 正名的 exe 被进程占用。**不放弃**：进程锁的是那个文件名，
            # 换个名字照样写得进去。发成 <Name>1.exe，等人关掉程序再改回来。
            # 这样"程序正跑着也能先把新版本放到位"，不用为了发布中断几小时的抓取任务。
            Write-Warning ("覆盖 {0}.exe 失败（多半是它正在运行、占用了文件）：{1}" -f $p.Name, $_.Exception.Message)
            try {
                Copy-Item -Path $src -Destination $alt -Force -ErrorAction Stop
                $info = Get-Item $alt
                Write-Host ("→ 已改名发布为 {0}（{1:N0} 字节，{2}）" -f $alt, $info.Length, $info.LastWriteTime) -ForegroundColor Yellow
                $suffixed += $p.Name
            }
            catch {
                $copyFailed += $p.Name
                Write-Warning ("连改名写入 {0} 也失败了：{1}" -f $alt, $_.Exception.Message)
            }
        }
    }

    Write-Host ""
    if ($copyFailed.Count -gt 0) {
        Write-Host ("发布失败——以下程序既没能覆盖、也没能改名写入：{0}" -f ($copyFailed -join "、")) -ForegroundColor Red
        exit 1
    }
    if ($suffixed.Count -gt 0) {
        Write-Host ("发布完成，但以下程序正在运行，新版本是改名放进去的：{0}" -f ($suffixed -join "、")) -ForegroundColor Yellow
        Write-Host "等它跑完之后收尾（三步）：" -ForegroundColor Yellow
        foreach ($nm in $suffixed) {
            Write-Host ("  {0}：① 完全关闭它  ② 删除 publish\{0}.exe  ③ 把 publish\{0}1.exe 改名成 {0}.exe" -f $nm)
        }
        Write-Host "（或者干脆等关掉程序后再跑一次这个脚本：正名能写了，它会自动覆盖并清掉那个带 1 的文件。）"
        exit 0
    }
    Write-Host "Success" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $scratchRoot -ErrorAction SilentlyContinue
}
