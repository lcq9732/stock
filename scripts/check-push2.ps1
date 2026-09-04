<#
.SYNOPSIS
  逐块网卡检查东财各域名能不能通，并报出每条链路的出口公网 IP。

.DESCRIPTION
  用来回答一个具体问题：**现在这台机器该让 push2 走哪块网卡**。

  背景（2026-09-04 实测）：有线接的公司网在网关上按域名把 push2.eastmoney.com 拦了——
  TCP 和 TLS 握手都能成，一发出 HTTP 请求就被切断，收到 0 字节；而同为东财的
  datacenter-web 一直正常。所以这不是东财在限流，是本地网络。换一条没限制的链路
  （另一个 Wi-Fi、或手机热点）就能通。

  出口公网 IP 那一列是用来确认"真的换了网络"的：两块网卡如果报同一个出口 IP，
  说明无线其实连回了同一个受限网络（实测踩过：Wi-Fi 拿到 172.16.20.29，跟有线同网段）。

  测出哪块网卡能通 push2 之后，写进 publish/data/fetcher-settings.json：
      { "Push2NetworkInterface": "Wi-Fi" }
  只有 push2（板块列表和成分股）会走它，其余任务照旧走默认路由。
#>

$ErrorActionPreference = 'Continue'

function Test-Endpoint {
    param([string]$SourceIp, [string]$HostName, [int]$Port, [bool]$Tls, [string]$Path)

    try   { $dst = [System.Net.Dns]::GetHostAddresses($HostName) |
                   Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1 }
    catch { return 'DNS失败' }
    if (-not $dst) { return 'DNS无A记录' }

    $sock = New-Object System.Net.Sockets.Socket 'InterNetwork','Stream','Tcp'
    try {
        $sock.Bind((New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($SourceIp)), 0))
        $iar = $sock.BeginConnect($dst, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(6000)) { return '连不上(超时)' }
        $sock.EndConnect($iar)

        $net = New-Object System.Net.Sockets.NetworkStream $sock, $false
        if ($Tls) {
            $ssl = New-Object System.Net.Security.SslStream $net, $false
            $ssl.AuthenticateAsClient($HostName)
            $stream = $ssl
        } else { $stream = $net }
        $stream.ReadTimeout = 6000

        $req = "GET $Path HTTP/1.1`r`nHost: $HostName`r`nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36`r`nAccept: */*`r`nReferer: https://quote.eastmoney.com/`r`nConnection: close`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($req)
        $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()

        $buf = New-Object byte[] 8192
        $sb = New-Object Text.StringBuilder
        try {
            while ($sb.Length -lt 6000) {
                $n = $stream.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $n))
            }
        } catch { }
        $body = $sb.ToString()
        if ($body.Length -eq 0) { return '✗ 0字节(被切断)' }
        return $body
    }
    catch  { return "✗ $($_.Exception.GetType().Name)" }
    finally { try { $sock.Close() } catch { } }
}

$nics = Get-NetIPConfiguration |
    Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address -and $_.InterfaceAlias -notlike 'vEthernet*' } |
    ForEach-Object { [pscustomobject]@{ Name = $_.InterfaceAlias; Ip = $_.IPv4Address.IPAddress;
                                        Gw = ($_.IPv4DefaultGateway.NextHop -join ',') } }

if (-not $nics) { Write-Host '没找到可用网卡。'; exit 1 }

Write-Host ''
Write-Host '网卡                 本地IP           网关             出口公网IP        push2          datacenter'
Write-Host ('-' * 108)

foreach ($n in $nics) {
    # 出口公网 IP：确认这块网卡走的是不是另一条链路
    $out = Test-Endpoint $n.Ip 'cip.cc' 80 $false '/'
    $pub = '?'
    if ($out -match 'IP\s*:\s*([0-9\.]+)') { $pub = $Matches[1] }
    if ($out -match '地址\s*:\s*(.+)')     { $pub = "$pub $($Matches[1].Trim().Split("`n")[0])" }

    $p2 = Test-Endpoint $n.Ip 'push2.eastmoney.com' 443 $true '/api/qt/clist/get?fid=f12&po=1&pz=5&pn=1&np=1&fltt=2&invt=2&fs=b%3ABK1137&fields=f12%2Cf14'
    $p2s = if ($p2 -match '"diff"|"total"') { '✓ 通' } elseif ($p2 -like '✗*') { $p2 } else { '✗ 无数据' }

    $dc = Test-Endpoint $n.Ip 'datacenter-web.eastmoney.com' 443 $true '/api/data/v1/get?reportName=RPT_F10_CORETHEME_BOARDTYPE&columns=ALL&pageNumber=1&pageSize=1&sortColumns=SECURITY_CODE&sortTypes=1'
    $dcs = if ($dc -match '"data"') { '✓ 通' } elseif ($dc -like '✗*') { $dc } else { '✗ 无数据' }

    '{0,-20} {1,-16} {2,-16} {3,-17} {4,-14} {5}' -f $n.Name, $n.Ip, $n.Gw, $pub, $p2s, $dcs | Write-Host
}

Write-Host ''
Write-Host '说明：'
Write-Host '  · 两块网卡如果网关或出口公网IP相同，说明它们其实连的是同一个网络，绑过去出的还是'
Write-Host '    同一个门、照样被拦。这时候要换的是这块网卡连的**网络**（换 Wi-Fi 的 SSID、或开手机'
Write-Host '    热点），不是换网卡。'
Write-Host '  · 找到 push2 那列是「✓ 通」的网卡，把它的名字写进 publish/data/fetcher-settings.json：'
Write-Host '        { "Push2NetworkInterface": "Wi-Fi" }'
Write-Host '    只有 push2（板块列表和成分股）走它，其余任务仍走默认路由。'
Write-Host ''
