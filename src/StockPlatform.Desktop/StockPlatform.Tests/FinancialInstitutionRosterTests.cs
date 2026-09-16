using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 金融机构名单，以及**哪些 PDF 归【金融监管指标】管**（2026-09-15）。
///
/// ════ 这一组存在的唯一理由 ════
/// <see cref="FinancialInstitutionRoster.OwnsPdfOf"/> 管着 <c>File.Delete</c>。
/// 它以前是一段没名字的内联条件，而且已经出过事——为子公司解析下载的非金融年报被当成
/// "下错的文件"删掉了 2 份（002594 比亚迪、600998 九州通）。
///
/// 两个 PDF 目录合并之后，它是**唯一防线**。谁想简化掉这个判断，先看这组用例。
/// </summary>
public class FinancialInstitutionRosterTests
{
    private readonly ITestOutputHelper _out;
    public FinancialInstitutionRosterTests(ITestOutputHelper output) => _out = output;

    private static FinancialSnapshot Snap(params (string Key, double Value)[] items)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, v) in items) d[k] = v;
        return new FinancialSnapshot { ReportDate = new DateTime(2025, 12, 31), Values = d };
    }

    /// <summary>工商企业：有"营业成本"。银行/券商/保险的利润表里没有这一行。</summary>
    private static FinancialSnapshot NonFinancial() => Snap(
        (FinancialKeys.Revenue, 100e8), (FinancialKeys.OperCost, 75e8));

    /// <summary>银行：没有营业成本，利息净收入占营收 ≥40%。</summary>
    private static FinancialSnapshot Bank() => Snap(
        (FinancialKeys.Revenue, 100e8), (FinancialKeys.InterestNet, 62e8));

    /// <summary>券商：有代理买卖证券净收入。利息净收入占比低（实测中信 3.5%）。</summary>
    private static FinancialSnapshot Broker() => Snap(
        (FinancialKeys.Revenue, 100e8), (FinancialKeys.InterestNet, 3.5e8),
        (FinancialKeys.BrokerageNet, 20e8));

    /// <summary>保险：有已赚保费。</summary>
    private static FinancialSnapshot Insurer() => Snap(
        (FinancialKeys.Revenue, 100e8), (FinancialKeys.PremiumEarned, 80e8));

    /// <summary>
    /// ⚠ **最重要的一条**：非金融股的 PDF 不归它管，一个字节都不许动。
    /// 002594 / 600998 就是这么被删掉的。
    /// </summary>
    [Fact]
    public void 非金融股的PDF不归它管()
    {
        var latest = new Dictionary<string, FinancialSnapshot>
        {
            ["002594"] = NonFinancial(),      // 比亚迪
            ["600998"] = NonFinancial(),      // 九州通
            ["601939"] = Bank(),              // 建设银行
        };

        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("002594", latest));
        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("600998", latest));
        Assert.True(FinancialInstitutionRoster.OwnsPdfOf("601939", latest));
    }

    /// <summary>
    /// ⚠ **没有财务快照 = 不归它管**，这是故意的。
    ///
    /// 删文件的判据必须收紧到"我确定这是我该管的"，而不是"我没认出来所以多半是垃圾"。
    /// 新股、刚上市、财务还没抓到的票都会落在这里——它们的 PDF 要原样留着。
    /// </summary>
    [Fact]
    public void 认不出来的一律不碰()
    {
        var latest = new Dictionary<string, FinancialSnapshot> { ["601939"] = Bank() };

        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("300750", latest));   // 库里没有
        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("", latest));
        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("不是代码", latest));
    }

    /// <summary>
    /// 科目集老旧、判不出具体类型的金融股（<c>OtherFinancial</c>）也不归它管。
    /// 安全降级：宁可少体检一家，也不能把券商当银行体检、更不能拿它的判断去删文件。
    /// </summary>
    [Fact]
    public void 判不出具体类型的金融股也不碰()
    {
        // 没有营业成本（是金融机构），但利息净收入占比不够、也没有券商/保险的特征科目
        var vague = Snap((FinancialKeys.Revenue, 100e8), (FinancialKeys.InterestNet, 5e8));
        var latest = new Dictionary<string, FinancialSnapshot> { ["600000"] = vague };

        Assert.Equal(FinancialInstitutionKind.OtherFinancial,
                     BankHealthCheckBuilder.ClassifyInstitution(vague));
        Assert.False(FinancialInstitutionRoster.OwnsPdfOf("600000", latest));
    }

    [Fact]
    public void 三类金融机构都认()
    {
        var latest = new Dictionary<string, FinancialSnapshot>
        {
            ["601939"] = Bank(), ["600030"] = Broker(),
            ["601318"] = Insurer(), ["002594"] = NonFinancial(),
        };

        var roster = FinancialInstitutionRoster.Classify(latest);
        foreach (var (c, k) in roster) _out.WriteLine($"{c} {k}");

        Assert.Equal(3, roster.Count);                       // 非金融被排除
        Assert.Equal(["600030", "601318", "601939"], roster.Select(x => x.Code));  // 按代码排序
        Assert.Equal(FinancialInstitutionKind.Broker, roster[0].Kind);
        Assert.Equal(FinancialInstitutionKind.Insurer, roster[1].Kind);
        Assert.Equal(FinancialInstitutionKind.Bank, roster[2].Kind);
    }

    /// <summary>
    /// ⚠ 补抓判据必须看**科目集版本号**，不能看"某个科目在不在"。
    ///
    /// 踩过的坑：原来写"缺 interest_net 就补抓"，而库里有 562 只已经抓到 v3
    /// （有 interest_net、没有 v4 才加的已赚保费），于是券商和保险全部被跳过、永远识别不出来。
    /// </summary>
    [Fact]
    public void 补抓判据看版本号_不看科目在不在()
    {
        var latest = new Dictionary<string, FinancialSnapshot>
        {
            ["601939"] = Bank(),              // 金融，v3（落后）
            ["600030"] = Broker(),            // 金融，v4（已最新）
            ["002594"] = NonFinancial(),      // 非金融，版本再旧也不管
        };
        var ver = new Dictionary<string, int> { ["601939"] = 3, ["600030"] = 4, ["002594"] = 1 };

        var need = FinancialInstitutionRoster.NeedFinancialRefetch(
            latest, c => ver.GetValueOrDefault(c), currentKeysVersion: 4);

        _out.WriteLine(string.Join(", ", need));
        Assert.Equal(["601939"], need);
    }

    /// <summary>从没抓过的（版本取不到，当 0）也要补。</summary>
    [Fact]
    public void 从没抓过的也要补()
    {
        var latest = new Dictionary<string, FinancialSnapshot> { ["601939"] = Bank() };
        var need = FinancialInstitutionRoster.NeedFinancialRefetch(
            latest, _ => 0, currentKeysVersion: 4);
        Assert.Equal(["601939"], need);
    }
}
