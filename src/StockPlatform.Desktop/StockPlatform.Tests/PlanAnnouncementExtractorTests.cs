using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 抽取器的判据（见 doc/watch-item-design.md §6.7）。
///
/// **所有正文样本都是 2026-09-11 从东财公告接口取的真实原文**，一字未改——
/// 包括格力那份里 PDF 转文本留下的空格（`495,600 股`、`2026 年 8 月 17 日`）。
/// 自己编的样本测不出这类污染，而这类污染的后果是"只对一部分公司生效"，
/// 失败的那些看起来就像"这家没在回购"。
/// </summary>
public class PlanAnnouncementExtractorTests
{
    // ── 真实样本 ──

    /// <summary>宁德时代 300750，2026-09-03《关于回购公司A股股份的进展公告》。</summary>
    private const string CatlNotStarted = """
        根据《上市公司股份回购规则》《深圳证券交易所上市公司自律监管指引第9号——回购股份》等相关规定，
        公司应当在回购期间每个月的前三个交易日内披露截至上月末的回购进展情况。现将公司股份回购进展情况公告如下：
        一、回购公司股份的进展情况
        截至2026年8月31日，公司尚未实施股份回购。
        二、其他事项
        公司后续将根据市场情况在回购期限内继续实施本次回购计划。
        """;

    /// <summary>美的集团 000333，2026-09-03《…回购A股股份进展情况的公告》。</summary>
    private const string MideaProgress = """
        截至2026年8月31日，公司通过回购专用证券账户，以集中竞价交易方式累计回购公司A股股份数量为
        99,797,967股，占公司目前总股本的1.31%，最高成交价为87.71元/股，最低成交价为73.66元/股，
        支付的总金额为8,019,722,850元（不含交易费用），本次回购符合相关法律法规的要求，
        符合公司既定的回购方案。
        """;

    /// <summary>格力电器 000651，2026-08-19《…首次回购股份暨回购股份进展的公告》。⚠ 数字里带空格，原样保留。</summary>
    private const string GreeFirstBuy = """
        公司于 2026 年 8 月 17 日至 2026 年 8 月 18 日以集中竞价方式累计回购股份数量为 495,600 股，
        占公司目前总股本的 0.0088%，最高成交价为 40.10 元/股，最低成交价为 39.70 元/股，
        支付的总金额为 19,736,208.00 元（不含交易费用）。
        """;

    /// <summary>宁德时代 300750，2026-07-25《关于回购公司股份方案的公告暨回购股份报告书》。</summary>
    private const string CatlProposal = """
        同意公司使用不低于人民币200亿元（含本数）且不超过人民币400亿元（含本数）自有或自筹资金
        以集中竞价交易方式回购部分A股股份，回购股份将用于注销并减少公司注册资本，
        回购期限为自公司股东会审议通过本次回购方案之日起12个月内。
        回购价格不高于公司董事会审议通过回购股份决议前30个交易日公司股票交易均价的150%，
        回购价格上限为573元/股。
        """;

    /// <summary>
    /// 江波龙 301308，2026-09-03。★ 实机跑全市场才暴露的变体：「尚未**开始实施**」——
    /// 比"尚未实施"中间多一个词，原正则漏掉，于是这条会被记成"没抽到"而不是"没买"。
    /// </summary>
    private const string JiangNotStarted = "截至 2026 年 8 月 31 日，公司尚未开始实施股份回购。";

    /// <summary>
    /// 扬帆新材 300637，2026-09-03。★ 实机跑全市场才暴露的变体，一句话里三处跟样本不同：
    /// 没有「累计…数量为」、用「成交总金额」而非「支付的总金额」、金额前带「人民币」。
    /// </summary>
    private const string YangfanProgress = """
        截至2026年8月31日，公司通过股份回购专用证券账户以集中竞价交易方式回购公司股份206,000股，
        占公司总股本的0.09%，最高成交价为9.23元/股，最低成交价为8.88元/股，
        成交总金额为人民币1,863,178元（不含印花税、交易佣金等交易费用）。
        """;

    // ── 标题筛选 ──

    /// <summary>
    /// **本项唯一会产生错误结论（而非漏数据）的坑**：这类公告标题含"回购"但内容是股东名册。
    /// 不排除的话会解析出一条各字段全空的"进展"，看起来像回购停滞。宁德和格力都发过。
    /// </summary>
    [Theory]
    [InlineData("宁德时代:关于回购股份事项前十名股东和前十名无限售条件股东持股情况的公告")]
    [InlineData("格力电器:关于回购股份事项前十名股东和前十名无限售条件股东持股情况的公告")]
    public void 股东名册类公告_不当成回购进展(string title)
        => Assert.False(PlanAnnouncementExtractor.IsBuybackAnnouncement(title));

