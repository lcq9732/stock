using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 按**实际的量**定这一条该是几档。纯计算，可单测。
///
/// ════ 为什么档位不能在挂观察项的时候定死 ════
/// 实机上康辰药业解禁 **74 股**（两千块钱），跟"解禁占流通 30%"用的是同一个 A 档
/// （2026-09-12 用户指出）。日期回答不了"要不要管"，**量才回答**。
/// 挂的时候还不知道量，所以档位得在求值时按数重算。
///
/// ════ 只有"轻重取决于量"的事项才走这里 ════
/// 回购方案的 stage 跃迁（开始买了没有）跟量无关，多少钱都是 A 档；
/// 业绩预告预增 5% 和预增 300% 也都值得看一眼。这类保持挂上时定的档。
/// </summary>
public static class WatchPriority
{
    public const string Push = "A";     // 推送
    public const string Daily = "B";    // 进日报
    public const string LogOnly = "C";  // 只落库

    /// <summary>
    /// 解禁按**占流通股比例**定档。阈值是拍的，理由：
    ///   · ≥10% 的解禁在 A 股是实打实的抛压事件，值得提前一个月准备；
    ///   · 3~10% 要知道，但不必打断手头的事；
    ///   · &lt;3% 常年有（员工持股、小额定增到期），推送只会让人对提醒脱敏。
    /// 抽不到占比时退回兜底档——**不猜**。
    /// </summary>
    public static string ForShareLift(double? freeRatioPct) => freeRatioPct switch
    {
        null => Daily,
        >= 10 => Push,
        >= 3 => Daily,
        _ => LogOnly,
    };

    /// <summary>
    /// 按 <see cref="WatchItem.Kind"/> 和实际取值重算档位；不需要按量分档的原样返回。
    /// </summary>
    /// <param name="extra">取值器给出的判档依据（解禁＝占流通比）。</param>
    public static string Resolve(WatchItem item, double? extra) => item.Kind switch
    {
        WatchKind.ScheduleAhead when item.Expr == "ShareLift" => ForShareLift(extra),
        _ => item.Priority,
    };
}
