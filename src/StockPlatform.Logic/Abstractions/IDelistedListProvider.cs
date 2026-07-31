using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取沪深两所官网的"终止上市公司"名单（合并返回）——补退市股历史K线的代码来源，
/// 用于消除回测的幸存者偏差（退市股K线本身用现有行情源按代码请求即可取到）。</summary>
public interface IDelistedListProvider
{
    event Action<string>? OnStatus;

    /// <summary>取两所全部终止上市公司（只含A股代码：00/30/60/68 前缀，剔除B股等）。</summary>
    Task<List<DelistedStockRow>> GetAllAsync(CancellationToken ct = default);
}
