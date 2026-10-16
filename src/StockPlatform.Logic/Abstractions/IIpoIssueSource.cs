using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>一轮抓取的对账数据（provider 边抓边填，任务收尾时拿来核对、报告）。</summary>
public sealed class IpoFetchStats
{
    /// <summary>接口自报的总行数（第一页给）。抓完拿实际行数比——对不上就是中途悄悄少了。</summary>
    public int? ReportedTotal { get; set; }

    /// <summary>实际拿到的行数。</summary>
    public int Fetched { get; set; }

    /// <summary>接口返回了、列目录里没有的列（存不进来，要人去加目录）。</summary>
    public HashSet<string> UnknownColumns { get; } = new(StringComparer.Ordinal);

    /// <summary>列目录里有、这一轮接口一行都没给的列（接口删列或改名了）。</summary>
    public HashSet<string> MissingColumns { get; } = new(StringComparer.Ordinal);

    /// <summary>数值列收到了解析不了的文本（存成空）。</summary>
    public HashSet<string> TypeMismatches { get; } = new(StringComparer.Ordinal);
}

/// <summary>新股发行数据的来源（2026-10-16：东财 RPTA_APP_IPOAPPLY）。</summary>
public interface IIpoIssueProvider
{
    event Action<string>? OnStatus;

    /// <summary>
    /// 一页一批地交出来。<paramref name="applySince"/> 为 null 取全量（1990 年起全部），
    /// 否则只取申购日在它之后的（日常增量）。对账数据填进 <paramref name="stats"/>。
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<IpoIssueRow>> FetchAsync(
        DateOnly? applySince, IpoFetchStats stats, CancellationToken ct = default);
}

/// <summary>新股发行表的本地存取。</summary>
public interface IIpoIssueRepository
{
    void EnsureSchema();

    /// <summary>按代码覆盖写入全部 151 列。</summary>
    void Upsert(IReadOnlyList<IpoIssueRow> rows);

    /// <summary>
    /// 把上市日写进名册（StockMeta.list_date）。**只改名册里已有的个股**（含退市），不新建名册行；
    /// 返回实际改到的行数。
    /// </summary>
    int UpdateListDates(IReadOnlyList<IpoIssueRow> rows);
}
