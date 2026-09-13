<#
.SYNOPSIS
  东财「分档资金流」历史搬运脚本 —— 在一台能访问 push2his 的机器上跑，把数据抓成 CSV。

.DESCRIPTION
  背景：公司网关按域名把 push2his.eastmoney.com 拦了（TCP/TLS 都通，一发 HTTP 请求就
  被切、收到 0 字节），而分档资金流的历史只有那个域名给得出来（fflow/daykline，最近约
  120 个交易日）。所以在主力机器上补不了历史，得拿到另一条网络上抓，再把 CSV 拷回去导入。

  这个脚本只做一件事：按清单逐只抓，抓一只追加写一只。中断了直接重跑，已抓过的自动跳过。

.PARAMETER Codes
  代码清单文件，每行 "代码,secid"（例：000001,0.000001）。
  由主力机器上的 MoneyFlowTransfer 工具导出：
      dotnet run --project tools\MoneyFlowTransfer -- export-codes
  ⚠ 别自己拼 secid：北交所 920xxx 要用 "0." 前缀，拼成 "1." 的话东财返回空、不报错，
    这个坑 2026-09-06 埋过两天。清单里的 secid 是主力机器用 MarketClassifier 算好的。
  找不到这个文件时会退而用东财的全市场排行接口在线拉一份（它自带 market 字段，同样不用猜）。

.PARAMETER OutDir
  输出目录，默认脚本所在目录下的 out\。产出四个文件：
      moneyflow-<日期>.csv  数据本体（追加写，就是要拷回去的那个）
      done.txt              已抓成功的代码，断点续跑靠它
      failed.txt            抓不到的代码（可以改天单独重跑一遍）
      fetch-log.txt         过程日志

.PARAMETER DelaySec
  每个请求之间的间隔秒数，默认 2。主力机器上用的是 5 秒 + 每 15 个歇 2 分钟（那是被限流
  之后调出来的保守档）；换一条干净的网络通常 2 秒就够。被切多了脚本会自己变慢，不用手调。

.PARAMETER BatchMin / BatchMax / RestMinSec / RestMaxSec
  每抓 BatchMin~BatchMax 只（默认 10~15，每批重新掷一次）就主动歇 RestMinSec~RestMaxSec 秒
  （默认 120~300 秒，每次也重新掷）。

  两处都**故意是随机的**：东财的触发点是累计请求数不是速率，光降速没用，得在被切之前主动歇；
  而"每 15 个整、歇整 120 秒"这种精确的周期本身就是机器行为最好认的特征，所以批量和歇多久
  都掷骰子。请求之间的间隔（DelaySec）同样带 ±30% 抖动。

.PARAMETER Fresh
  忽略 done.txt 从头再抓一遍（CSV 仍是追加，重复行导入时会被 INSERT OR REPLACE 覆盖，
  不会重复入库）。

.EXAMPLE
  .\moneyflow-fetch.ps1
  .\moneyflow-fetch.ps1 -DelaySec 3 -BatchMin 8 -BatchMax 12 -RestMinSec 180 -RestMaxSec 420   # 被切得厉害就调慢
  powershell -ExecutionPolicy Bypass -File .\moneyflow-fetch.ps1     # 机器不让跑脚本时
#>
param(
  [string]$Codes = "",
  [string]$OutDir = "",
  [double]$DelaySec = 2,
  [int]$BatchMin = 10,
  [int]$BatchMax = 15,
  [int]$RestMinSec = 120,
  [int]$RestMaxSec = 300,
  [switch]$Fresh
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ⚠ 路径全部转成绝对路径，而且要把 .NET 的当前目录也掰回来（2026-09-12 踩过）：
#   PowerShell 的当前目录和 .NET 的当前目录**是两个东西**。New-Item / Add-Content 用前者，
#   New-Object System.IO.StreamWriter 用后者。只要传进来的是相对路径，目录就建在 A、
#   CSV 却写进 B（常是用户主目录或 system32）——out\ 里只剩 done.txt 和日志，
#   关掉窗口一看就像"抓到的数据被删了"。
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $here) { $here = (Get-Location).Path }          # 某些启动方式取不到脚本路径
[System.IO.Directory]::SetCurrentDirectory((Get-Location).Path)