    /// <summary>
    /// ★ 实机跑全市场误收到的真实标题（2026-09-11）。它们都带"回购"两个字，但实质无关：
    /// 限制性股票回购注销是**股权激励收回员工股份**，跟"公司拿钱从二级市场买自己股票"
    /// 是两码事，金额量级也差几个数量级。
    ///
    /// 混进来的后果不是"多几条垃圾"，是**各字段全空的「进展」在库里看起来像回购停滞**，
    /// 还会让 GetOpenPlans 把根本没在回购的公司判成"方案进行中"、平白挂上 A 档观察项。
    /// </summary>
    [Theory]
    [InlineData("运达股份：关于部分限制性股票回购注销完成的公告")]
    [InlineData("隆扬电子：关于回购股份集中竞价减持计划的公告")]
    [InlineData("安集科技：关于2024年限制性股票激励计划第一个归属期部分归属结果公告（回购股份）")]
    [InlineData("丽珠集团：H股公告：翌日披露报表-回购股份")]
    [InlineData("鸿泉物联：关于注销回购股份并减少注册资本暨通知债权人的公告")]
    [InlineData("京东方Ａ:关于收到《贷款承诺函》暨获得回购公司股份融资支持的自愿性信息披露公告")]
    public void 带回购二字但实质无关的公告_要排除(string title)
        => Assert.False(PlanAnnouncementExtractor.IsBuybackAnnouncement(title));

    /// <summary>
    /// ★ 认不出明确 stage 的一律不收（2026-09-11 加的第三条判据）。
    /// 原来 ClassifyStage 认不出就兜底当「进展」，于是这些全落进进展类、各字段抽不到，
    /// **在库里看起来像回购停滞**，还把进展类的抽取率从真实水平拉到 37%。
    /// 真正的进展公告标题里一定有"进展/首次回购/方案/报告书/完毕/达总股本"之一。
    /// </summary>
    [Theory]
    [InlineData("瑞玛精密：关于权益投资被回购的公告")]
    [InlineData("奥克股份：关于子公司江苏奥克拟与江苏扬州化学工业园区达成部分土地有偿回购交易的公告")]
    [InlineData("华大基因：关于控股股东提议公司回购股份的公告")]
    [InlineData("康希通信：关于实际控制人、董事长、总经理提议公司回购股份的公告")]
    public void 认不出明确stage的_不收(string title)
        => Assert.False(PlanAnnouncementExtractor.IsBuybackAnnouncement(title));

    /// <summary>
    /// ★ 融资租赁的「回购担保」是对客户的担保义务，跟公司回购自家股票毫无关系
    /// （2026-09-11 实机误收）。它标题同时含"回购"和"进展"，穿过了前面所有判据，
    /// 落库后表现为一条各字段全空的「进展」——界面上就是一行读不出金额的回购记录。
    /// </summary>
    [Theory]
    [InlineData("杭叉集团：杭叉集团股份有限公司关于公司为客户提供融资租赁业务回购担保的进展公告")]
    [InlineData("某公司：关于为经销商融资租赁提供回购担保额度的公告")]
    public void 融资租赁回购担保_不是股份回购(string title)
        => Assert.False(PlanAnnouncementExtractor.IsBuybackAnnouncement(title));

    /// <summary>
    /// ★ 语序陷阱（2026-09-11 实机踩出来）：三环集团那份方案先写资金总额、紧接着写价格上限，
    /// 两者都用「不超过…元」，只差一个「/股」后缀。
    ///
    /// 没有 <c>(?!/股)</c> 断言时，正则在全文找第一个"不超过…元"会抓到 **135**（价格），
    /// 当成资金上限，而 135 后面没有"亿"，显示时就成了「计划 4.5~0 亿」。
    /// </summary>
    [Fact]
    public void 方案资金区间_不能把每股价格当成资金上限()
    {
        const string body = """
            公司拟使用自有资金以集中竞价交易方式回购公司股份，回购资金总额不低于人民币 4.5 亿元
            且不超过人民币 9 亿元。本次回购的价格不超过人民币 135 元/股（含），
            该回购价格上限不超过董事会通过回购股份决议前 30 个交易日公司股票交易均价的 150%。
            """;

        var r = PlanAnnouncementExtractor.Extract("300408", "三环集团",
            "关于2026年第二期回购公司股份方案的公告暨回购股份报告书", new DateTime(2026, 7, 20), body);

        Assert.Equal(4.5e8, r.PlanAmountLow);
        Assert.Equal(9e8, r.PlanAmountHigh);    // ← 不是 135
        Assert.Equal(135, r.PlanCapPrice);      // 价格上限该归到这里
    }

