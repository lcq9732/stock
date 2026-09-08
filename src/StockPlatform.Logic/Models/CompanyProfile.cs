namespace StockPlatform.Logic.Models;

/// <summary>
/// 公司档案（2026-09-08，东财 <c>RPT_HSF9_BASIC_ORGINFO</c>）。每股一行的快照。
///
/// 直接用途是给客户/供应商做**实体消歧**：年报里写的是"福建时代星云科技有限公司"这种全称，
/// 而本地只有简称"宁德时代"，对不上；有了 <see cref="FullName"/> 才能把对手方还原成股票代码。
///
/// 但它本身也是一份公司基本面档案——省份、员工数、实控人、主营业务、中介机构。
/// 这些字段是**同一个请求一起带回来的**（东财这类接口 <c>columns=ALL</c> 一次给整条记录），
/// 所以存下来不额外花任何抓取成本。
/// </summary>
public class CompanyProfile
{
    public string Code { get; set; } = "";

    /// <summary>全称"宁德时代新能源科技股份有限公司"。<b>实体消歧靠它</b>。</summary>
    public string FullName { get; set; } = "";

    public string Abbr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string OrgForm { get; set; } = "";
    public string FoundDate { get; set; } = "";
    public string ListingDate { get; set; } = "";
    public string ListingState { get; set; } = "";

    /// <summary>
    /// 注册资本（<b>万元</b>，东财 REG_CAPITAL）。
    /// ⚠ 跟 <see cref="RegCapital"/> 差 1 万倍，光看名字看不出来——宁德时代 462677.041 万元。
    /// </summary>
    public double? RegCapitalWan { get; set; }

    /// <summary>注册资本（<b>元</b>，东财 REG_CAPITALY）。宁德时代 4626770410 元。</summary>
    public double? RegCapital { get; set; }

    public string Currency { get; set; } = "";
    public string Province { get; set; } = "";
    public string City { get; set; } = "";
    public string District { get; set; } = "";
    public string RegAddress { get; set; } = "";
    public string Address { get; set; } = "";
    public string Postcode { get; set; } = "";
    public string IndustryCsrc { get; set; } = "";

    /// <summary>员工数。空值率约 12%。</summary>
    public int? EmpNum { get; set; }

    public string LegalPerson { get; set; } = "";
    public string ActualHolder { get; set; } = "";

    /// <summary>最终控制人。空值率约 41%——很多公司只披露到实控人这一层。</summary>
    public string FinalHolder { get; set; } = "";

    public string HolderName { get; set; } = "";
    public double? HolderRatio { get; set; }
    public string Chairman { get; set; } = "";
    public string President { get; set; } = "";
    public string Secretary { get; set; } = "";
    public string PublishPerson { get; set; } = "";
    public string SecretaryTel { get; set; } = "";
    public string OrgTel { get; set; } = "";
    public string OrgFax { get; set; } = "";
    public string OrgEmail { get; set; } = "";
    public string OrgWeb { get; set; } = "";
    public string RegNum { get; set; } = "";
    public string LawFirm { get; set; } = "";
    public string AccountFirm { get; set; } = "";
    public string Cpa { get; set; } = "";

    /// <summary>实控人变更历史，形如 "20171110 李平,曾毓群→20240208 曾毓群"。</summary>
    public string AhChange { get; set; } = "";

    /// <summary>主营业务，一句话（平均 44 字）。长篇的在 <see cref="CompanyNarrative"/> 里。</summary>
    public string MainBusiness { get; set; } = "";

    /// <summary>东财机构号。出问题时拿它去东财那边对得上同一条记录。</summary>
    public string OrgCodeEm { get; set; } = "";

    public string ReportDate { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 公司档案里的**长文本**部分（2026-09-08）。
///
/// 跟 <see cref="CompanyProfile"/> 是同一份接口响应，拆两张表存。不是洁癖：
/// 档案表会被**匹配步骤全表读**（5634 行），而 <see cref="BusinessReview"/> 平均 4186 字、
/// 最长 46410 字，一个字段就占整条记录体积的 69%。混在一行里，每次全表扫描要多读 3 倍数据，
/// 而这些长文本是"查某一家时才看"的东西。
/// </summary>
public class CompanyNarrative
{
    public string Code { get; set; } = "";

    /// <summary>公司简介（平均 664 字）。</summary>
    public string OrgProfile { get; set; } = "";

    /// <summary>公司沿革/大事年表（平均 386 字）。</summary>
    public string OrgEvolution { get; set; } = "";

    /// <summary>经营范围，工商登记原文（平均 252 字）。</summary>
    public string BusinessScope { get; set; } = "";

    /// <summary>经营评述（平均 4186 字，最长 46410）。整张表 69% 的体积在这一列。</summary>
    public string BusinessReview { get; set; } = "";

    public DateTime FetchedAt { get; set; }
}
