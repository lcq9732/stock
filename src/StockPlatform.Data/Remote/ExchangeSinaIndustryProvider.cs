using System.Text.RegularExpressions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 全市场证监会行业分类，**两所门类 + 新浪大类**（2026-08-04 新增，2026-09-10 抽出
/// <see cref="IndustryProviderBase"/> 后只剩新浪那一段）：
/// - **门类**（19 类，覆盖沪深全部）：来自基类的两所官网接口。
/// - **大类**（84 类，约覆盖 58%）：新浪 <c>newFLJK.php?param=industry</c> 给行业清单
///   （hangye_ZAxx），再逐个用 <c>Market_Center.getHQNodeData?node=hangye_xxx</c> 取成分股。
///
/// 为什么不用申万：新浪的申万节点(sw2_xxxxxx)虽然能取成分股，但拿不到"所有申万节点"的清单，
/// 只能靠枚举代码猜，不可靠。
///
/// ⚠⚠ <b>2026-09-10 实机验证发现这条路的大类是【整组错位】的，已停用，不要切回来</b>。
/// 同日两条路各跑一遍全市场、逐票比对（3482 只共有票），**557 只（16%）大类不同**，
/// 而且是成批错、错得离谱——抽样一看就知道谁对：
///
/// | 本类给的 | 东财给的 | 抽样 | 只数 |
/// |---|---|---|---|
/// | 金属制品、机械和设备修理业 | 化学原料和化学制品制造业 | 湖北宜化/新金路/红太阳/安道麦/渝三峡 | 114 |
/// | 黑色金属冶炼和压延加工业 | 造纸和纸制品业 | ST晨鸣/美利云/凯恩股份/太阳纸业 | 23 |
/// | 石油加工、炼焦和核燃料加工业 | 家具制造业 | 索菲亚/喜临门/永艺/曲美家居 | 7 |
/// | 铁路、船舶、航空航天和其他运输设备制造业 | 文教、工美、体育和娱乐用品制造业 | 奥飞娱乐/珠江钢琴/海伦钢琴 | 7 |
///
/// 病根应该在 <see cref="FetchSinaMajorAsync"/>：节点清单是拿正则从 newFLJK.php 里
/// 一行行抠出来的 (node, name) 配对，一旦某个节点的字段错位，整个节点的成分股就会被贴上
/// **别人的行业名**——而且不会报任何错。<b>这个错从 2026-08-04 建表起就一直在库里。</b>
///
/// 所以本类现在的定位是**留档**（东财整体不可用时的最后手段，用之前必须先修错位），
/// 不再是"随时可切的退路"。默认见 <see cref="ExchangeEastMoneyIndustryProvider"/>，
/// 比对详情见 doc/industry-source-eastmoney-design.md。
/// </summary>
public class ExchangeSinaIndustryProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    : IndustryProviderBase(rateLimiter, httpClient)
{
    public override string SourceName => IndustrySources.Sina;

    /// <summary>新浪只补大类；它没收录的票保持"只有门类"（<see cref="StockIndustry.Best"/> 会退回门类）。</summary>
    protected override async Task ApplyDetailAsync(Dictionary<string, StockIndustry> map, CancellationToken ct)
    {
        int majorHit = 0;
        foreach (var (code, major) in await FetchSinaMajorAsync(ct))
        {
            if (map.TryGetValue(code, out var row)) row.MajorName = major;
            else map[code] = new StockIndustry { Code = code, MajorName = major };
            majorHit++;
        }
        Status($"新浪证监会大类：{majorHit} 只有细分行业；合计 {map.Count} 只");
    }

    // ── 新浪：先取84个大类清单，再逐个取成分股 ──
    private async Task<List<(string Code, string Major)>> FetchSinaMajorAsync(CancellationToken ct)
    {
        var result = new List<(string, string)>();
        var listTxt = await Limiter.RunAsync(
            () => GetStringAsync("https://vip.stock.finance.sina.com.cn/q/view/newFLJK.php?param=industry",
                                 "https://finance.sina.com.cn/", gbk: true, ct), ct);
        // 形如 "hangye_ZA01":"hangye_ZA01,农业,15,..."
        var nodes = Regex.Matches(listTxt, @"""(hangye_\w+)"":""hangye_\w+,([^,]+),(\d+),")
            .Select(m => (Node: m.Groups[1].Value, Name: m.Groups[2].Value, Count: int.Parse(m.Groups[3].Value)))
            .Where(x => x.Count > 0)
            .ToList();
        Status($"证监会大类行业 {nodes.Count} 个，开始逐个取成分股...");

        int done = 0;
        foreach (var (node, name, count) in nodes)
        {
            for (int page = 1; page <= (count / 80) + 1; page++)
            {
                var url = "https://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                          $"?page={page}&num=80&sort=symbol&asc=1&node={node}";
                string txt;
                try
                {
                    txt = await Limiter.RunAsync(() => GetStringAsync(url, "https://finance.sina.com.cn/", gbk: false, ct), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { break; } // 单个行业取不到不影响整体，它会退回门类
                if (string.IsNullOrWhiteSpace(txt) || txt.TrimStart().StartsWith("null")) break;
                List<string> codes;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(txt);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) break;
                    codes = doc.RootElement.EnumerateArray().Select(x => Str(x, "code")).Where(x => x.Length == 6).ToList();
                }
                catch { break; }
                if (codes.Count == 0) break;
                foreach (var c in codes) result.Add((c, name));
                if (codes.Count < 80) break;
            }
            if (++done % 20 == 0) Status($"证监会大类进度 {done}/{nodes.Count}");
        }
        return result;
    }
}
