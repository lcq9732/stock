using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>龙虎榜的本地存取（Lhb 表）——按交易日累积保留历史，见各方法上的写入语义说明。</summary>
public interface ILhbRepository
{
    void EnsureSchema();

    /// <summary>
    /// 写入一批龙虎榜记录（已存在的 (trade_date, stock_code, reason) 忽略）。
    ///
    /// ⚠ 2026-09-17 起**产品代码里没有调用方了**，抓取的每一条路径都走
    /// <c>LhbDayWriter</c>（派生对应值 + <see cref="ReplaceDays"/>）。留着是给单测说明语义用的。
    /// 新写一条路径别图省事用它：它既不派生对应值、也不清掉那天的旧行，
    /// 上榜原因文本一变就并排多出一套。
    /// </summary>
    void InsertOrIgnore(IEnumerable<LhbRow> rows);

    /// <summary>
    /// 写入一批、**同主键的覆盖**（2026-09-09）。东财源要用它，理由有两个：
    ///   ① 上榜后 N 日涨跌幅是滞后字段，当天抓是空的、一个月后才有值——不覆盖就永远填不上；
    ///   ② 派生出来的对应值/成交量以后规则改进了要能重算。
    /// 新浪那条路径仍走 <see cref="InsertOrIgnore"/>，语义不变。
    /// </summary>
    void Upsert(IEnumerable<LhbRow> rows);

    /// <summary>
    /// 把这些交易日的数据整天换掉：先删这一天的全部行，再写入给定的行，同一个事务。
    ///
    /// 换数据源时必须这么做，不能靠 upsert：两个源的上榜原因文本不一样（新浪是归并过的粗类、
    /// 东财是交易所原文），而 reason 是主键的一部分——upsert 只会**并排多出一套行**，
    /// 26 万行的历史会变成 53 万行，且没有任何字段能一眼分出哪套是哪套。
    /// </summary>
    /// <returns>(删除行数, 写入行数)</returns>
    (int Deleted, int Inserted) ReplaceDays(IEnumerable<LhbRow> rows);

    /// <summary>某个交易日区间内、指定数据源的行数——迁移完拿它跟东财自报的 count 对账。
    /// <paramref name="source"/> 传 null 表示不限来源。</summary>
    int CountRows(DateOnly start, DateOnly end, string? source = null);

    /// <summary>
    /// 把整张 Lhb 表导出成一个**独立的 sqlite 文件**（换源前的备份）。
    ///
    /// 故意不做成库内的 Lhb_backup 表：备份的意义是"主库出事时还在"，跟主库同生共死的副本
    /// 只是把 23GB 的库撑得更大。导出的文件能直接 ATTACH 回来做新旧比对。
    /// </summary>
    /// <returns>导出的行数。</returns>
    int ExportTo(string targetDbPath);

    /// <summary>本地已有龙虎榜的最新交易日（没有数据为 null），供界面显示与判断从哪天续抓。</summary>
    DateTime? GetLatestTradeDate();

    /// <summary>本地已有龙虎榜数据的全部交易日集合——供"一键补齐每日历史"跳过已有的日子、不重复请求。</summary>
    HashSet<DateOnly> GetTradeDates();
}
