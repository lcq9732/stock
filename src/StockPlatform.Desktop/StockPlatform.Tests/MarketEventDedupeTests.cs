using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 市场事件三表的**批内主键去重自检**（2026-09-18 拆成两档）。
///
/// ════ 为什么要拆 ════
/// 原来只要主键撞了就报"会静默丢数据"。实测【拉取市场事件】因此连着十几天每轮带 1 条错误，
/// 而那 1 条零影响：东财把"2 家投资者"展开成两行、区分列 <c>NUM</c> 两行都给 1，
/// 个人投资者又没有机构代码和参与人员 ⇒ 两行**逐字段相同**，覆盖掉一条什么都没丢
/// （真实家数在 <c>OrgTotal</c> 里存着）。
///
/// **天天喊的告警等于没有告警**——所以判据要分得开"接口自己重复返回"和"主键少了区分列"。
/// 这一组把两个方向都钉住：该闭嘴的闭嘴，该喊的照喊。
/// </summary>
public class MarketEventDedupeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMarketEventRepository _repo;
    private readonly List<string> _warnings = [];

    public MarketEventDedupeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mktdedupe_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteMarketEventRepository(_dbPath);
        _repo.EnsureSchema();
        _repo.OnWarning += _warnings.Add;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>主键那五列固定，其余字段由调用方决定——两行差不差就看那些。</summary>
    private static OrgSurvey Survey(string code = "600609", string org = "投资者",
        string receptionist = "公司董事长 冯圣良", string investigators = "", int total = 2,
        int surveyNo = 1) => new()
        {
            Code = code,
            Name = "云赛智联",
            NoticeDate = new DateTime(2026, 9, 16),
            ReceiveStartDate = new DateTime(2026, 9, 15),
            OrgName = org,
            SurveyNo = surveyNo,
            ReceiveWay = "网络互动,业绩说明会",
            Receptionist = receptionist,
            Investigators = investigators,
            OrgTotal = total,
            FetchedAt = DateTime.Now,
        };

    /// <summary>库里的 survey_no 升序——顺延有没有生效、有没有盖掉别人，看它最直接。</summary>
    private List<int> SurveyNos()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT survey_no FROM OrgSurvey ORDER BY survey_no";
        var list = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    /// <summary>主键三列固定的限售解禁行——差异只在解禁股数上。</summary>
    private static ShareLift Lift(double shares) => new()
    {
        Code = "600609",
        Name = "云赛智联",
        FreeDate = new DateTime(2026, 9, 16),
        ShareType = "首发原股东限售股份",
        LiftShares = shares,
        FetchedAt = DateTime.Now,
    };

    [Fact]
    public void 接口原样重复返回_去重但不报错()
    {
        // 这就是生产上那 2 行：主键撞了，但逐字段相同。丢一条什么都没少。
        var a = Survey();
        var b = Survey();

        int written = _repo.UpsertOrgSurveys([a, b]);

        Assert.Equal(1, written);
        Assert.Empty(_warnings);          // 一个字都不该说
    }

    [Fact]
    public void 抓取时刻不同不算内容不同()
    {
        // FetchedAt 是"我们什么时候抓的"，不是数据内容——同一批里它也可能差几毫秒。
        var a = Survey();
        var b = Survey();
        b.FetchedAt = a.FetchedAt.AddSeconds(3);

        _repo.UpsertOrgSurveys([a, b]);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void 主键撞了而内容不同_顺延区分列两行都留下()
    {
        // 两次调研的接待人员不一样 ⇒ 这是**两条真记录**，覆盖掉一条就真丢了。
        // 2026-09-19 改成顺延 survey_no：两行都落库，一条都不少。
        var a = Survey(receptionist: "公司董事长 冯圣良");
        var b = Survey(receptionist: "董事会秘书 孙学龙");

        int written = _repo.UpsertOrgSurveys([a, b]);

        Assert.Equal(2, written);
        Assert.Equal(2, _repo.Count("OrgSurvey"));
        Assert.Equal([1, 2], SurveyNos());
        Assert.Empty(_warnings);           // 没丢数据就不该报错
    }

    [Fact]
    public void 顺延过的批次重抓一次_不长副本()
    {
        // 顺延号是"批内第几次撞"，靠抓取侧的排序唯一键保证两次抓取顺序一致（见
        // EastMoneyMarketEventProvider 的 tieBreaker）。顺序一致 ⇒ 顺延结果一致 ⇒
        // INSERT OR REPLACE 覆盖回同样两行。这条是整个方案幂等性的落脚点，必须钉住。
        List<OrgSurvey> Batch() =>
        [
            Survey(receptionist: "公司董事长 冯圣良"),
            Survey(receptionist: "董事会秘书 孙学龙"),
        ];

        _repo.UpsertOrgSurveys(Batch());
        _repo.UpsertOrgSurveys(Batch());
        _repo.UpsertOrgSurveys(Batch());

        Assert.Equal(2, _repo.Count("OrgSurvey"));
        Assert.Equal([1, 2], SurveyNos());
    }

    [Fact]
    public void 顺延时跳过接口已占用的序号()
    {
        // 同一天同一机构本来就可能有多次调研（实测中信证券对 600177 有 NUM=1/7/22）。
        // 第二行顺延到 2 时若 2 已被真实记录占着，必须继续往后挪，不能盖掉人家。
        var a = Survey(receptionist: "甲", surveyNo: 1);
        var b = Survey(receptionist: "乙", surveyNo: 2);   // 接口真给的 2
        var c = Survey(receptionist: "丙", surveyNo: 1);   // 跟 a 撞，顺延时要跳过 2

        int written = _repo.UpsertOrgSurveys([a, b, c]);

        Assert.Equal(3, written);
        Assert.Equal([1, 2, 3], SurveyNos());
    }

    [Fact]
    public void 顺延不了的表_照样报丢数据()
    {
        // 只有机构调研给得起顺延（survey_no 的语义本来就是"记录序号"）。其余三张表的主键
        // 每一列都是实打实的业务值，撞了只能覆盖 —— 那条"静默丢数据"的告警得留着。
        var a = Lift(shares: 100);
        var b = Lift(shares: 200);

        int written = _repo.UpsertShareLifts([a, b]);

        Assert.Equal(1, written);
        var w = Assert.Single(_warnings);
        Assert.Contains("内容还不一样", w);
        Assert.Contains("静默丢数据", w);
        Assert.Contains("600609", w);      // 样例 key 要带上，否则没法定位缺哪一列
    }

    [Fact]
    public void 重复返回占比过高_提一句但不说丢数据()
    {
        // 零告警的代价是"接口哪天开始大批量重复返回也没人知道"。所以量够大、占比也够高时仍要提，
        // 只是措辞完全不同——那不叫丢数据。
        var rows = new List<OrgSurvey>();
        for (int i = 0; i < 100; i++) rows.Add(Survey(code: $"60{i:D4}"));
        for (int i = 0; i < 25; i++) rows.Add(Survey(code: $"60{i:D4}"));   // 原样再来一遍

        _repo.UpsertOrgSurveys(rows);

        var w = Assert.Single(_warnings);
        Assert.Contains("原样重复返回", w);
        Assert.DoesNotContain("静默丢数据", w);
    }

    [Fact]
    public void 重复量太小时不提_小样本的比例是噪声()
    {
        // 一批 2 行、其中 1 行重复就是 50%，按占比判必报——可那说明不了任何事。
        // 生产上机构调研常年是 5 万行里 2 行，正是靠这道下限才没有天天喊。
        var rows = new List<OrgSurvey> { Survey(), Survey() };

        _repo.UpsertOrgSurveys(rows);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void 没有重复时什么都不说()
    {
        _repo.UpsertOrgSurveys([Survey(surveyNo: 1), Survey(surveyNo: 7)]);

        Assert.Empty(_warnings);
    }
}
