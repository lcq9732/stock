using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 「哪些票是金融机构、各是哪一类」——把这个判定从
/// <c>FetchOrchestrator.RunFetchBankRegulatoryAsync</c> 里抽出来（2026-09-15）。**纯计算，不碰 IO。**
///
/// ════ 为什么要抽 ════
/// 两个地方都要它，而且用途完全不同：
///   · 【金融监管指标】—— 决定去查谁的年报中报、用哪一类 parser
///   · <see cref="OwnsPdfOf"/> —— 决定**哪些 PDF 归它管、可以删**
///
/// 第二条以前是 <c>ReparseCachedBankReports</c> 里的一段内联条件，没名字、测不到，
/// 而它管着 <c>File.Delete</c>。已经出过事：为子公司解析下载的非金融年报放进同一个目录后，
/// <c>LooksLikeReport</c>（判据是前 3 页有没有年报结构关键词，为拦截问询函而写）对它们一律
/// 返回 false —— 非金融年报前几页是封面和图片 —— 于是当成"下错的文件"删掉，删了 2 份
/// （002594 比亚迪、600998 九州通）。
///
/// ⚠ 两个目录合并成 <c>reports/</c> 之后，<see cref="OwnsPdfOf"/> 是**唯一防线**。
///   有用例钉着它，谁想简化掉先看那组测试。
/// </summary>
public static class FinancialInstitutionRoster
{
    /// <summary>【金融监管指标】认这三类；其余一律不碰。</summary>
    public static bool IsCovered(FinancialInstitutionKind kind)
        => kind is FinancialInstitutionKind.Bank
                or FinancialInstitutionKind.Broker
                or FinancialInstitutionKind.Insurer;

    /// <summary>
    /// 这只票的 PDF 归【金融监管指标】管吗——**删文件之前必须问这一句**。
    ///
    /// <b>没有财务快照 = 不归它管</b>。这一条是故意的：删文件的判据必须收紧到
    /// "我确定这是我该管的文件"，而不是"我没认出来所以多半是垃圾"。新股、刚上市、
    /// 财务还没抓到的票都会落在这里，它们的 PDF 要原样留着。
    /// </summary>
    public static bool OwnsPdfOf(string code, IReadOnlyDictionary<string, FinancialSnapshot> latest)
        => latest.TryGetValue(code, out var snap)
           && IsCovered(BankHealthCheckBuilder.ClassifyInstitution(snap));

    /// <summary>
    /// 全市场里的金融机构名单，(代码, 类型)，按代码排序——结果必须跟输入顺序无关，否则不可复现。
    /// 只返回 <see cref="IsCovered"/> 认的三类。
    /// </summary>
    public static IReadOnlyList<(string Code, FinancialInstitutionKind Kind)> Classify(
        IReadOnlyDictionary<string, FinancialSnapshot> latest)
        => latest
            .Select(kv => (kv.Key, Kind: BankHealthCheckBuilder.ClassifyInstitution(kv.Value)))
            .Where(x => IsCovered(x.Kind))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 科目集版本落后、需要先补抓财务的金融机构。
    ///
    /// ════ 鸡生蛋 ════
    /// 没抓到 v4 科目之前认不出谁是券商谁是保险（已赚保费/代理买卖证券净收入是 v4 才加的）。
    /// 所以这里用**老数据也判得出**的特征先粗筛：银行/券商/保险的利润表都没有"营业成本"。
    /// 金融机构总共一百来只，全抓一遍也就几分钟。
    ///
    /// ⚠ 判据必须是**科目集版本号**，不能是"某个科目在不在"。踩过的坑：原来写的是
    ///   "缺 interest_net 就补抓"，可库里有 562 只已经抓到 v3（有 interest_net、没有 v4 的
    ///   已赚保费），于是券商和保险全部被跳过、**永远识别不出来**。
    /// </summary>
    /// <param name="keysVersionOf">代码 → 已抓到的科目集版本；取不到的当 0（从没抓过）。</param>
    public static IReadOnlyList<string> NeedFinancialRefetch(
        IReadOnlyDictionary<string, FinancialSnapshot> latest,
        Func<string, int> keysVersionOf,
        int currentKeysVersion)
        => latest
            .Where(kv => kv.Value.Get(FinancialKeys.OperCost) is null or 0)   // 粗筛：金融机构
            .Where(kv => keysVersionOf(kv.Key) < currentKeysVersion)          // 科目集落后
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
}
