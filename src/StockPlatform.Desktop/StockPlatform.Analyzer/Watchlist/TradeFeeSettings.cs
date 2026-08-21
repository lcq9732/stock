using System.IO;
using System.Text.Json;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// A股交易的费率设置（2026-08-11新增）——用户在"主动仓"页顶部自己填，因为各家券商佣金不一样。
/// 默认值就是用户现在这家的：佣金万分之1.2（不足5元按5元收）、过户费万分之0.1，两者买卖都收；
/// 印花税万分之5，只在卖出时收。费率一律用"万分之几"存（<see cref="CommissionRateBp"/> = 1.2 就是
/// 万分之1.2）——券商和交易所都是这么报价的，界面上填 1.2 比填 0.00012 不容易错。
/// </summary>
public class TradeFeeSettings
{
    /// <summary>佣金费率，单位"万分之"。买卖都收。</summary>
    public double CommissionRateBp { get; set; } = 1.2;

    /// <summary>单笔最低佣金（元）——算出来不足这个数就按这个数收。填0表示不设下限。</summary>
    public double MinCommission { get; set; } = 5;

    /// <summary>过户费费率，单位"万分之"。买卖都收。</summary>
    public double TransferRateBp { get; set; } = 0.1;

    /// <summary>印花税费率，单位"万分之"。**只有卖出收**。</summary>
    public double StampDutyRateBp { get; set; } = 5;

    /// <summary>某一笔的佣金：金额×费率，不足最低佣金按最低佣金。金额为0（老数据没填股数）时不收——
    /// 那不是一笔真实成交，收个最低佣金5元反而把盈亏算歪。</summary>
    public double Commission(double amount)
        => amount <= 0 ? 0 : Math.Max(amount * CommissionRateBp / 10000, MinCommission);

    public double TransferFee(double amount) => amount <= 0 ? 0 : amount * TransferRateBp / 10000;

    public double StampDuty(double amount, TradeSide side)
        => amount <= 0 || side != TradeSide.Sell ? 0 : amount * StampDutyRateBp / 10000;

    /// <summary>一笔成交的全部费用（佣金＋过户费＋卖出印花税）。</summary>
    public double FeeFor(double amount, TradeSide side)
        => Commission(amount) + TransferFee(amount) + StampDuty(amount, side);

    public double FeeFor(TradeLot lot) => FeeFor(lot.Price * lot.Shares, lot.Side);

    /// <summary>界面上用来一眼确认当前费率的说明串。</summary>
    public string Describe()
        => $"佣金万分之{CommissionRateBp:0.###}（最低{MinCommission:0.##}元）、过户费万分之{TransferRateBp:0.###}，买卖都收；"
         + $"印花税万分之{StampDutyRateBp:0.###}，仅卖出";

    public TradeFeeSettings Clone() => (TradeFeeSettings)MemberwiseClone();
}

/// <summary>
/// 费率设置的持久化 + 全局共享实例。跟 <see cref="JsonWatchlistStore"/> 一样是个小 JSON 文件
/// （<c>data\trade-fees.json</c>）：全程序就一份设置，各页共用同一个 <see cref="Current"/> 对象，
/// 用户改完立刻落盘，下次开程序还是这个费率。文件不存在（第一次用）就用默认值，不写文件。
/// </summary>
public class TradeFeeStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>当前生效的费率——各页直接读它（不要各自拷一份，否则改完这页那页还是老费率）。</summary>
    public TradeFeeSettings Current { get; private set; } = new();

    public TradeFeeStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                Current = JsonSerializer.Deserialize<TradeFeeSettings>(json) ?? new TradeFeeSettings();
            }
            catch (Exception)
            {
                // 设置文件坏了不该让程序打不开——退回默认费率，用户在界面上重填一次就好。
                Current = new TradeFeeSettings();
            }
        }
    }

    public void Save()
    {
        lock (_fileLock)
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
    }
}
