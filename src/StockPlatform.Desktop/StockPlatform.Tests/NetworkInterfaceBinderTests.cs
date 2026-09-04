using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 把 push2 的请求钉在指定网卡上（2026-09-04）。
///
/// 背景：本机有线接的是公司网、网关按域名把 push2 拦了（TCP/TLS 都通，一发 HTTP 就被切断），
/// 换一条没限制的链路就能通。这里守的是配置出错时的行为——**找不到网卡不能抛异常**，
/// 否则抓取整个起不来；应该退回默认路由并且把话说明白。
/// </summary>
public class NetworkInterfaceBinderTests
{
    private readonly ITestOutputHelper _out;
    public NetworkInterfaceBinderTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void 列出的网卡都带IPv4()
    {
        var list = NetworkInterfaceBinder.ListUsable();
        foreach (var (name, desc, ip) in list)
        {
            _out.WriteLine($"{name}  {ip}  ({desc})");
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(System.Net.IPAddress.TryParse(ip, out _), $"{name} 的 IP 解析不出来：{ip}");
        }
    }

    [Fact]
    public void 按网卡名能解析出当前IP()
    {
        var list = NetworkInterfaceBinder.ListUsable();
        if (list.Count == 0) { _out.WriteLine("这台机器没有可用网卡，跳过"); return; }

        var first = list[0];
        var ip = NetworkInterfaceBinder.ResolveIPv4(first.Name);
        Assert.NotNull(ip);
        Assert.Equal(first.Ipv4, ip!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 没配网卡时解析结果为空(string? key)
        => Assert.Null(NetworkInterfaceBinder.ResolveIPv4(key));

    [Fact]
    public void 网卡名不存在时返回空而不是抛异常()
        => Assert.Null(NetworkInterfaceBinder.ResolveIPv4("根本没有这块网卡"));

    [Fact]
    public void 建handler不抛异常_不管配没配网卡()
    {
        // 配错了也必须能起来：抓取整个流程不能因为一个网卡名写错就起不来
        using var a = NetworkInterfaceBinder.CreateHandler(null);
        using var b = NetworkInterfaceBinder.CreateHandler("根本没有这块网卡");
        using var c = NetworkInterfaceBinder.CreateHandler("Wi-Fi");
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(c);
    }

    [Fact]
    public void 网卡找不到时会把话说明白()
    {
        // 静默退回默认网卡是最坏的结果——用户以为配了就生效，实际还从被拦的那条链路出去，
        // 然后对着"又失败了"找不到原因。所以必须有这条提示。
        var msgs = new List<string>();
        using var h = NetworkInterfaceBinder.CreateHandler("根本没有这块网卡", msgs.Add);
        using var client = new HttpClient(h) { Timeout = TimeSpan.FromMilliseconds(300) };
        try { client.GetAsync("http://127.0.0.1:9/never").GetAwaiter().GetResult(); } catch { }

        Assert.NotEmpty(msgs);
        Assert.Contains(msgs, m => m.Contains("找不到网卡"));
        _out.WriteLine(msgs[0]);
    }

    [Fact]
    public void 没配网卡时说的是走默认路由()
        => Assert.Contains("默认路由", NetworkInterfaceBinder.Describe(null));

    [Fact]
    public void 网卡不存在时说明会退回默认路由并列出可选的()
    {
        var d = NetworkInterfaceBinder.Describe("根本没有这块网卡");
        Assert.Contains("找不到这块网卡", d);
        Assert.Contains("当前可用", d);
        _out.WriteLine(d);
    }

    [Fact]
    public void 网卡跟别人共用网关时要警告绑了也白绑()
    {
        // 2026-09-04 踩的坑：Wi-Fi 连回了公司网，拿到 172.16.20.29、网关跟有线的
        // 172.16.19.253 一模一样。绑过去等于没绑——出的还是同一个门，照样被拦。
        // 光说"绑定成功"会让人以为配好了，然后对着"还是不通"找不到原因。
        var list = NetworkInterfaceBinder.ListUsable();
        if (list.Count == 0) { _out.WriteLine("没有可用网卡，跳过"); return; }

        foreach (var (name, _, _) in list)
        {
            var d = NetworkInterfaceBinder.Describe(name);
            _out.WriteLine(d);
            Assert.Contains(name, d);
            // 有警告的话必须把话说到底：告诉人该换的是网络不是网卡
            if (d.Contains("同一个网关"))
                Assert.Contains("不是换网卡", d);
        }
    }
}
