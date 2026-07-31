namespace StockPlatform.Logic.Models;

/// <summary>
/// Small piece of cross-run state for the Fetcher UI, persisted next to the local database
/// (see doc/data-platform-design.md §3.9 for the 2026-07-09 removal of the earlier master/daily
/// output-file bookkeeping this class used to also carry — that scheme was a holdover from an
/// abandoned multi-machine-via-netdisk sharing design and never actually served a purpose here).
/// </summary>
public class Manifest
{
    /// <summary>When the last "拉取全部"/"拉取当天" run actually completed (wall-clock, not a
    /// trading day) — paired with the Bar table's own earliest/latest period_start so the Fetcher
    /// UI can show both "数据覆盖到哪天" and "上次真的抓取是什么时候"，让用户判断该不该再抓一次，
    /// 不用凭感觉重复点。Updated every time a run reaches completion, even if it found nothing new
    /// (a clean "刚检查过，没有新数据" is still worth recording as a last-checked time).</summary>
    public DateTime? LastFetchAt { get; set; }
    public string? LastFetchKind { get; set; } // "拉取全部" / "拉取当天" / "重新拉取失败股票"

    /// <summary>K线抓取失败、还没成功补上的股票代码（2026-07-08新增）——每次"拉取全部"/"拉取
    /// 当天"/"重新拉取失败股票"结束后都会更新：本轮尝试过且这次成功了的代码会被移出，这次还是
    /// 失败的会被加入/保留。用户可以在Fetcher界面点"重新拉取失败股票"只针对这份名单重试，不用
    /// 重新跑一遍全市场；只要这份名单不为空，就可以一直重复点这个按钮。</summary>
    public List<string> FailedCodes { get; set; } = new();

    /// <summary>流通市值抓取失败、还没成功补上的股票代码（2026-07-09新增）。跟FailedCodes不完全
    /// 一样——流通市值现在是"新浪股票列表整体扫一遍"（SinaListMarketCapFetcher），不是逐只单独
    /// 查，所以"失败"的粒度是"这一整轮扫描失败了"：扫描失败时把这一轮请求的全部代码都记进来，
    /// 扫描成功时把这一轮请求的代码全部移出（某只股票本来就没有市值数据不算失败，不会被记录）。
    /// "重新拉取失败股票"点击时会用这份名单重新扫一遍——注意扫描本身还是全市场性质的，重试并不会
    /// 比正常跑一次更快，但至少能正确清零失败名单、把之前没写进去的值补上。</summary>
    public List<string> FailedMarketCapCodes { get; set; } = new();

    /// <summary>资金净流入抓取失败、还没成功补上的股票代码（2026-07-09新增）——这个是逐只股票
    /// 单独查的，跟FailedCodes（K线）同样的按股票精确追踪逻辑，"重新拉取失败股票"点击时只会
    /// 针对名单里这些代码重新查，不用等一轮全市场扫描。</summary>
    public List<string> FailedNetInflowCodes { get; set; } = new();

    /// <summary>指数成分权重（中证指数官网 closeweight.xls）抓取失败、还没补上的指数代码
    /// （2026-07-16新增）——中证源不稳、失败率较高，逐指数精确记录，"重新拉取失败股票"会一并重试。
    /// 注意非中证系指数本来就没有权重文件，不算失败、不会被记进来（见 CsindexWeightProvider）。</summary>
    public List<string> FailedIndexWeightCodes { get; set; } = new();

    /// <summary>指数成分名单（新浪指数成分接口）抓取失败、还没补上的指数代码（2026-07-16新增）——
    /// 逐指数精确记录，跟权重共用"重新拉取失败股票"重试。新浪对某些老指数本来就无成分数据，返回空
    /// 不算失败；只有请求本身失败（网络/限流）才记进来。</summary>
    public List<string> FailedIndexConsCodes { get; set; } = new();

    /// <summary>股东数据（新浪股本股东页：户数+十大股东+十大流通股东）抓取失败、还没补上的股票代码
    /// （2026-07-16新增）——逐只精确记录，共用"重新拉取失败股票"重试。全市场逐只抓、量大易有零星失败。</summary>
    public List<string> FailedShareholderCodes { get; set; } = new();

    /// <summary>分红送配（新浪分红派息页 vISSUE_ShareBonus）抓取失败、还没补上的股票代码
    /// （2026-07-31新增）——逐只精确记录，共用"重新拉取失败股票"重试。全市场逐只抓、量大易有零星失败。</summary>
    public List<string> FailedDividendCodes { get; set; } = new();
}