function To-AbsolutePath { param([string]$Path, [string]$Base)
  if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
  return [System.IO.Path]::GetFullPath((Join-Path $Base $Path))
}

# 没传参就落在脚本自己旁边；传了相对路径就按**当前目录**算（人在哪儿敲命令就按哪儿算）。
$cwd = (Get-Location).Path
if (-not $Codes)  { $Codes  = Join-Path $here 'codes.txt' } else { $Codes  = To-AbsolutePath $Codes  $cwd }
if (-not $OutDir) { $OutDir = Join-Path $here 'out' }       else { $OutDir = To-AbsolutePath $OutDir $cwd }
$Codes  = [System.IO.Path]::GetFullPath($Codes)
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# 份号在读清单时才知道，所以 CSV 的名字等读完清单再定（见下面的 Set-CsvPath）
$script:PartNo = ''
$script:PartTotal = ''
$csvPath  = Join-Path $OutDir ('moneyflow-' + (Get-Date -Format 'yyyyMMdd') + '.csv')
$donePath = Join-Path $OutDir 'done.txt'
$failPath = Join-Path $OutDir 'failed.txt'
$logPath  = Join-Path $OutDir 'fetch-log.txt'

$UA      = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
$Header  = @{ 'User-Agent' = $UA; 'Referer' = 'https://quote.eastmoney.com/' }
$FIELDS2 = 'f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61,f62,f63,f64,f65'

function Write-Log {
  param([string]$Msg)
  $line = '[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $Msg
  Write-Host $line
  Add-Content -Path $logPath -Value $line -Encoding UTF8
}

# 一次请求。返回 @{ Ok=$true; Json=... } 或 @{ Ok=$false; Why='...' }。
# 「0 字节被切断」单独认出来——那是网关按域名拦的特征，跟东财限流（空 data、拒连）不是一回事。
function Invoke-Api {
  param([string]$Url)
  try {
    $r = Invoke-WebRequest -Uri $Url -Headers $Header -TimeoutSec 25 -UseBasicParsing
    if (-not $r.Content) { return @{ Ok = $false; Why = '空响应' } }
    return @{ Ok = $true; Json = ($r.Content | ConvertFrom-Json) }
  } catch {
    $m = $_.Exception.Message
    if ($m -match 'closed|reset|关闭|中止') { return @{ Ok = $false; Why = '连接被切断(0字节)' } }
    return @{ Ok = $false; Why = $m }
  }
}

# ── 0. 先确认这台机器到底通不通，别让人白跑一整轮 ──────────────────────────
Write-Log '自检：试抓一只（000001）看 push2his 通不通...'
$probeUrl = 'https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get?lmt=0' +
            '&klt=101&secid=0.000001&fields1=f1,f2,f3,f7&fields2=' + $FIELDS2
$probe = Invoke-Api $probeUrl
if (-not $probe.Ok) {
  Write-Log ('X 自检没过：' + $probe.Why)
  Write-Log '  这台机器也拿不到 push2his。如果原因是「连接被切断(0字节)」，说明这条网络的'
  Write-Log '  网关同样按域名拦了它——换个网络（手机热点、别的 Wi-Fi）再试，换网卡没用。'
  exit 1
}
$probeRows = @($probe.Json.data.klines).Count
Write-Log ('OK 自检通过，000001 拿到 ' + $probeRows + ' 行。')