    [Theory]
    [InlineData("宁德时代:关于回购公司A股股份的进展公告")]
    [InlineData("美的集团:关于以集中竞价交易方式回购A股股份进展情况的公告")]
    [InlineData("格力电器:关于以集中竞价方式首次回购股份暨回购股份进展的公告")]
    [InlineData("熵基科技：关于首次回购公司股份暨回购股份进展的公告")]
    [InlineData("国电南瑞：关于以集中竞价交易方式首次回购公司股份的公告")]
    [InlineData("扬帆新材：关于股份回购进展的公告")]
    public void 真回购公告_认得出来(string title)
        => Assert.True(PlanAnnouncementExtractor.IsBuybackAnnouncement(title));

    [Fact]
    public void 不含回购二字的_直接排除()
        => Assert.False(PlanAnnouncementExtractor.IsBuybackAnnouncement("宁德时代:2026年半年度报告"));

    // ── stage 分类 ──

    [Theory]
    [InlineData("宁德时代:关于回购公司股份方案的公告暨回购股份报告书", PlanStage.Proposal)]
    [InlineData("格力电器:关于以集中竞价方式首次回购股份暨回购股份进展的公告", PlanStage.FirstBuy)]
    [InlineData("美的集团:关于以集中竞价交易方式回购A股股份达总股本1%的进展公告", PlanStage.Milestone)]
    [InlineData("宁德时代:关于回购公司A股股份的进展公告", PlanStage.Progress)]
    [InlineData("某公司:关于回购股份实施完毕的公告", PlanStage.Done)]
    [InlineData("某公司:关于终止回购公司股份的公告", PlanStage.Terminated)]
    public void 标题定stage(string title, string expected)
        => Assert.Equal(expected, PlanAnnouncementExtractor.ClassifyStage(title));

    /// <summary>格力那份标题同时含「首次回购」和「进展」——必须判成首次回购，那才是要等的信号。</summary>
    [Fact]
    public void 首次回购优先于进展()
        => Assert.Equal(PlanStage.FirstBuy, PlanAnnouncementExtractor.ClassifyStage(
            "格力电器:关于以集中竞价方式首次回购股份暨回购股份进展的公告"));

    // ── 正文抽取 ──

    /// <summary>★ 「尚未实施」要记成 0，不是 null——这正是"到底买没买"的答案本身。</summary>
    [Fact]
    public void 宁德未实施_记0而不是null()
    {
        var r = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司A股股份的进展公告", new DateTime(2026, 9, 3), CatlNotStarted);

        Assert.Equal(PlanStage.Progress, r.Stage);
        Assert.Equal(new DateTime(2026, 8, 31), r.AsOfDate);   // 截止日≠公告日
        Assert.Equal(0, r.CumAmount);
        Assert.Equal(0, r.CumShares);
        Assert.NotEqual(r.AnnounceDate.Date, r.AsOfDate!.Value.Date);
    }

    [Fact]
    public void 美的已实施_五个数都抽得出()
    {
        var r = PlanAnnouncementExtractor.Extract("000333", "美的集团",
            "关于以集中竞价交易方式回购A股股份进展情况的公告", new DateTime(2026, 9, 3), MideaProgress);

        Assert.Equal(new DateTime(2026, 8, 31), r.AsOfDate);
        Assert.Equal(99_797_967, r.CumShares);
        Assert.Equal(8_019_722_850, r.CumAmount);
        Assert.Equal(1.31, r.PctOfCapital);
        Assert.Equal(87.71, r.PriceHigh);
        Assert.Equal(73.66, r.PriceLow);
    }

    /// <summary>★ 格力那份数字里有空格（PDF 转文本的产物）。不容忍空格就会静默抽不到。</summary>
    [Fact]
    public void 格力首次回购_数字里带空格照样抽得出()
    {
        var r = PlanAnnouncementExtractor.Extract("000651", "格力电器",
            "关于以集中竞价方式首次回购股份暨回购股份进展的公告", new DateTime(2026, 8, 19), GreeFirstBuy);

        Assert.Equal(PlanStage.FirstBuy, r.Stage);
        Assert.Equal(495_600, r.CumShares);
        Assert.Equal(19_736_208.00, r.CumAmount);
        Assert.Equal(0.0088, r.PctOfCapital);
        Assert.Equal(40.10, r.PriceHigh);
        Assert.Equal(39.70, r.PriceLow);
        // 没有"截至X日"，取区间末 2026-08-18
        Assert.Equal(new DateTime(2026, 8, 18), r.AsOfDate);
    }

