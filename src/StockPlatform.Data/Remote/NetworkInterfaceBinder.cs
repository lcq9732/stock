using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 把某一类请求**钉在指定网卡上出去**（2026-09-04）。
///
/// 起因：本机有线接的是公司网，网关按域名把 <c>push2.eastmoney.com</c> 拦了；无线接的是
/// 另一个没限制的网络。两条链路的出口是不同的运营商出口（实测有线 183.34.226.239 江门、
/// 无线 183.12.216.44 深圳），所以只要让 push2 的连接从无线那块网卡发出去就能通。
///
/// 做法是给 <see cref="SocketsHttpHandler.ConnectCallback"/> 换成自己建 socket、先
/// <c>Bind</c> 到那块网卡的本地 IP 再 connect。Windows 从 Vista 起默认强主机模型，
/// 绑定了源地址就会从对应网卡出去——上面那两个不同的出口 IP 就是这么实测出来的。
///
/// 两个刻意的设计：
///   · **按网卡名配置，不配 IP**。无线是 DHCP，换个网络地址就变了；手机开热点电脑连上去，
///     用的还是同一块 "Wi-Fi" 网卡，配置不用动。（USB 线共享手机网络会多出一块 RNDIS 网卡，
///     所以界面上要能列出所有网卡随便挑，不能写死。）
///   · **每次建连时重新解析**当前 IP，不是启动时解析一次。中途换网络、热点重连导致 IP 变了，
///     下一个请求自动用新的，不用重启程序。
/// </summary>
public static class NetworkInterfaceBinder
{
    /// <summary>
    /// 当前能用的网卡（已启用、非回环、有 IPv4）。给界面列选项用。
    /// </summary>
    public static List<(string Name, string Description, string Ipv4)> ListUsable()
    {
        var list = new List<(string, string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var ip = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (ip == null) continue;
            list.Add((ni.Name, ni.Description, ip.ToString()));
        }
        return list;
    }

    /// <summary>
    /// 网卡名 → 当前 IPv4。先精确匹配 <c>Name</c>，再退回包含匹配（<c>Name</c> 或 <c>Description</c>），
    /// 这样配 "Wi-Fi" 能命中 "Wi-Fi 2" 这种系统自动改过名的情况。找不到返回 null。
    /// </summary>
    public static IPAddress? ResolveIPv4(string? nameOrKeyword)
    {
        if (string.IsNullOrWhiteSpace(nameOrKeyword)) return null;
        var key = nameOrKeyword.Trim();

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var exact = candidates.FirstOrDefault(
            ni => string.Equals(ni.Name, key, StringComparison.OrdinalIgnoreCase));
        var pick = exact ?? candidates.FirstOrDefault(
            ni => ni.Name.Contains(key, StringComparison.OrdinalIgnoreCase)
               || ni.Description.Contains(key, StringComparison.OrdinalIgnoreCase));

        return pick?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
    }

    /// <summary>
    /// 描述当前的绑定状态，给日志用。**必须在订阅了 OnStatus 之后再调**——
    /// 原来这段话是在构造函数里发的，那时候还没人订阅，消息直接丢了，
    /// 结果配了网卡也看不出有没有生效（2026-09-04 踩过）。
    ///
    /// 除了报网卡和 IP，还会检查一件事：**这块网卡跟默认路由是不是同一个网关**。
    /// 实测踩过——Wi-Fi 连回了公司网，拿到 172.16.20.29、网关跟有线的 172.16.19.253 一模一样，
    /// 绑过去等于没绑，出的还是同一个门、照样被拦。这种情况光看"绑定成功"会以为配好了。
    /// </summary>
    public static string Describe(string? nameOrKeyword)
    {
        // 没配＝走默认路由，这是常态，日志里说它等于什么都没说（还得解释一句没配哪个键）。
        // 返回空串让调用方整行跳过；真配了网卡才有信息量，那才是执行时的事实。
        if (string.IsNullOrWhiteSpace(nameOrKeyword)) return "";

        var ip = ResolveIPv4(nameOrKeyword);
        if (ip == null)
            return $"⚠ 配置指定 push2 走网卡「{nameOrKeyword}」，但现在找不到这块网卡（没插/没连/名字不对），" +
                   "会退回默认路由。当前可用：" +
                   string.Join("、", ListUsable().Select(x => $"{x.Name}({x.Ipv4})"));

        var msg = $"push2 走指定网卡「{nameOrKeyword}」（当前 {ip}）。";

        var gw = GatewayOf(nameOrKeyword);
        if (gw != null)
        {
            // 别的网卡里有没有人用着同一个网关——有的话说明它们其实在同一个网络
            var same = ListUsable()
                .Where(x => !x.Name.Equals(ResolvedName(nameOrKeyword), StringComparison.OrdinalIgnoreCase))
                .Where(x => gw.Equals(GatewayOf(x.Name)))
                .Select(x => x.Name).ToList();
            if (same.Count > 0)
                msg += $"⚠ 但它的网关（{gw}）跟【{string.Join("、", same)}】是同一个——" +
                       "说明这几块网卡其实连的是同一个网络，绑过去出的还是同一个门，多半照样被拦。" +
                       "要换的是这块网卡连的**网络**（换 Wi-Fi 的 SSID 或开手机热点），不是换网卡。";
        }
        return msg;
    }

    private static string? ResolvedName(string key)
    {
        var c = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
        return (c.FirstOrDefault(ni => string.Equals(ni.Name, key, StringComparison.OrdinalIgnoreCase))
             ?? c.FirstOrDefault(ni => ni.Name.Contains(key, StringComparison.OrdinalIgnoreCase)
                                    || ni.Description.Contains(key, StringComparison.OrdinalIgnoreCase)))?.Name;
    }

    private static string? GatewayOf(string nameOrKeyword)
    {
        var name = ResolvedName(nameOrKeyword);
        if (name == null) return null;
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return ni?.GetIPProperties().GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                              && !g.Address.Equals(IPAddress.Any))?.Address.ToString();
    }

    /// <summary>
    /// 建一个把出站连接钉在指定网卡上的 handler。
    /// <paramref name="nameOrKeyword"/> 为空就返回普通 handler（＝保持现在的行为，走默认路由）。
    ///
    /// <paramref name="onStatus"/> 会在**网卡找不到**时被调用一次：这种情况下请求仍然照发
    /// （从默认路由走），但必须让人看见——静默退回默认网卡的话，用户会以为配了就生效了，
    /// 实际还是从被拦的那条链路出去，然后对着"又失败了"找不到原因。
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? nameOrKeyword, Action<string>? onStatus = null)
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;

        var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            // 连接别一直留着：换网络之后旧连接是绑在旧网卡/旧 IP 上的，留着会继续用坏的那条路。
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        if (string.IsNullOrWhiteSpace(nameOrKeyword)) return handler;

        bool warned = false;
        handler.ConnectCallback = async (ctx, ct) =>
        {
            // 每次都重新解析：换网络/热点重连之后 IP 会变，不能用启动时那个
            var local = ResolveIPv4(nameOrKeyword);
            if (local == null && !warned)
            {
                warned = true;
                onStatus?.Invoke(
                    $"⚠ 找不到网卡「{nameOrKeyword}」（没插/没连/名字不对），本次请求改走默认网卡。" +
                    "当前可用：" + string.Join("、", ListUsable().Select(x => $"{x.Name}({x.Ipv4})")));
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                if (local != null) socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return handler;
    }
}