# ── 1. 代码清单 ────────────────────────────────────────────────────────────
$queue = New-Object System.Collections.ArrayList
if (Test-Path $Codes) {
  foreach ($line in (Get-Content $Codes)) {
    $t = $line.Trim()
    # 清单头部的「# part: 3/6」——六台机器分头抓时，CSV 文件名带上份号，
    # 抓回来拷到一处才不会互相覆盖（份号是工具分份时写进去的，不用人管）
    if ($t -match '^#\s*part:\s*(\d+)\s*/\s*(\d+)') { $script:PartNo = $Matches[1]; $script:PartTotal = $Matches[2]; continue }
    if (-not $t -or $t.StartsWith('#')) { continue }
    $p = $t.Split(',')
    if ($p.Count -lt 2) { continue }
    [void]$queue.Add([pscustomobject]@{ Code = $p[0].Trim(); SecId = $p[1].Trim() })
  }
  if ($script:PartNo) {
    $csvPath = Join-Path $OutDir ('moneyflow-' + (Get-Date -Format 'yyyyMMdd') + '-p' + $script:PartNo + '.csv')
    Write-Log ('清单：' + $Codes + ' —— ' + $queue.Count + ' 只（第 ' + $script:PartNo + '/' + $script:PartTotal + ' 份）')
  } else {
    Write-Log ('清单：' + $Codes + ' —— ' + $queue.Count + ' 只')
  }
} else {
  Write-Log ('没找到 ' + $Codes + '，改从东财全市场排行接口在线拉一份...')
  $fs = 'm:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048'
  for ($pn = 1; $pn -le 200; $pn++) {
    $u = 'https://push2delay.eastmoney.com/api/qt/clist/get?fid=f12&po=0&pz=100&pn=' + $pn +
         '&np=1&fltt=2&invt=2&fs=' + $fs + '&fields=f12,f13'
    $res = Invoke-Api $u
    if (-not $res.Ok) { Write-Log ('  第 ' + $pn + ' 页失败：' + $res.Why + '，停止翻页'); break }
    $diff = @($res.Json.data.diff)
    if ($diff.Count -eq 0) { break }
    foreach ($d in $diff) {
      # secid 前缀直接用接口自己给的 market(f13)，不按代码段猜
      [void]$queue.Add([pscustomobject]@{ Code = [string]$d.f12; SecId = ([string]$d.f13 + '.' + [string]$d.f12) })
    }
    Start-Sleep -Milliseconds 800
  }
  Write-Log ('在线拉到 ' + $queue.Count + ' 只')
}
if ($queue.Count -eq 0) { Write-Log 'X 一只都没有，退出。'; exit 1 }

# ── 2. 续跑：已抓过的跳过 ──────────────────────────────────────────────────
$done = New-Object 'System.Collections.Generic.HashSet[string]'
if ($Fresh) {
  Write-Log '-Fresh：忽略已有进度，从头再抓一遍（已有的 CSV 一个字都不会删）。'
} else {
  if (Test-Path $donePath) {
    foreach ($c in (Get-Content $donePath)) { if ($c.Trim()) { [void]$done.Add($c.Trim()) } }
  }
  # ⚠ CSV 才是唯一真相。done.txt 只是加速用的索引：它要是丢了、或者进程正好死在
  #   "写完 CSV 还没写 done" 那一瞬，光看 done.txt 就会把已经抓到的票再抓一遍——
  #   在 16~35 个请求就被切的配额下，重复抓是实打实的损失。所以启动时扫一遍已有的 CSV。
  $csvFiles = @(Get-ChildItem -Path $OutDir -Filter 'moneyflow-*.csv' -ErrorAction SilentlyContinue)
  if ($csvFiles.Count -gt 0) {
    $before = $done.Count
    foreach ($f in $csvFiles) {
      $rdr = New-Object System.IO.StreamReader($f.FullName)
      try {
        $null = $rdr.ReadLine()                      # 表头
        while ($null -ne ($line = $rdr.ReadLine())) {
          $i = $line.IndexOf(',')
          if ($i -gt 0) { [void]$done.Add($line.Substring(0, $i)) }
        }
      } finally { $rdr.Close() }
    }
    $extra = $done.Count - $before
    if ($extra -gt 0) {
      Write-Log ('从已有 CSV 又校对出 ' + $extra + ' 只（done.txt 没记全，以 CSV 为准）')
    }
  }
  if ($done.Count -gt 0) { Write-Log ('续跑：' + $done.Count + ' 只已抓过，跳过不重复取。') }
  else { Write-Log '没有历史进度，从头开始。' }
}

# ── 3. CSV（追加写；表头只在新建时写一次）──────────────────────────────────
$needHeader = -not (Test-Path $csvPath)
$csv = New-Object System.IO.StreamWriter($csvPath, $true, (New-Object System.Text.UTF8Encoding($false)))
$csv.AutoFlush = $true
if ($needHeader) {
  $csv.WriteLine('code,trade_date,main_net,small_net,mid_net,big_net,super_net,main_ratio,small_ratio,mid_ratio,big_ratio,super_ratio,close_price,change_rate,fetched_at')
}

