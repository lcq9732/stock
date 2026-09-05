# push2 可达性压测（2026-09-05）
#
# 为什么留这个脚本：程序里那条 WebView2 浏览器通道，是为了绕开"HttpClient 打 push2
# 第 7 个请求就被切"而建的（见 EastMoneyBoardFetcher 类注释里的实测对照表）。
# 2026-09-05 复测时那个前提**不成立了**——裸 HttpClient 连发 110 个请求零失败。
#
# ⚠ 但那次复测是在**周六（非交易日）**跑的，push2 盘中负载完全是另一回事。
#    所以结论必须在**交易日盘中**复跑一次才算数。这个脚本就是干这个的。
#
# 怎么读结果：
#   · 100/100 全过  → push2 对普通 HttpClient 通畅，浏览器通道（和它带来的图片验证码）
#                     可以降级成回退，成分股那 2500 个请求不用人守着了。
#   · 中途开始 X/0  → 老结论仍然成立，浏览器通道该留着。记下"第几个开始失败"。
#
# 用法：pwsh -File doc\push2-reachability-probe.ps1 [请求数] [间隔毫秒]

param([int]$Count = 100, [int]$DelayMs = 1200)

$ErrorActionPreference = 'Continue'

# 板块代码从菜单 JSON 现取——不依赖库，随处可跑
$menu = Invoke-WebRequest -Uri "https://quote.eastmoney.com/center/api/sidemenu_new.json" `
    -UseBasicParsing -TimeoutSec 25
$codes = @(($menu.Content | ConvertFrom-Json).bklist |
    Where-Object { $_.type -eq 3 } | Select-Object -First 70 | ForEach-Object { $_.code })
Write-Host "板块代码 $($codes.Count) 个（菜单 JSON，不走 push2）"

$ok = 0; $fail = 0; $firstFail = 0; $log = @()
$sw = [Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $Count; $i++) {
    $c = $codes[$i % $codes.Count]
    # 跟 EastMoneyBoardFetcher.FetchMembersAsync 用的 URL 形态完全一致
    $u = "https://push2.eastmoney.com/api/qt/clist/get?pn=1&pz=100&po=0&np=1&fltt=2&invt=2" +
         "&fid=f12&fs=b:$c&fields=f12,f14"
    try {
        $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 15 `
                -Headers @{ Referer = "https://quote.eastmoney.com/" }
        $d = ($r.Content | ConvertFrom-Json).data
        if ($d) { $ok++; $log += "." }
        else { $fail++; if (-not $firstFail) { $firstFail = $i + 1 }; $log += "0" }   # 合法JSON但data空＝限流
    }
    catch { $fail++; if (-not $firstFail) { $firstFail = $i + 1 }; $log += "X" }      # 断连＝限流
    Start-Sleep -Milliseconds $DelayMs
}

$sw.Stop()
Write-Host ""
Write-Host "push2 裸 HttpClient 连发 $Count 个（间隔 ${DelayMs}ms，共 $([int]$sw.Elapsed.TotalSeconds) 秒）"
Write-Host "成功 $ok / 失败 $fail；首次失败在第 $firstFail 个（0=没失败过）"
Write-Host "序列（.=成功 0=空data X=断连）："
Write-Host ($log -join '')
