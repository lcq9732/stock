using System.Text.Json;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 公司档案的解析与落库（2026-09-08）。JSON 从线上原样抄，不联网。
///
/// 盯两件事：
/// 1. <b>一份响应拆两张表，必须一起写</b>——一张写了另一张漏就留下半拉记录，
///    而消歧那一步会读到全称却查不到简介。
/// 2. <b>注册资本那两列差 1 万倍</b>，名字看不出来，搞反了整份数据的量级就错了。
/// </summary>
public class CompanyProfileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteCompanyProfileRepository _repo;

    public CompanyProfileRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cprofile_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteCompanyProfileRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly DateTime Now = new(2026, 9, 8, 12, 0, 0);

    /// <summary>宁德时代那一条，线上原样抄（长文本截短了，其余一字未改）。</summary>
    private const string Row = """
        {"SECUCODE":"300750.SZ","SECURITY_CODE":"300750","SECURITY_NAME_ABBR":"宁德时代",
         "LISTING_STATE":"0","ORG_CODE":"10554205","ORG_NAME":"宁德时代新能源科技股份有限公司",
         "ORG_NAME_ABBR":"宁德时代","ORG_NAME_EN":"Contemporary Amperex Technology Co., Ltd.",
         "FOUND_DATE":"2011-12-16","REG_CAPITAL":462677.041,"REG_CAPITALY":4626770410,
         "LISTING_DATE":"2018-06-11 00:00:00","INDUSTRYCSRC1":"电气机械和器材制造业",
         "ACTUAL_HOLDER":"曾毓群","FINAL_HOLDER":null,"LEGAL_PERSON":"曾毓群",
         "CHAIRMAN":"潘健,曾毓群","PRESIDENT":"曾毓群","SECRETARY":"蒋理","SECRETARY_TEL":"0593-8901666",
         "COUNTRY":"China 中国","PROVINCE":"福建","CITY":"宁德市","DISTRICT":"蕉城区",
         "REG_ADDRESS":"中国福建省宁德市蕉城区漳湾镇新港路2号","ADDRESS_POSTCODE":"352100",
         "ORG_TEL":"0593-8901666","ORG_EMAIL":"CATL-IR@catl.com","ORG_WEB":"www.catl.com",
         "EMP_NUM":185839,"REG_NUM":"91350900587527783P","CPA":"殷雪芳,杨遒景",
         "ORG_PROFILE":"全球领先的零碳新能源科技公司","MAIN_BUSINESS":"动力电池、储能电池的研发生产销售",
         "BUSINESS_SCOPE":"锂离子电池…","BUSINESS_REVIEW":"1、主要业务…"}
        """;

    [Fact]
    public void 解析出全称_消歧全靠它()
    {
        var x = EastMoneyCompanyProfileProvider.Parse(J(Row), Now);

        Assert.NotNull(x);
        var (p, n) = x!.Value;
        Assert.Equal("300750", p.Code);
        Assert.Equal("宁德时代新能源科技股份有限公司", p.FullName);   // ← 匹配靠这一列
        Assert.Equal("宁德时代", p.Abbr);
        Assert.Equal("福建", p.Province);
        Assert.Equal(185839, p.EmpNum);
        Assert.Equal("300750", n.Code);                             // 两张表的 code 必须一致
    }

    [Fact]
    public void 注册资本万元和元不能搞反()
    {
        // ★ REG_CAPITAL 是万元、REG_CAPITALY 是元，差 1 万倍，字段名完全看不出来。
        //   搞反了整份数据的量级就错了，而 4.6 亿和 46 亿看起来都"像个正常的注册资本"。
        var (p, _) = EastMoneyCompanyProfileProvider.Parse(J(Row), Now)!.Value;

        Assert.Equal(462677.041, p.RegCapitalWan);
        Assert.Equal(4626770410, p.RegCapital);
        Assert.Equal(p.RegCapital!.Value, p.RegCapitalWan!.Value * 10000, 0.5);   // 关系钉死
    }

    [Theory]
    [InlineData("""{"SECUCODE":"00700.HK","SECURITY_CODE":"00700","ORG_NAME":"腾讯控股有限公司"}""")]
    [InlineData("""{"SECUCODE":"AAPL.O","SECURITY_CODE":"AAPL","ORG_NAME":"Apple Inc."}""")]
    [InlineData("""{"SECURITY_CODE":"300750","ORG_NAME":"没有 SECUCODE"}""")]
    public void 非A股一律跳过(string json)
        => Assert.Null(EastMoneyCompanyProfileProvider.Parse(J(json), Now));

    [Fact]
    public void FINAL_HOLDER为null不能变成字符串null()
    {
        var (p, _) = EastMoneyCompanyProfileProvider.Parse(J(Row), Now)!.Value;
        Assert.Equal("", p.FinalHolder);   // JSON 里是 null，空值率约 41%
    }

    // ════════ 落库 ════════

    private static (CompanyProfile, CompanyNarrative) Make(string code, string full, string review = "评述")
        => (new CompanyProfile { Code = code, FullName = full, Abbr = "简称", FetchedAt = Now },
            new CompanyNarrative { Code = code, BusinessReview = review, FetchedAt = Now });

    [Fact]
    public void 空批次是空操作()
    {
        _repo.Upsert([Make("300750", "宁德时代新能源科技股份有限公司")]);
        Assert.Equal(0, _repo.Upsert([]));
        Assert.Equal((1, 1), _repo.GetCounts());
    }

    [Fact]
    public void 两张表一起写_条数必须相等()
    {
        // ★ 不等就是有半拉记录：消歧那一步会读到全称却查不到简介
        _repo.Upsert([Make("300750", "宁德时代新能源科技股份有限公司"),
                      Make("000001", "平安银行股份有限公司")]);

        var (profiles, narratives) = _repo.GetCounts();
        Assert.Equal(2, profiles);
        Assert.Equal(profiles, narratives);
    }

    [Fact]
    public void 重抓是覆盖不是重复()
    {
        _repo.Upsert([Make("300750", "旧名称有限公司", "旧评述")]);
        _repo.Upsert([Make("300750", "新名称有限公司", "新评述")]);

        Assert.Equal((1, 1), _repo.GetCounts());
        Assert.Equal("新名称有限公司", _repo.GetAllNames().Single().FullName);
    }

    [Fact]
    public void 取名字时不带长文本()
    {
        // GetAllNames 会被消歧那一步全表读。经营评述平均 4186 字、最长 4.6 万字，
        // 捎上它每次要多读 3 倍数据——所以长文本本来就在另一张表，这里只取三列
        // （2026-09-15 加了 abbr：简称精确档要用，它短，不违反这条）。
        _repo.Upsert([Make("300750", "宁德时代新能源科技股份有限公司", new string('评', 5000))]);

        var names = _repo.GetAllNames();
        Assert.Single(names);
        Assert.Equal("300750", names[0].Code);
        Assert.Equal("宁德时代新能源科技股份有限公司", names[0].FullName);
    }

    [Fact]
    public void 没有全称的行不进名字表()
    {
        // 全称空着的公司参与不了匹配，取出来只会让索引多一个空键
        _repo.Upsert([Make("300750", ""), Make("000001", "平安银行股份有限公司")]);

        Assert.Equal((2, 2), _repo.GetCounts());          // 档案照存
        Assert.Single(_repo.GetAllNames());               // 但不参与匹配
        Assert.Equal("000001", _repo.GetAllNames()[0].Code);
    }
}