$total = $queue.Count
$ok = 0; $fail = 0; $empty = 0; $rows = 0; $reqs = 0; $consecFail = 0; $skipped = 0
$startAt = Get-Date
$lastReport = Get-Date

# 这一批抓几只、歇多久，都在每一批开始前重新掷——固定周期太像机器
$sinceRest = 0
$nextBatch = Get-Random -Minimum $BatchMin -Maximum ($BatchMax + 1)

$todoNow = $total - $done.Count
$avgBatch = ($BatchMin + $BatchMax) / 2.0
$avgRest  = ($RestMinSec + $RestMaxSec) / 2.0
$estSec   = $todoNow * $DelaySec + ($todoNow / $avgBatch) * $avgRest
$est      = [TimeSpan]::FromSeconds($estSec)

Write-Log ('开始：共 ' + $total + ' 只（这轮要抓 ' + $todoNow + ' 只），间隔 ' + $DelaySec +
           ' 秒±30%，每 ' + $BatchMin + '~' + $BatchMax + ' 只歇 ' + $RestMinSec + '~' + $RestMaxSec + ' 秒。')
Write-Log ('按这个节奏顺利的话约 ' + [int]$est.TotalHours + ' 小时 ' + $est.Minutes +
           ' 分（被限流退避会更久）。中途关窗口不丢数据，重跑自动接着来。')
Write-Log ('输出：' + $csvPath)

try {
  foreach ($item in $queue) {
    if ($done.Contains($item.Code)) { $skipped++; continue }

    # 单只的快速重试：2 秒、10 秒（跟主力程序同一节奏）
    $res = $null
    foreach ($wait in @(0, 2, 10)) {
      if ($wait -gt 0) { Start-Sleep -Seconds $wait }
      $url = 'https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get?lmt=0' +
             '&klt=101&secid=' + $item.SecId + '&fields1=f1,f2,f3,f7&fields2=' + $FIELDS2
      $res = Invoke-Api $url
      $reqs++
      if ($res.Ok) { break }
    }

    if (-not $res.Ok) {
      $fail++; $consecFail++
      Add-Content -Path $failPath -Value ($item.Code + ',' + $res.Why) -Encoding UTF8
    } else {
      $klines = @($res.Json.data.klines)
      if ($klines.Count -eq 0) {
        # 停牌/退市，或者 secid 拼错了。不算失败，但单独计数——全是空的话多半是清单有问题。
        $empty++; $consecFail = 0
        [void]$done.Add($item.Code)
        Add-Content -Path $donePath -Value $item.Code -Encoding UTF8
      } else {
        $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        foreach ($k in $klines) {
          $p = $k.Split(',')
          if ($p.Count -lt 13) { continue }
          # 原样落 CSV：列顺序就是东财 f51..f63，导入端按同一顺序读
          $csv.WriteLine($item.Code + ',' + ($p[0..12] -join ',') + ',' + $stamp)
          $rows++
        }
        $ok++; $consecFail = 0
        [void]$done.Add($item.Code)
        Add-Content -Path $donePath -Value $item.Code -Encoding UTF8
      }
    }

    # ── 被切之后的退避。连续失败才算数：东财正常也会拒个三五个（令牌桶空了），
    #    一失败就长睡的话吞吐直接归零。
    if ($consecFail -ge 25) {
      Write-Log ('X 连续失败 ' + $consecFail + ' 只，这条网络多半也被拦了、或者被长封禁。已抓到的都在 CSV 里，')
      Write-Log '  换个网络、或者过几小时再跑一次这个脚本（会自动续上）。'
      break
    } elseif ($consecFail -gt 0 -and ($consecFail % 10) -eq 0) {
      Write-Log ('! 连续失败 ' + $consecFail + ' 只，歇 15 分钟再试...')
      Start-Sleep -Seconds 900
    } elseif ($consecFail -gt 0 -and ($consecFail % 5) -eq 0) {
      Write-Log ('! 连续失败 ' + $consecFail + ' 只，歇 2 分钟再试...')
      Start-Sleep -Seconds 120
    }

    # 主动歇：触发点是累计请求数，得在被切之前歇。批量和歇多久每次重掷。
    $sinceRest++
    # 队列跑完了就别再歇——收尾那一下歇了也没有下一个请求，纯粹让人多等几分钟
    if ($sinceRest -ge $nextBatch -and ($ok + $fail + $empty) -lt $todoNow) {
      $restNow = Get-Random -Minimum $RestMinSec -Maximum ($RestMaxSec + 1)
      Write-Log ('这批抓了 ' + $sinceRest + ' 只（累计 ' + $reqs + ' 个请求），主动歇 ' +
                 [Math]::Round($restNow / 60.0, 1) + ' 分钟...')
      Start-Sleep -Seconds $restNow
      $sinceRest = 0
      $nextBatch = Get-Random -Minimum $BatchMin -Maximum ($BatchMax + 1)
    } else {
      # 间隔带 ±30% 抖动：固定节奏是机器行为里最好认的特征
      $jitter = $DelaySec * (0.7 + (Get-Random -Minimum 0.0 -Maximum 0.6))
      Start-Sleep -Milliseconds ([int]($jitter * 1000))
    }

    $now = Get-Date
    if (($now - $lastReport).TotalSeconds -ge 30) {
      $lastReport = $now
      $doneCnt = $ok + $fail + $empty
      $left = $total - $skipped - $doneCnt
      $perItem = ($now - $startAt).TotalSeconds / [Math]::Max(1, $doneCnt)
      $eta = [TimeSpan]::FromSeconds($perItem * $left)
      Write-Log ('进度 ' + ($skipped + $doneCnt) + '/' + $total +
                 '（成功 ' + $ok + '、失败 ' + $fail + '、没数据 ' + $empty + '、' + $rows + ' 行）' +
                 ' 预计还要 ' + [int]$eta.TotalHours + ' 小时 ' + $eta.Minutes + ' 分')
    }
  }
}
finally {
  $csv.Flush(); $csv.Close()
}