    [Fact]
    public void 宁德方案_抽得出价格上限和资金区间()
    {
        var r = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司股份方案的公告暨回购股份报告书", new DateTime(2026, 7, 25), CatlProposal);

        Assert.Equal(PlanStage.Proposal, r.Stage);
        Assert.Equal(573, r.PlanCapPrice);
        Assert.Equal(200e8, r.PlanAmountLow);
        Assert.Equal(400e8, r.PlanAmountHigh);
    }

    /// <summary>
    /// ★ 方案公告**不该有** as_of_date（2026-09-11 修）。
    ///
    /// 「数据截止日」只对进展类有意义（截至上月末累计回购了多少）；方案公告里出现的日期
    /// 是别的语境——股本基准日、董事会决议日、前 30 个交易日均价的起算日…
    /// 硬抽的后果是拿不相干的日期当"值所属日期"：实机上宁德 07-25 那份被抽成 06-30、
    /// 山东高速 08-26 那份被抽成 03-31，观察项的触发记录就按这个错日期归档了
    /// （日报里出现"2026-03-31 山东高速 回购方案进行中"这种读不懂的行）。
    /// </summary>
    [Fact]
    public void 方案公告_不抽as_of_date()
    {
        // 正文里**确实有**「截至…日」，但那是股本基准日，不是回购数据的截止日
        const string body = """
            截至2026年6月30日，公司总股本为4,626,651,000股。
            同意公司使用不低于人民币200亿元且不超过人民币400亿元自有资金回购部分A股股份，
            回购价格上限为573元/股。
            """;

        var r = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司股份方案的公告暨回购股份报告书", new DateTime(2026, 7, 25), body);

        Assert.Equal(PlanStage.Proposal, r.Stage);
        Assert.Null(r.AsOfDate);              // ← 不拿股本基准日冒充
        Assert.Equal(573, r.PlanCapPrice);    // 该抽的照样抽
    }

    /// <summary>取不到正文是软失败：标题那部分照样要留下来，不能整条丢掉。</summary>
    [Fact]
    public void 没有正文_也要留下标题和stage()
    {
        var r = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司A股股份的进展公告", new DateTime(2026, 9, 3), null);

        Assert.Equal(PlanStage.Progress, r.Stage);
        Assert.Null(r.CumAmount);      // null≠0：没抽到，不是"没买"
        Assert.Null(r.AsOfDate);
    }

    /// <summary>
    /// ★ 实机回归：三家样本归纳的正则跑全市场只抽到 17%（18/103）。
    /// 这两条是放宽后补上的真实变体——**三家的措辞不是通例**，这个教训比这两条断言本身更重要。
    /// </summary>
    [Fact]
    public void 江波龙_尚未开始实施_也要记成0()
    {
        var r = PlanAnnouncementExtractor.Extract("301308", "江波龙",
            "关于回购公司股份的进展公告", new DateTime(2026, 9, 3), JiangNotStarted);

        Assert.Equal(0, r.CumAmount);
        Assert.Equal(new DateTime(2026, 8, 31), r.AsOfDate);
    }

    [Fact]
    public void 扬帆新材_无累计数量为句式_成交总金额_也抽得出()
    {
        var r = PlanAnnouncementExtractor.Extract("300637", "扬帆新材",
            "关于股份回购进展的公告", new DateTime(2026, 9, 3), YangfanProgress);

        Assert.Equal(206_000, r.CumShares);
        Assert.Equal(1_863_178, r.CumAmount);
        Assert.Equal(0.09, r.PctOfCapital);
        Assert.Equal(9.23, r.PriceHigh);
        Assert.Equal(8.88, r.PriceLow);
        Assert.Equal(new DateTime(2026, 8, 31), r.AsOfDate);
    }

    /// <summary>
    /// 九洲药业 603456，2026-06-12。★ 一句话里三处变体：**万股/万元单位**、
    /// 「成交的最高价」（跟"最高成交价"词序相反）、「已支付的」。
    ///
    /// **单位是这里面最危险的一条**：漏掉「万」不是抽不到，是抽出一个小 10000 倍、
    /// 但看起来完全正常的数——"回购了 1524 元"没人会察觉那是解析错了。
    /// </summary>
    private const string JiuzhouFirstBuy = """
        2026 年 6 月 12 日，公司通过集中竞价交易方式首次回购股份 123.25 万股，
        累计占公司总股本的比例为 0.14%，成交的最高价为 12.71 元/股，最低价为 12.12元/股，
        已支付的总金额为 1,524.73 万元（不含交易费用）。
        """;

