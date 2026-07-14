using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>板块行情快照的本地存取。板块数据每次抓取整体覆盖（不像 Bar 那样按日累积）——"热点"本身
/// 就是"当下的"，只保留最近一次快照即可，所以是 ReplaceAll 语义而非增量。</summary>
public interface IBoardRepository
{
    void EnsureSchema();

    /// <summary>用一次抓取的结果整体替换本地板块数据（清空 Board/BoardMember 再写入）。</summary>
    void ReplaceAll(IEnumerable<Board> boards);

    /// <summary>读板块列表，按涨跌幅从高到低排序。type 为 null 时返回全部。</summary>
    List<Board> QueryBoards(BoardType? type = null);

    /// <summary>读某个板块的成分股代码。</summary>
    List<string> QueryMembers(string boardCode);

    /// <summary>反查：股票代码 → 它所属的概念/题材板块名称列表（只含 Concept 类型）。一次 JOIN 查询
    /// 建好整张映射，供自选股等需要"这只票在哪些概念板块里"的地方一次性取用，避免逐板块查询。</summary>
    Dictionary<string, List<string>> GetConceptBoardsByStock();

    /// <summary>本地板块快照的抓取时刻（没有数据时为 null），用于界面显示"数据截至"。</summary>
    DateTime? GetLatestAsOf();
}