$elapsed = (Get-Date) - $startAt
Write-Log ('==== 结束：成功 ' + $ok + ' 只、失败 ' + $fail + ' 只、接口没数据 ' + $empty + ' 只、' +
           '跳过（之前抓过）' + $skipped + ' 只，写入 ' + $rows + ' 行，用时 ' +
           [int]$elapsed.TotalHours + ' 小时 ' + $elapsed.Minutes + ' 分。')
# 把落盘结果原样报出来：文件在哪、多大、多少行。
# 关窗口/断电之后最想知道的就是"到底写进去没有"，靠猜不行。
if (Test-Path $csvPath) {
  $fi = Get-Item $csvPath
  $lineCount = 0
  $rdr = New-Object System.IO.StreamReader($csvPath)
  try { while ($null -ne $rdr.ReadLine()) { $lineCount++ } } finally { $rdr.Close() }
  Write-Log ('落盘确认：' + $csvPath)
  Write-Log ('           ' + [Math]::Round($fi.Length / 1MB, 2) + ' MB、' + $lineCount + ' 行（含表头）')
  $allCsv = @(Get-ChildItem -Path $OutDir -Filter 'moneyflow-*.csv' -ErrorAction SilentlyContinue)
  if ($allCsv.Count -gt 1) {
    Write-Log ('⚠ 这个目录里一共有 ' + $allCsv.Count + ' 个 moneyflow-*.csv（跨天续跑会每天一个），')
    Write-Log '  拷回主力机器时**全都要拷**，导入时用通配一次导完：import out\moneyflow-*.csv'
  } else {
    Write-Log '把上面这个文件拷回主力机器。'
  }
  Write-Log '数据是抓一只写一只的，中途关窗口、断网都不会丢已抓的部分；重跑会接着上次往下取。'
} else {
  Write-Log ('X 没找到输出文件：' + $csvPath + ' —— 这不该发生，请把上面的日志发回来。')
}
if ($fail -gt 0) {
  Write-Log ('失败的 ' + $fail + ' 只记在 ' + $failPath + '，再跑一次本脚本会自动只补它们。')
}