    [Fact]
    public void 九洲药业_万股万元单位_要换算成股和元()
    {
        var r = PlanAnnouncementExtractor.Extract("603456", "九洲药业",
            "关于以集中竞价交易方式首次回购股份暨回购股份比例达1%的进展公告",
            new DateTime(2026, 6, 12), JiuzhouFirstBuy);

        Assert.Equal(1_232_500, r.CumShares);      // 123.25 万股
        Assert.Equal(15_247_300, r.CumAmount);     // 1,524.73 万元
        Assert.Equal(0.14, r.PctOfCapital);
        Assert.Equal(12.71, r.PriceHigh);          // 「成交的最高价为」
        Assert.Equal(12.12, r.PriceLow);           // 「最低价为」
        // 单日首次回购没有「截至X日」也没有区间，靠「X日，公司通过…」这条兜
        Assert.Equal(new DateTime(2026, 6, 12), r.AsOfDate);
    }

    /// <summary>
    /// ★ 大北农 002385，2026-06-16。就差一个「为」字：
    /// 「成交总金额 3,033,800.00 元」直接跟数字，而原正则写的是 <c>总金额为</c>（必需）。
    /// 这一个字让 **437 条（进展类的 33%）** 金额抽不到——而那些公告股数、占比、成交价
    /// 全都抽到了，唯独金额空着，在界面上看起来像"没买"。
    /// </summary>
    [Fact]
    public void 成交总金额后面不跟为字_也要抽得出()
    {
        const string body = """
            2026 年 6 月 15 日，公司首次通过股份回购专用证券账户以集中竞价方式实施回购股份，
            回购股份数量 1,010,000 股，占公司总股本的 0.02%，最高成交价为 3.01 元/股，
            最低成交价为 3.00 元/股，成交总金额 3,033,800.00 元（不含交易费用）。
            """;

        var r = PlanAnnouncementExtractor.Extract("002385", "大北农",
            "关于以集中竞价交易方式首次回购公司股份的公告", new DateTime(2026, 6, 16), body);

        Assert.Equal(3_033_800.00, r.CumAmount);
        Assert.Equal(1_010_000, r.CumShares);
        Assert.Equal(0.02, r.PctOfCapital);
    }

    /// <summary>
    /// ★ 雷柏科技 002577，2026-07-02：「**暂未**通过回购专用证券账户…回购公司股份」。
    /// 原正则只认「尚未」，于是这条既没抽到金额、也没判成未实施，落库是一片 null——
    /// **把一个明确的答案（没买）变成了看似的数据故障**。这是这一类里代价最大的漏判。
    /// </summary>
    [Fact]
    public void 暂未回购_也要记成0()
    {
        const string body = """
            二、股份回购实施进展
            截至2026年6月30日，公司暂未通过回购专用证券账户以集中竞价交易方式回购公司股份。
            """;

        var r = PlanAnnouncementExtractor.Extract("002577", "雷柏科技",
            "关于回购公司股份的进展公告", new DateTime(2026, 7, 2), body);

        Assert.Equal(0, r.CumAmount);
        Assert.Equal(0, r.CumShares);
        Assert.Equal(new DateTime(2026, 6, 30), r.AsOfDate);
    }

    /// <summary>放宽「尚未…回购」之后不能误伤：同一句里没有"回购"的否定句不算。</summary>
    [Fact]
    public void 未实施的判据_不跨句误伤()
    {
        // "尚未收到" 和 "回购" 分属两句，不该被判成未实施
        const string body = """
            截至2026年8月31日，公司尚未收到相关批复。
            公司通过回购专用证券账户累计回购股份数量为 500,000 股，成交总金额 1,000,000 元。
            """;

        var r = PlanAnnouncementExtractor.Extract("000001", "测试",
            "关于回购公司股份的进展公告", new DateTime(2026, 9, 3), body);

        Assert.Equal(500_000, r.CumShares);        // 正常抽到，没被当成"未实施"
        Assert.Equal(1_000_000, r.CumAmount);
    }

    /// <summary>★ 0 和 null 必须分得开，否则"抽取坏了"会显示成"公司没买"。</summary>
    [Fact]
    public void 零和null语义不同()
    {
        var notStarted = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司A股股份的进展公告", new DateTime(2026, 9, 3), CatlNotStarted);
        var noContent = PlanAnnouncementExtractor.Extract("300750", "宁德时代",
            "关于回购公司A股股份的进展公告", new DateTime(2026, 9, 3), null);

        Assert.Equal(0, notStarted.CumAmount);
        Assert.Null(noContent.CumAmount);
    }
}
