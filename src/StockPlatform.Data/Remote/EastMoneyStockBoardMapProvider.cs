using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 个股的**行业归属**和**题材归属**（东财 <c>RPT_F10_CORETHEME_BOARDTYPE</c>，走 datacenter）。
///
/// 一次抓取产出两样东西，因为它们本来就在同一张报表里（靠 <c>BOARD_TYPE</c> 区分）：
///   · <b>行业</b>（BOARD_TYPE='行业'）→ <see cref="StockIndustryEm"/>，东财三级分类；
///   · <b>题材</b>（其余）→ <see cref="StockThemeEm"/>，带入选理由和精确匹配标记。
///
/// 为什么要它——现有的证监会分类粒度太粗，实测：33 门类 + 84 大类，但 <b>1867 只（32.5%）
/// 大类为空只能退回门类</b>，而"制造业"一个门类装了 3596 只（占 62%）。拿这个做行业中性化
/// 等于没中性化。东财是三级（一级31/二级128/三级337），最大的三级行业也才 627 只。
///
/// <b>这个 provider 不碰 push2</b>，这是刻意的：push2 要人工在浏览器过一道反爬验证才放行，
/// 而且验证有时效、过一阵又会失效，不适合无人值守的日常抓取。datacenter 一直稳定可达。
///
/// 那 F10 报表会漏股的问题呢？——那是**成分股场景**的问题（要的是"这个板块完整有哪些股"，
/// 漏一只结论就错，见 <see cref="EastMoneyBoardFetcher"/> 里那个液冷服务器漏 4 只的实测）。
/// 这里是**个股属性场景**（"这只股属于哪个行业"），正是这张报表的本职用途；覆盖 5644/5753 只，
/// 剩下那 109 只退回证监会分类即可——所以 <c>StockIndustry</c> 那张老表要留着做兜底，不删。
/// </summary>
/// <summary>
/// 一轮抓取的结果（2026-09-22）。
///
/// 加这个 record 是为了让任务能判断**该不该清孤儿票**：
/// <see cref="IStockBoardMapRepository.PurgeOlderThan"/> 只在"整轮抓完且对账通过"时才能调，
/// 而原来 provider 只返回落库行数、对账结果仅仅报进日志——任务无从判断，只能一律清或一律不清。
/// </summary>
/// <param name="Industry">落库的行业行数。</param>
/// <param name="Theme">落库的题材行数。</param>
/// <param name="Rows">这一轮实际收到的总行数。</param>
/// <param name="Reported">接口自报的总行数（拿不到是 0）。</param>
public sealed record StockBoardMapFetchOutcome(int Industry, int Theme, int Rows, int Reported)
{
    /// <summary>
    /// 这一轮是不是**取全了**。判据跟 provider 里那句告警同一条（差 1% 以内不算，
    /// 抓的过程中接口那侧数据可能有微调）；接口没自报总数时只能认它取全了。
    /// </summary>
    public bool Complete => Reported <= 0 || Rows >= Reported * 0.99;
}

