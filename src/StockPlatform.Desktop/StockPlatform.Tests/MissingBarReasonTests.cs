using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 待补名单里"缺行"和"值错"两类记录的隔离（2026-09-09）。
///
/// 两类走的是同一条管道（<see cref="Manifest.MissingBars"/> → 【重新拉取失败】），但补法和复查
/// 方式完全不同：缺行看"行在不在"，值错要重查对应判据。**隔离没做好就会互相清掉，而且很安静**
/// ——这套断言盯的就是那件事，两处都是实际写错过的：
///   ① 体检的面级落账把同 code+口径的值类记录连带删了（被 FullAuditTaskTests 抓到）；
///   ② 【重新拉取失败】的 SaveProgress 把 MissingBars 整体换成"按口径分组"的内容，
///      值类记录全丢（那时候名单刚被体检填上）。
/// </summary>
public class MissingBarReasonTests
{
    [Fact]
    public void 老记录没有Reason字段_一律当缺行()
    {
        var r = new MissingBarRange { Code = "600000", Reason = null! };
        Assert.Equal(AuditFindingKind.Gap, r.EffectiveReason);
        Assert.False(r.IsValueIssue);
    }

    [Fact]
    public void 空串也当缺行()
    {
        var r = new MissingBarRange { Code = "600000", Reason = "" };
        Assert.Equal(AuditFindingKind.Gap, r.EffectiveReason);
        Assert.False(r.IsValueIssue);
    }

    [Fact]
    public void 缺省就是缺行()
    {
        Assert.False(new MissingBarRange { Code = "600000" }.IsValueIssue);
    }

    [Theory]
    [InlineData(AuditFindingKind.Intraday)]
    [InlineData(AuditFindingKind.NullValue)]
    [InlineData(AuditFindingKind.Ohlc)]
    [InlineData(AuditFindingKind.Inconsistent)]
    public void 四类值问题都算值错(string reason)
    {
        var r = new MissingBarRange { Code = "600000", Reason = reason };
        Assert.True(r.IsValueIssue);
        Assert.Equal(reason, r.EffectiveReason);
    }

    /// <summary>Ratio 和 Note 不进待补名单，所以不该出现在 MissingBars 里——
    /// 这里只是把"它们是只报数的那类"这个约定钉住，免得以后有人顺手塞进去。</summary>
    [Fact]
    public void 只报数的那两类不该进名单()
    {
        Assert.Equal("ratio", AuditFindingKind.Ratio);
        Assert.Equal("note", AuditFindingKind.Note);
    }
}
