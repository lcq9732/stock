using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>指数成分名单 / 成分权重 / ETF→指数映射 的本地存取（IndexCons / IndexWeight / EtfIndexMap
/// 三张表）。成分与权重按指数"删旧写新"覆盖（成分会调整，不能 INSERT OR IGNORE 累积）；ETF 映射整体
/// 覆盖。查询侧提供"股票→指数""股票→ETF"反查，供后续 Analyzer 使用。</summary>
public interface IIndexConsRepository
{
    void EnsureSchema();

    /// <summary>用一次抓取结果替换某指数的成分名单（先删该 index_code 旧行再写）——每只成分带纳入日期。</summary>
    void ReplaceCons(string indexCode, IEnumerable<(string Code, DateTime? InDate)> members, DateTime fetchedAt);

    /// <summary>用一次抓取结果替换某指数的成分权重（先删该 index_code 旧行再写）。</summary>
    void ReplaceWeights(string indexCode, IEnumerable<IndexWeightRow> rows);

    /// <summary>
    /// 每个指数**本地最新的权重基准日**（2026-09-02 新增）——给"这一期已经抓过了就别再抓"用。
    ///
    /// 中证的 closeweight.xls 是**月度**更新（基准日是月末那个交易日），而这一项原来每次跑都要
    /// 硬抓 732 个指数，很容易把中证那边的反爬撞醒。有了这份"本地到哪一期"，月中再跑就能整批跳过。
    /// </summary>
    Dictionary<string, DateTime> GetLatestWeightDateByIndex();

    /// <summary>整体替换 ETF→指数映射表（清空再写）。indexCode 为 null 表示该 ETF 未匹配到指数。</summary>
    void ReplaceEtfIndexMap(IEnumerable<(string EtfCode, string? IndexCode, string MatchType)> rows);

    /// <summary>某个指数当前的成分股代码（6 位）——正查，跟 <see cref="GetIndexesByStock"/> 反向。
    /// 给"风格温度计"用（晨检里拿红利/蓝筹指数的成分股等权算近期超额，见 StyleGauge）。
    ///
    /// 注意口径：返回的是**最近一次抓取时**的成分名单，不是历史上某天的名单。拿它回溯很久以前
    /// 会有成分变迁/幸存者偏差；算最近几天的风格强弱没问题。</summary>
    List<string> GetConsByIndex(string indexCode);

    /// <summary>反查：某只股票（6 位）所属的指数代码列表。</summary>
    List<string> GetIndexesByStock(string stockCode);

    /// <summary>反查：某只股票（6 位）被哪些 ETF（带前缀 8 位）纳入 = 该股所属指数 → 跟踪这些指数的 ETF。</summary>
    List<string> GetEtfsByStock(string stockCode);

    /// <summary>已存有成分的指数个数（供界面显示"数据状态"）。</summary>
    int GetConsIndexCount();

    /// <summary>成分名单最近一次抓取时刻（没有数据为 null）。</summary>
    DateTime? GetLatestConsFetch();
}
