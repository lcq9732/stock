namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 本地库的**进程内写闸**（2026-09-21）。要串行化的不是单次写，而是
/// 「读一段 → 按它算 → 写回去」这种**读改写组合**——SQLite 只允许一个写者，
/// 而两个这样的组合交叉跑，后一个算的时候看到的是前一个写之前的样子。
///
/// ════ 为什么是一个公开的锁对象，而不是把 lock 内置进每个仓储方法 ════
/// 内置的话保护不到组合：`Query(...)` 出来、算完、`Upsert(...)` 回去——两次调用各自加锁，
/// 中间那道缝仍然敞着。所以闸必须由调用方按**整个组合**的粒度持有，仓储层只提供这把闸。
///
/// ════ 为什么归 Sqlite 层 ════
/// 分层职责原则（doc/solution-class-map.md §0.1）：本地库的并发控制是"操作本地库"的一部分。
/// 在这之前它是 <c>FetchOrchestrator</c> 的一个私有字段 <c>_dbLock</c>，于是
/// **新框架的任务全都拿不到它**——`EtfRawBarTask`、`LhbTask` 这些迁过去的任务一直在裸写。
/// 平时没出事是因为计划引擎串行跑、`SourceAdmission` 还挡着同源并发；
/// 但"手动点一项 + 计划正在跑另一项"这个组合从来没有东西挡着。
///
/// ⚠ 只挡**本进程**。Fetcher 写、Analyzer 只读是靠 WAL 模式隔离的，跟这把闸无关。
/// </summary>
public static class SqliteWriteGate
{
    /// <summary>
    /// 本地库（<c>current.sqlite</c>）的写闸。用法就是 <c>lock (SqliteWriteGate.Local) { ... }</c>，
    /// 范围盖住**整个读改写组合**，不要只盖住那一行写。
    ///
    /// 只有一把、不按表分：分表的话"同时写两张表"确实能放开，但代价是每加一张表就要判一次
    /// 该用哪把锁，判错了又是静默的。抓取本来就是网络受限（几秒一个请求），写库那点时间
    /// 不值得用这个风险去换。
    /// </summary>
    public static readonly object Local = new();
}
