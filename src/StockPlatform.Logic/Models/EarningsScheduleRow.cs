namespace StockPlatform.Logic.Models;

/// <summary>
/// 定期报告的**预约披露日**（2026-09-01 新增）。数据源见 CninfoPrebookProvider。
///
/// 为什么要存全套日期而不只存一个"下次财报日"：
///   ① 预约日**会改**，而且能改三次——实测沪市 2000 条样本里 12% 改过；
///   ② 改的方向**不是只会延后**：提前 133 条（55%）、延后 105 条（44%），
///      最多提前 44 天、最多延后 62 天。所以既不能抓一次当定论，也不能只在临近时才复查。
/// 存下变更轨迹本身也有信息量——一只票反复推迟披露，往往不是好信号。
/// </summary>
/// <param name="Code">6 位股票代码</param>
/// <param name="ReportPeriod">报告期，如 2026-06-30</param>
/// <param name="AppointDate">首次预约披露日</param>
/// <param name="Change1">第一次变更后的日期（没改过则为 null）</param>
/// <param name="Change2">第二次变更</param>
/// <param name="Change3">第三次变更</param>
/// <param name="ActualDate">实际披露日（还没披露则为 null）</param>
public readonly record struct EarningsScheduleRow(
    string Code,
    DateTime ReportPeriod,
    DateTime? AppointDate,
    DateTime? Change1,
    DateTime? Change2,
    DateTime? Change3,
    DateTime? ActualDate)
{
    /// <summary>
    /// **当前有效的预约日**：取最后一次变更；没改过就是首次预约日。
    /// 界面上的"财报日"显示的就是这个。
    /// </summary>
    public DateTime? EffectiveDate => Change3 ?? Change2 ?? Change1 ?? AppointDate;

    /// <summary>改过几次（0~3）。</summary>
    public int ChangeCount => (Change1 is null ? 0 : 1) + (Change2 is null ? 0 : 1) + (Change3 is null ? 0 : 1);

    /// <summary>还没实际披露——这种才需要每天复查，见 CninfoPrebookProvider 的类注释。</summary>
    public bool Pending => ActualDate is null;
}
