namespace StockPlatform.Logic.Services;

/// <summary>
/// 决定"这一轮探到的空响应，哪些可以落成永久水位"（<c>BarProbeFloor</c> 表）。纯计算、无 IO，
/// 单独抽出来就是为了让下面那两道安全前提能被单测钉住——记错一条的后果比多抓一轮严重得多：
/// 水位记高了会让这只票的历史**永久跳过**，而且不会有任何报错。
/// </summary>
public static class ProbeFloorPlanner
{
    /// <summary>
    /// <paramref name="emptyProbes"/> 是本轮"请求成功、但返回 0 行"的 (代码, 请求终点)。
    /// 返回该写库的水位 (代码, 这天之前没数据)。
    ///
    /// 两道前提，缺一不可：
    /// <list type="number">
    /// <item><b>本地必须已有这只票的K线</b>。本地一根都没有时返回空，可能是代码不存在、也可能是
    /// 接口抽风——没有旁证，不该下"数据源没有"的结论；而且这种票下一轮也只花 1 个请求，不值得冒险。</item>
    /// <item><b>请求终点必须早于本地最早一根</b>。这是"往前补缺口"的特征。日常增量抓的是
    /// [水位线, 今天]，终点是今天——周末/节假日跑一次同样会返回空（见 ProcessOneStockAsync 里
    /// 那个 FetchedButEmpty 分支的注释），要是也记了水位，就会把水位抬到明天、这只票的全部历史
    /// 从此永久跳过。这道前提就是专门挡它的。</item>
    /// </list>
    /// 水位取 <c>min(请求终点+1天, 本地最早一根)</c>：探到空只证明"终点及其之前那段没有"，
    /// 越过本地最早一根就不是这次探测能支持的结论了。
    /// </summary>
    public static List<(string Code, DateTime NoDataBefore)> Plan(
        IEnumerable<(string Code, DateTime End)> emptyProbes,
        IReadOnlyDictionary<string, DateTime> earliestByCode)
    {
        // 同一只票本轮可能被探多次（三个粒度各自调用，或一轮里补了多段），取最靠后的终点
        var latestEnd = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (code, end) in emptyProbes)
            if (!latestEnd.TryGetValue(code, out var had) || end.Date > had) latestEnd[code] = end.Date;

        var result = new List<(string, DateTime)>();
        foreach (var (code, end) in latestEnd)
        {
            if (!earliestByCode.TryGetValue(code, out var earliest)) continue;   // 前提 1
            if (end >= earliest.Date) continue;                                  // 前提 2
            var floor = end.AddDays(1);
            if (floor > earliest.Date) floor = earliest.Date;
            result.Add((code, floor));
        }
        return result;
    }

    /// <summary>
    /// 不发一个请求，直接从**本地已有的历史**推出水位（【回填"无更早数据"水位】那一项用）。
    ///
    /// 判据只有一条，但它很强：<b>前复权/后复权/不复权三路的最早一根落在同一天</b>。
    /// 这三路是三次**独立**的抓取（各有各的水位线、各自分批补过），三次都停在同一天，
    /// 那一天就是数据源这只票的起点，不是某次抓取的水位线。
    ///
    /// 2026-09-07 在本机 23GB 库上实测这条判据：
    /// <list type="bullet">
    /// <item>5874 只个股（含 319 只退市股）里 <b>5841 只三路一致</b>（99.4%）；</item>
    /// <item>剩 33 只是 day 比另两路更早（那两路真缺前段，所以**不能**填）；</item>
    /// <item>外部交叉验证：退市股名单带两所官网的真实上市日，319 只里 313 只（98%）
    /// 本地起点 ≤ 官网上市日，剩 6 只只差几天到一年（早年数据源本身就没有）；</item>
    /// <item>个股侧最大的"同一天起点"堆积只有 31 只（2020-07-27 那批集中上市），
    /// 没有人为水位线该有的成百上千只堆积。</item>
    /// </list>
    ///
    /// <b>只有 day 一路的标的一律不填</b>（ETF、大盘指数、板块指数）：它们没有交叉印证，
    /// 而且板块指数是本地合成的、区间回补根本不抓它们。ETF 那 1655 只留给真探测——
    /// 探一遍约 20 分钟，探完 <c>BarProbeFloor</c> 自然就记住了，不必在这里冒风险。
    /// </summary>
    /// <returns>三路各自要写的水位。key 是粒度名，value 是 (代码, 这天之前没数据)。</returns>
    public static Dictionary<string, List<(string Code, DateTime NoDataBefore)>> PlanFromLocalHistory(
        IReadOnlyDictionary<string, DateTime> earliestDay,
        IReadOnlyDictionary<string, DateTime> earliestHfq,
        IReadOnlyDictionary<string, DateTime> earliestRaw,
        string granDay, string granHfq, string granRaw,
        out int agreed, out int disagreed, out int dayOnly)
    {
        agreed = disagreed = dayOnly = 0;
        var day = new List<(string, DateTime)>();
        var hfq = new List<(string, DateTime)>();
        var raw = new List<(string, DateTime)>();

        foreach (var (code, eDay) in earliestDay)
        {
            bool hasHfq = earliestHfq.TryGetValue(code, out var eHfq);
            bool hasRaw = earliestRaw.TryGetValue(code, out var eRaw);
            if (!hasHfq || !hasRaw) { dayOnly++; continue; }        // ETF/指数/板块指数：不填

            if (eDay.Date != eHfq.Date || eDay.Date != eRaw.Date) { disagreed++; continue; }

            agreed++;
            day.Add((code, eDay.Date));
            hfq.Add((code, eDay.Date));
            raw.Add((code, eDay.Date));
        }

        return new Dictionary<string, List<(string Code, DateTime NoDataBefore)>>(StringComparer.Ordinal)
        {
            [granDay] = day, [granHfq] = hfq, [granRaw] = raw,
        };
    }
}
