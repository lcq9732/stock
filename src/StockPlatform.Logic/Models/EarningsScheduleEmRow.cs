namespace StockPlatform.Logic.Models;

/// <summary>
/// 预约披露日·东财版（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）。
///
/// 跟 <see cref="EarningsScheduleRow"/>（巨潮）是**两个模型**而不是一个带来源标记的：
/// 东财这张表多出 <see cref="IsPublish"/>、<see cref="TradeMarket"/>、<see cref="Quarter"/>
/// 等十来列，合成一个模型会让巨潮那半永远是 null。照 StockIndustry / StockIndustryEm 的先例分开，
/// **合并只发生在消费端**（SqliteEarningsScheduleRepository 的几个查询）。
///
/// 为什么要第二家：巨潮的"已披露"标记实测滞后半天以上（2026-10-27 当天 00:39 标了 1325 只、
/// 14:12 变 1690 只），而【拉取财务报表】拿它当闸门，于是当晚披露的票整批晚一轮。
/// 判据改成「任一家标了已披露就算已披露」后，下限就是现状。
/// </summary>
/// <param name="Code">6 位股票代码（SECURITY_CODE）</param>
/// <param name="ReportPeriod">报告期（REPORT_DATE）</param>
/// <param name="AppointDate">当前有效预约日（APPOINT_PUBLISH_DATE）——东财已经把改期算进去了，
/// 不像巨潮要自己从 change1~3 里挑最后一个</param>
/// <param name="FirstAppointDate">首次预约日（FIRST_APPOINT_DATE）</param>
/// <param name="Change1">第一次改期（FIRST_CHANGE_DATE）</param>
/// <param name="Change2">第二次改期（SECOND_CHANGE_DATE）</param>
/// <param name="Change3">第三次改期（THIRD_CHANGE_DATE）</param>
/// <param name="ActualDate">实际披露日（ACTUAL_PUBLISH_DATE），还没披露则为 null</param>
/// <param name="IsPublish">东财自己的已披露标记（IS_PUBLISH，1=已披露）。
/// 比"看 ActualDate 空不空"更直接，但两者都存着——消费端以 ActualDate 为准、这个作交叉校验，
/// 万一哪天两者打架能立刻发现</param>
/// <param name="ReportTypeName">"2026年 三季报"</param>
/// <param name="Quarter">"2026q3"</param>
/// <param name="TradeMarket">深交所主板 / 北京证券交易所 …（巨潮没有）</param>
/// <param name="SecurityType">A股</param>
/// <param name="BoardName">所属行业板块名</param>
/// <param name="NewBoardCode">BKxxxx</param>
/// <param name="ResidualDays">距预约日还剩几天（东财算好的）</param>
/// <param name="AppointChange">改期说明</param>
/// <param name="AppointChangeDays">改了几天</param>
/// <param name="InfoCode">东财公告编号</param>
/// <param name="Eitime">东财建档时间。⚠ 实测是**这张预约表发布时**的时间
/// （格力 2026-09-30 18:53:36），不是实际披露日的回填时间，别拿它判"东财什么时候标的"</param>
public readonly record struct EarningsScheduleEmRow(
    string Code,
    DateTime ReportPeriod,
    DateTime? AppointDate,
    DateTime? FirstAppointDate,
    DateTime? Change1,
    DateTime? Change2,
    DateTime? Change3,
    DateTime? ActualDate,
    int? IsPublish,
    string? ReportTypeName,
    string? Quarter,
    string? TradeMarket,
    string? SecurityType,
    string? BoardName,
    string? NewBoardCode,
    int? ResidualDays,
    string? AppointChange,
    int? AppointChangeDays,
    string? InfoCode,
    string? Eitime)
{
    /// <summary>
    /// 当前有效的预约日。东财的 <see cref="AppointDate"/> 本身就是算过改期的，
    /// 但万一它为空就退回改期链和首次预约日——兜底方向只能是"有值"。
    /// </summary>
    public DateTime? EffectiveDate
        => AppointDate ?? Change3 ?? Change2 ?? Change1 ?? FirstAppointDate;

    /// <summary>还没实际披露。以 <see cref="ActualDate"/> 为准，不看 <see cref="IsPublish"/>——
    /// 两家合并时比的是日期，口径得统一。</summary>
    public bool Pending => ActualDate is null;
}