public class EastMoneyStockBoardMapProvider
{
    private const string Report = "RPT_F10_CORETHEME_BOARDTYPE";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyStockBoardMapProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 全市场一次拉全（约 9.4 万行、188 页）。这张表没有时间维度，是**当下快照**，
    /// 所以没法做增量、每次都是全量重取；好在行业和题材归属变动都很慢，季度跑一次就够。
    ///
    /// 边抓边回调落库：9.4 万行攒内存没必要，而且中途断了已落库的部分仍然有效。
    ///
    /// ⚠ **一批绝不切开一只票**（2026-09-22）：落库那侧是「按票整只替换」（先删这只票的旧行、
    /// 再插新的，见 <see cref="IStockBoardMapRepository.ReplaceForStocks"/>），一只票的行被切成
    /// 两批的话，第二批会把第一批刚写进去的行当成旧行删掉。所以攒够行数之后要等到
    /// <c>SECURITY_CODE</c> 换下一只才提交——同一只票的行必然连续，这一点正好由下面那个
    /// 排序键保证（它本来是为了翻页不重不漏才加的）。
    /// </summary>
    /// <param name="onBatch">每积累一批就回调（行业, 题材），返回写入行数。</param>
    /// <param name="fetchedAt">
    /// 这一轮的落盘时刻，**由调用方给**：整轮抓完之后它要拿这个时刻去清「本轮一行都没出现」
    /// 的孤儿票（<see cref="IStockBoardMapRepository.PurgeOlderThan"/>），两边必须是同一个值。
    /// </param>
    public async Task<StockBoardMapFetchOutcome> FetchAsync(
        Func<List<StockIndustryEm>, List<StockThemeEm>, (int, int)> onBatch,
        DateTime fetchedAt,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var inds = new List<StockIndustryEm>(4000);
        var themes = new List<StockThemeEm>(4000);
        int totalInd = 0, totalTheme = 0, rows = 0;
        int reported = 0;             // 接口自报的总行数，抓完拿来对账
        var now = fetchedAt;
        string? batchCode = null;     // 这一批攒到哪只票了——批边界只能落在它变化的地方

        progress?.Report("个股行业/题材归属：全量重取（约 9.4 万行、188 页）...");

        // ⚠ 排序键**必须**是 SECURITY_CODE + BOARD_CODE 两列。
        // 一只股票在这张表里有十几行，光按 SECURITY_CODE 排的话同一只股票内部的先后不定，
        // 翻页时同键行会既重复又丢失——实测前 3 页 1500 行里重了 15 行（＝也丢了 15 行），
        // 补上 BOARD_CODE 之后为 0。这是这个项目在龙虎榜、板块上都踩过的同一个坑。
        await foreach (var el in _dc.QueryAsync(Report, sortColumns: "SECURITY_CODE,BOARD_CODE",
                                                descending: false,
                                                onTotalCount: n => reported = n, ct: ct))
        {
            var code = Str(el, "SECURITY_CODE");
            // NEW_BOARD_CODE 才是 BKxxxx 形式，BOARD_CODE 是去掉 BK 的裸号
            var board = Str(el, "NEW_BOARD_CODE");
            if (code.Length == 0 || board.Length == 0) continue;
            rows++;

            // 批边界：攒够了、而且**下一只票开始了**才提交。判据和它的理由在
            // StockBoardMapBatchRule——那条规则是正确性前提（切开一只票会让下一批的整只替换
            // 删掉刚写进去的行），所以它在 Logic 层、有用例钉着，不留在这儿当一句内联条件。
            // ⚠ 必须判在把这一行加进批**之前**。
            if (StockBoardMapBatchRule.ShouldFlush(inds.Count + themes.Count, batchCode, code))
            {
                var (a, b) = onBatch(inds, themes);
                totalInd += a; totalTheme += b;
                inds.Clear(); themes.Clear();
                progress?.Report($"个股行业/题材：已处理 {rows} 行（行业 {totalInd}、题材 {totalTheme}）");
            }
            batchCode = code;

            if (Str(el, "BOARD_TYPE") == "行业")
            {
                inds.Add(new StockIndustryEm
                {
                    Code = code,
                    BoardCode = board,
                    BoardName = Str(el, "BOARD_NAME"),
                    BoardLevel = (int?)Num(el, "BOARD_LEVEL"),
                    FetchedAt = now,
                });
            }
            else
            {
                themes.Add(new StockThemeEm
                {
                    Code = code,
                    BoardCode = board,
                    BoardName = Str(el, "BOARD_NAME"),
                    IsPrecise = EastMoneyJson.Bool(el, "IS_PRECISE"),
                    BoardRank = (int?)Num(el, "BOARD_RANK"),
                    Reason = Str(el, "SELECTED_BOARD_REASON"),
                    FetchedAt = now,
                });
            }
        }
        if (inds.Count + themes.Count > 0)
        {
            var (a, b) = onBatch(inds, themes);
            totalInd += a; totalTheme += b;
        }

        if (rows == 0)
            throw new RateLimitedException(
                "东财个股行业/题材归属返回 0 行——接口可能改版或被限流，本轮不更新。");

        // 对账：跟接口自报的总行数比。少了就是中途悄悄漏了行（翻页不稳、某页被限流返回空），
        // 这种"少一点"最危险——不报错，但行业分类会缺几只票，一路带进后面所有按行业的统计里。
        // 差 1% 以内不算（抓的过程中接口那侧数据可能有微调），超过就明着说，让人能看见。
        if (reported > 0 && rows < reported * 0.99)
            progress?.Report(
                $"⚠ 个股行业/题材只取到 {rows} 行，接口自报 {reported} 行——缺了 {reported - rows} 行。" +
                "已落库的部分有效，但覆盖不全，建议重跑一次（这张表是全量重取，重跑无副作用）。");

        progress?.Report(
            $"个股行业/题材完成：共 {rows} 行"
            + (reported > 0 ? $"（接口自报 {reported} 行）" : "")
            + $"，行业 {totalInd} 条、题材 {totalTheme} 条。");
        return new StockBoardMapFetchOutcome(totalInd, totalTheme, rows, reported);
    }

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double? Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                                                    CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null,
        };
    }
}
