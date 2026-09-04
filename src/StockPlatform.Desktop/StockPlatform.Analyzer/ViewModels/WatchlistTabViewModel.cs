using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Desktop.Shared.Theme;

namespace StockPlatform.Analyzer.ViewModels;

public class WatchlistRowViewModel : ISelectableRow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public WatchlistEntry Entry { get; }
    private readonly JsonWatchlistStore _store;
    private readonly TradeFeeStore _fees;
    private double? _latestClose;

    /// <summary>本行成交明细的汇总（含佣金/过户费/印花税）——费率改了或成交明细改了就重算。</summary>
    private TradeCostSummary _cost;

    public WatchlistRowViewModel(WatchlistEntry entry, IBarRepository barRepository, string conceptBoards, JsonWatchlistStore store, TradeFeeStore fees,
        StockPlatform.Logic.Models.EarningsScheduleRow? autoEarnings = null)
    {
        Entry = entry;
        Board = conceptBoards;
        _store = store;
        AutoEarnings = autoEarnings;
        _fees = fees;
        _cost = TradeCostSummary.For(entry.Lots, fees.Current);
        ComputeTracking(barRepository);
    }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
    /// <summary>该股所属的概念/题材板块（来自"板块热度"抓取的数据，一只票可能属于多个，用顿号连接）；
    /// 没有板块数据或不属于任何概念板块时显示"—"。由 WatchlistTabViewModel 一次性反查后传入。</summary>
    public string Board { get; }
    /// <summary>申万行业（跟其它结果表"板块"列同一个口径，来自本地静态映射，见 IndustryClassifier）。</summary>
    public string Industry => IndustryClassifier.GetIndustry(Entry.Code);
    public string Method => Entry.Method;
    public string DataDate => Entry.DataDate.ToString("yyyy-MM-dd");
    public double PriceAtPick => Entry.PriceAtPick;

    /// <summary>"加入时价格（那天的日期）"——格式跟 <see cref="LatestCloseText"/> 一模一样，
    /// 两列并排就能直接看出"从哪天的多少钱，走到今天的多少钱"。
    ///
    /// 日期用的是 <see cref="WatchlistEntry.DataDate"/>（这个价所属的交易日）而不是 AddedAt
    /// （点"加入"的时刻）：只有同为"价格所属交易日"，跟最新收盘那列才是同一口径、能直接比。
    /// 两者不是同一天时（比如周末把周五的信号加进来），AddedAt 在悬停提示里给出。</summary>
    public string PriceAtPickText =>
        Entry.PriceAtPick > 0 ? $"{Entry.PriceAtPick:F2}（{Entry.DataDate:yyyy-MM-dd}）" : "—";

    /// <summary>加入时价格那格的悬停提示——点"加入"的实际时刻，以及跟数据日期的关系。</summary>
    public string PriceAtPickTooltip =>
        Entry.PriceAtPick > 0
            ? $"{Entry.DataDate:yyyy-MM-dd} 的收盘价 {Entry.PriceAtPick:F2}（信号就是按这一天的数据选出来的）\n"
              + $"实际点击加入的时刻：{Entry.AddedAt:yyyy-MM-dd HH:mm}"
            : "没有记录加入时的价格";
    public string AddedAt => Entry.AddedAt.ToString("yyyy-MM-dd HH:mm");
    public string SatisfiedText => $"{Entry.SatisfiedCount}/{Entry.TotalCount}";

    /// <summary>"选中后表现"——用本地已有的K线历史，从选中当天(DataDate)开始查到最新一根日线，
    /// 拿最新收盘价相对PriceAtPick（选中当天收盘价）算涨跌幅。故意不持久化任何跟踪数据：本地
    /// K线库本来就有选中日之后的完整历史，每次刷新这个Tab直接现算即可，没必要额外存一份、
    /// 也没有"数据过期"的问题。start用DataDate本身（不是DataDate+1）——如果选中日之后还没有更新的
    /// 交易日数据，会查到DataDate自己那根，涨跌幅显示为0%，这正确反映"还没有新的一天可比较"。</summary>
    public string LatestCloseText { get; private set; } = "—";
    public string ChangeText { get; private set; } = "本地无该日期之后的K线数据";
    public Brush ChangeColor { get; private set; } = ThemeBrushes.Gray;

    /// <summary>"选中后涨跌幅"的数值形式——给"自选股"页统计各方法准确率用（平均涨跌/胜率）。</summary>
    public double? SincePickPct { get; private set; }

    /// <summary>本地K线里最新一根的收盘价（没数据时为 null）——【仓位计算器】用它把建议金额折成股数、
    /// 推出止损价和两档止盈价。</summary>
    public double? LatestClose => _latestClose;

    /// <summary>上面那个收盘价是**哪一天**的。盘中打开程序时今天的K线还没入库，它就是昨天的收盘价——
    /// 所以凡是把它当"现价"用的地方都要把日期一起显示出来，否则用户会以为那是实时价
    /// （用户 2026-08-19 指出）。</summary>
    public DateTime? LatestCloseDate { get; private set; }

    /// <summary>含费成本均价（买入总支出÷买入总股数，没买过或老数据没填股数时为 null）——
    /// 【仓位计算器】只把它当**参考信息**显示，不参与仓位计算（成本是沉没成本，见那个窗口的说明）。</summary>
    public double? NetAvgCost => _cost.NetAvgCost;

    // ── 供表格排序用的数值形式（2026-08-19新增）──
    // 那几列显示的是拼好的字符串（"12.34（08-14）"、"1,500股"、"+1,234元（+5.6%）"），DataGrid 默认
    // 就按字符串比大小，于是 13.44% 会排在 2.00% 后面（用户 2026-08-19 报的股息率排序问题）。
    // 每个这样的列都用 SortMemberPath 指到下面对应的数值属性上。

    /// <summary>止亏价的数值形式（没买/已清仓/算不出为 null）。</summary>
    public double? BreakEvenValue => !Entry.HasBought || Entry.IsClosedTrade ? null : _cost.BreakEvenPrice();

    /// <summary>持仓盈亏的数值形式（元，含费口径，跟 <see cref="HoldingText"/> 同源）——
    /// 已平仓给已实现，持仓中给"按现价全卖"的结果，没买过为 null。</summary>
    public double? HoldingPnlValue
    {
        get
        {
            if (!Entry.HasBought) return null;
            if (Entry.IsClosedTrade) return _cost.RealizedNet;
            if (_latestClose is not (> 0)) return null;
            return _cost.TotalPnlIfLiquidated(_latestClose.Value);
        }
    }

    /// <summary>财报日的数值(日期)形式——排序要按时间先后，不能按 "2026-08-26" 这个字符串。</summary>
    public DateTime? EarningsDateValue => Entry.EarningsDate;

    /// <summary>剩余持仓股数的数值形式。</summary>
    public int RemainingSharesValue => Entry.RemainingShares;

    /// <summary>满足条件数的数值形式（"11/12" 按字符串排会把 9/12 排在 11/12 后面）。</summary>
    public int SatisfiedCountValue => Entry.SatisfiedCount;

    /// <summary>是否在"主动仓"池里（显式勾入，或已填买入价）——"自选股"页用一列标出来，让人一眼看出
    /// 哪些样本自己真的下手了。</summary>
    public string TradePoolText => Entry.IsInTradePool ? (Entry.HasBought ? "✔持仓" : "✔已加入") : "";
    public Brush TradePoolColor => Entry.HasBought ? ThemeBrushes.Firebrick : ThemeBrushes.SeaGreen;

    private void ComputeTracking(IBarRepository barRepository)
    {
        var bars = barRepository.Query(Entry.Code, Granularity.Day, start: Entry.DataDate);
        if (bars.Count == 0 || Entry.PriceAtPick <= 0) return;

        var latest = bars[^1];
        _latestClose = latest.Close;
        LatestCloseDate = latest.PeriodStart;
        LatestCloseText = $"{latest.Close:F2}（{latest.PeriodStart:yyyy-MM-dd}）";
        var pct = (latest.Close - Entry.PriceAtPick) / Entry.PriceAtPick * 100;
        SincePickPct = pct;
        ChangeText = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
        ChangeColor = pct >= 0 ? ThemeBrushes.Red : ThemeBrushes.Green; // 国内看盘习惯：涨红跌绿
    }

    // ── 持仓信息（2026-08-11起支持多笔买入/卖出，见 TradeLot）——买卖明细在"主动仓"Tab点
    //    【交易记录】录入，这里只显示汇总：总股数 + 加权均价。一笔都没有=观察中、还没买。 ──

    /// <summary>买入汇总："3笔 1,500股 均12.34"；没买过显示"—"。老数据没填股数时只显示均价。</summary>
    public string BuySummaryText => LotSummary(Entry.BuyLots.Count, Entry.TotalBuyShares, Entry.AvgBuyPrice);

    /// <summary>卖出汇总，格式同 <see cref="BuySummaryText"/>；一笔没卖显示"—"。</summary>
    public string SellSummaryText => LotSummary(Entry.SellLots.Count, Entry.TotalSellShares, Entry.AvgSellPrice);

    /// <summary>还拿着多少股（部分卖出后就是剩下的那部分）——已全部卖出显示"已清仓"。</summary>
    public string PositionText =>
        !Entry.HasBought ? "—"
        : Entry.IsClosedTrade ? "已清仓"
        : Entry.RemainingShares > 0 ? $"{Entry.RemainingShares:N0}股" : "—";

    // ── 财报披露日 ─────────────────────────────────────────────────────────────
    //   2026-08-17 起是手填的（当时本地没有预约披露日这种数据）。
    //   2026-09-01 起由【拉取财报预约日】自动抓（巨潮，深沪京全覆盖），手填保留为**覆盖**手段：
    //     · 你填过 → 一直用你填的，自动值不会盖掉它（清空即可交还给自动值）
    //     · 你没填 → 显示自动抓来的
    //   为什么保留手填：预约日有空窗——上一期都披露完、下一期预约表还没发布时自动值是空的，
    //   那时候你从公告里看到日期还是得能录进来。
    //   单元格里直接编辑、失焦即存；解析不了的输入丢弃（Raise 让界面回显旧值）。

    /// <summary>【拉取财报预约日】抓来的本期预约情况；没抓到/没这只票时为 null。</summary>
    public StockPlatform.Logic.Models.EarningsScheduleRow? AutoEarnings { get; }

    /// <summary>真正用来算"还有几天"的日期：手填的优先，其次是抓来的。</summary>
    private DateTime? EffectiveEarningsDate => Entry.EarningsDate ?? AutoEarnings?.EffectiveDate;

    /// <summary>这一格的值是不是手填的（界面上用来区分显示）。</summary>
    public bool IsEarningsManual => Entry.EarningsDate.HasValue;

    public string EarningsDateText
    {
        get => Entry.EarningsDate?.ToString("yyyy-MM-dd")
            ?? AutoEarnings?.EffectiveDate?.ToString("yyyy-MM-dd")
            ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.EarningsDate = null;
            else if (DateTime.TryParse(t, out var d)) Entry.EarningsDate = d.Date;
            _store.UpdateEarningsDate(Entry.Id, Entry.EarningsDate);
            Raise(nameof(EarningsDateText));
            Raise(nameof(EarningsColor));
            Raise(nameof(EarningsTooltip));
        }
    }

    /// <summary>距财报还有几天（自然日）；没有日期为 null，已过去为负数。</summary>
    private int? DaysToEarnings => EffectiveEarningsDate is { } d ? (int)(d - DateTime.Today).TotalDays : null;

    /// <summary>
    /// 三种颜色对应三种含义，别混：
    ///   红  = 临近财报（提前量跟晨检共用 <see cref="MorningStockRowViewModel.EarningsWarnDays"/>，
    ///        两处不能各定各的，否则这边红了那边不提醒）——跨财报持仓是回测参数里没有的事件风险；
    ///   常色 = 将来某天要披露；
    ///   **灰** = 日期已经过去了，这是**上一次**财报，不是下一次。
    ///
    /// 灰这一档是 2026-09-02 加的：接口只给最近两期，两期都披露完之后会有一段空窗（下期预约表还没发布），
    /// 那时列里显示的是"最近一次已披露"。不压暗的话跟未来的预约日长得一模一样，很容易看成"下次财报在 8-29"。
    /// </summary>
    public Brush EarningsColor => DaysToEarnings switch
    {
        { } d when d >= 0 && d <= MorningStockRowViewModel.EarningsWarnDays => ThemeBrushes.Firebrick,
        < 0 => ThemeBrushes.Gray,
        _ => ThemeBrushes.Foreground,
    };

    public string EarningsTooltip => DaysToEarnings switch
    {
        null => "还不知道下一次财报什么时候披露。\n【拉取财报预约日】每天会自动抓，但预约表是分期发布的——"
             + "上一期都披露完、下一期还没发布时就是空的（季报/半年报的预约表在报告期结束后才出，年报在前一年底）。\n"
             + "在公告里看到日期的话，也可以直接在这一格填，手填的优先级更高。",
        0 => "今天披露财报——利好出尽/低于预期都可能，跨事件持仓的风险自己认。",
        > 0 and <= MorningStockRowViewModel.EarningsWarnDays =>
            $"还有 {DaysToEarnings} 天披露财报（{EffectiveEarningsDate:MM-dd}{EarningsSourceNote}）。\n短线法/回调法的参数是按普通交易日回测的，没区分财报窗口：预期打得越满，兑现日越容易利好出尽。",
        > 0 => $"{EffectiveEarningsDate:yyyy-MM-dd} 披露财报，还有 {DaysToEarnings} 天{EarningsSourceNote}。",
        // 日期已经过去 —— 这是**上一次**财报，不是下一次。列里压成灰色就是这个意思。
        _ => $"⚠ 这是**上一次**财报（{EffectiveEarningsDate:yyyy-MM-dd} 已披露），不是下一次。\n"
           + "下一期的预约表还没发布——接口只给最近两期，两期都披露完就会有这么一段空窗，"
           + "等交易所放出下一期预约表，这里会自动换成新日期。\n"
           + "另外记得跑一次\"季度/不定期\"抓取把新报告期入库，在那之前各方法的财务条件用的还是上一期数据。",
    };

    /// <summary>
    /// 这个日期哪来的、可不可靠。改期是常事——实测沪市 2000 条样本里 12% 改过，
    /// 而且**提前的比延后的还多**（55% vs 44%，最多提前 44 天），所以改过几次值得摆出来。
    /// </summary>
    private string EarningsSourceNote
    {
        get
        {
            if (Entry.EarningsDate.HasValue) return "，你手填的";
            if (AutoEarnings is not { } a) return "";
            return a.ChangeCount switch
            {
                0 => "，交易所预约日",
                1 => $"，交易所预约日（改过 1 次，原定 {a.AppointDate:MM-dd}）",
                _ => $"，交易所预约日（改过 {a.ChangeCount} 次，原定 {a.AppointDate:MM-dd}）",
            };
        }
    }

    /// <summary>止亏价——剩下的股票卖到这个价刚好不赚不亏（买入费用已在成本里，卖出的佣金/过户费/
    /// 印花税按这个价再扣一遍；分批卖过的把已落袋的钱也算进去了）。已清仓/没买过显示"—"。</summary>
    public string BreakEvenText
    {
        get
        {
            if (!Entry.HasBought || Entry.IsClosedTrade) return "—";
            if (_cost.NetAvgCost is null) return "未填股数";
            return _cost.BreakEvenPrice() switch
            {
                null => "—",
                <= 0 => "已保本",
                var p => $"{p:F3}",
            };
        }
    }

    /// <summary>止亏价相对现价的位置——现价已经跌破止亏价就标红（再卖就是真亏钱了）。</summary>
    public Brush BreakEvenColor
        => _latestClose is > 0 && _cost.BreakEvenPrice() is { } p && _latestClose < p
            ? ThemeBrushes.Firebrick
            : ThemeBrushes.Foreground;

    /// <summary>成交明细的悬停提示——逐笔列出来（日期/方向/价格/股数/该笔费用），外加含费成本均价，
    /// 不用打开窗口也能核对。</summary>
    public string LotsTooltip
    {
        get
        {
            if (Entry.Lots.Count == 0) return "还没有成交记录——点【交易记录】录入买入/卖出（可分多笔）";
            var fees = _fees.Current;
            var lines = Entry.Lots.OrderBy(l => l.Date).Select(l =>
                $"{l.Date:yyyy-MM-dd}  {(l.Side == TradeSide.Buy ? "买入" : "卖出")}  {l.Price:F2}"
                + (l.Shares > 0
                    ? $" × {l.Shares:N0}股 = {l.Price * l.Shares:N0}元，费用{fees.FeeFor(l):N2}元"
                    : "（股数未填）")).ToList();
            if (_cost.NetAvgCost is { } cost)
                lines.Add($"—— 含费成本均价 {cost:F3}（买入总支出{_cost.NetCost:N0}元 ÷ {_cost.BuyShares:N0}股）");
            if (_cost.TotalFee > 0)
                lines.Add($"已发生费用合计 {_cost.TotalFee:N2}元" +
                          (_cost.BreakEvenPrice() is { } be and > 0 ? $"；止亏价 {be:F3}（已含卖出时还要交的费用）" : ""));
            lines.Add($"费率：{fees.Describe()}");
            return string.Join("\n", lines);
        }
    }

    private static string LotSummary(int count, int shares, double? avgPrice)
    {
        if (count == 0 || avgPrice is not (> 0)) return "—";
        return shares > 0
            ? $"{count}笔 {shares:N0}股 均{avgPrice.Value:F2}"
            : $"{count}笔 均{avgPrice.Value:F2}（股数未填）";
    }

    /// <summary>整体替换本条自选的成交明细（【交易记录】窗口点保存后调用）——落盘并刷新本行显示。</summary>
    public void ApplyLots(IReadOnlyList<TradeLot> lots)
    {
        _store.UpdateLots(Entry.Id, lots);
        Entry.Lots = lots.OrderBy(l => l.Date).ToList();
        Entry.SyncLegacyFromLots();
        _cost = TradeCostSummary.For(Entry.Lots, _fees.Current);
        Raise(nameof(BreakEvenText));
        Raise(nameof(BreakEvenColor));
        Raise(nameof(BuySummaryText));
        Raise(nameof(SellSummaryText));
        Raise(nameof(PositionText));
        Raise(nameof(LotsTooltip));
        Raise(nameof(HoldingText));
        Raise(nameof(HoldingColor));
        Raise(nameof(TradePoolText));
        Raise(nameof(TradePoolColor));
    }

    /// <summary>持仓盈亏——**扣光费用、并把没卖的部分按现价全部卖出**来算："现在全兑现，账户里到底
    /// 多出/少掉多少钱"。买入的佣金/过户费已经在成本里，卖出那头的佣金/过户费/印花税按现价再扣一遍；
    /// 分批卖过的，已经落袋的钱也算在里面。收益率按买入总支出算。
    ///
    /// 三种状态：没买过=观察中；已全部卖出=已平仓（就是最终已实现结果）；持仓中=按现价全卖的结果。
    /// 股数没填的老数据摊不出费用，退回纯价格口径并标注。
    ///
    /// 注意跟晨检那页的差别：晨检的止损/止盈线是**价格口径**（回测出来的纪律按价格算），这里是钱的口径。</summary>
    public string HoldingText
    {
        get
        {
            if (!Entry.HasBought) return "观察中";

            // 老数据只填了价格没填股数：摊不出每股费用，退回纯价格口径，并标出来别让人以为含费。
            if (_cost.NetAvgCost is null) return PriceOnlyHoldingText();

            if (Entry.IsClosedTrade)
            {
                var pnl = _cost.RealizedNet ?? 0;
                var pct = _cost.RealizedNetPct ?? 0;
                return $"已平仓 {(pnl >= 0 ? "+" : "")}{pnl:N0}元（{(pct >= 0 ? "+" : "")}{pct:F2}%，含费）";
            }

            if (_latestClose is not (> 0)) return "无最新价";

            var total = _cost.TotalPnlIfLiquidated(_latestClose.Value) ?? 0;
            var totalPct = _cost.TotalPnlPctIfLiquidated(_latestClose.Value) ?? 0;
            var text = $"{(total >= 0 ? "+" : "")}{total:N0}元（{(totalPct >= 0 ? "+" : "")}{totalPct:F2}%）";
            // 分批卖过的：顺带标一下其中已经落袋的部分，剩下的才是还浮着的。
            if (_cost.RealizedNet is { } r)
                text += $"，其中已落袋{(r >= 0 ? "+" : "")}{r:N0}元";
            return text;
        }
    }

    /// <summary>股数未知的老记录用的退化显示：只能按价格算涨跌幅，没法算费用和金额。</summary>
    private string PriceOnlyHoldingText()
    {
        if (Entry.IsClosedTrade)
        {
            var spct = Entry.RealizedPct ?? 0;
            return $"已平仓 {(spct >= 0 ? "+" : "")}{spct:F2}%（未填股数，不含费）";
        }
        if (_latestClose is not (> 0)) return "无最新价";
        var cost = Entry.AvgBuyPrice!.Value;
        var pct = (_latestClose.Value - cost) / cost * 100;
        return $"{(pct >= 0 ? "+" : "")}{pct:F2}%（未填股数，不含费）";
    }

    public Brush HoldingColor
    {
        get
        {
            if (!Entry.HasBought) return ThemeBrushes.Gray;
            if (Entry.IsClosedTrade)
                return (_cost.RealizedNetPct ?? Entry.RealizedPct) >= 0 ? ThemeBrushes.Red : ThemeBrushes.Green;
            if (_latestClose is not (> 0)) return ThemeBrushes.Gray;
            // 含费口径：按现价全卖是赚是亏（跟"现价 vs 止亏价"是同一回事）。
            var pnl = _cost.TotalPnlIfLiquidated(_latestClose.Value)
                      ?? (_latestClose.Value - (Entry.AvgBuyPrice ?? 0));
            return pnl >= 0 ? ThemeBrushes.Red : ThemeBrushes.Green;
        }
    }

    /// <summary>Plain mutable property, same reasoning as ResultRowViewModel.IsSelected — only
    /// read when "移除勾选" is clicked.</summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// "自选股（算法验证）" tab — 各选股方法丢进来的**样本池**，用途只有一个：跟踪这些票后来涨跌如何，
/// 统计各方法的准确率（见下方 MethodStatsText）。2026-07-31 起这里**不再录买卖信息**——那是"主动仓"
/// Tab 的事（<see cref="TradePoolTabViewModel"/>）；本页只看"选中后涨跌幅"。
///
/// 关键设计：一只票加入主动仓后**仍然留在本页**。否则"你挑走的正好是自己看好的那些"，剩下的样本
/// 就有了选择偏差，方法准确率会被系统性低估/高估——验证样本必须包含方法选出的全部票。本页因此显示
/// 的是全部自选记录，主动仓只是叠加在上面的一个标记（"在主动仓"列）。
///
/// Reads/writes JsonWatchlistStore, not the market database — this is the Analyzer's own state, not
/// Fetcher's shared read-only data.
/// </summary>
public class WatchlistTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly JsonWatchlistStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IBoardRepository _boardRepository;
    private readonly TradeFeeStore _fees;

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    private string _methodStatsText = "";
    /// <summary>各方法的准确率速览（只数 / 平均涨跌 / 上涨占比）——本页的核心产出。</summary>
    public string MethodStatsText
    {
        get => _methodStatsText;
        private set { _methodStatsText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodStatsText))); }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AddToTradePoolCommand { get; }

    /// <summary>加进去之后要通知"主动仓"页和晨检页重新加载——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    public WatchlistTabViewModel(JsonWatchlistStore store, IBarRepository barRepository, IBoardRepository boardRepository, TradeFeeStore fees)
    {
        _store = store;
        _barRepository = barRepository;
        _boardRepository = boardRepository;
        _fees = fees;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportWatchlist(Entries));
        AddToTradePoolCommand = new RelayCommand(_ => AddSelectedToTradePool());
        Reload();
    }

    /// <summary>Public so MainWindow can call it when the user switches to this tab — entries
    /// added from another tab in the same session otherwise wouldn't show up until "刷新" is
    /// clicked manually. Also re-triggers each row's "选中后表现"计算 with whatever is currently
    /// the latest local K线 data.</summary>
    public void Reload()
    {
        Entries.Clear();
        // 一次性反查"股票→所属概念板块"，每行直接取（没有板块数据时 map 为空，各行显示"—"）。
        var conceptMap = _boardRepository.GetConceptBoardsByStock();
        // 一次性取出"下一次财报日"（每只票取还没披露的最早那期），各行直接查表。
        // 抓取由 Fetcher 的【拉取财报预约日】负责，这里只读。
        var earnings = EarningsLookup.LoadUpcoming();
        foreach (var e in _store.Load().OrderByDescending(e => e.AddedAt))
        {
            var boards = conceptMap.TryGetValue(e.Code, out var list) && list.Count > 0
                ? string.Join("、", list)
                : "—";
            Entries.Add(new WatchlistRowViewModel(e, _barRepository, boards, _store, _fees,
                earnings.TryGetValue(e.Code, out var es) ? es : null));
        }
        BuildMethodStats();
    }

    /// <summary>按"来源方法"统计准确率——只数、平均"选中后涨跌幅"、上涨占比，按平均涨跌从高到低排。
    /// 口径说明：各方法的自选日期不同、持有时长不可比，这是粗对比；严格评测要固定同一时间窗口。</summary>
    private void BuildMethodStats()
    {
        var groups = Entries
            .Where(r => r.SincePickPct.HasValue)
            .GroupBy(r => r.Method)
            .Select(g => new
            {
                Method = g.Key,
                Count = g.Count(),
                Avg = g.Average(r => r.SincePickPct!.Value),
                Up = g.Count(r => r.SincePickPct!.Value >= 0),
            })
            .OrderByDescending(x => x.Avg)
            .ToList();

        if (groups.Count == 0)
        {
            MethodStatsText = "各方法准确率：还没有可统计的自选记录（从各选股方法勾选\"加入自选\"后这里会自动统计）";
            return;
        }

        var parts = groups.Select(x =>
            $"{x.Method} {x.Count}只 平均{(x.Avg >= 0 ? "+" : "")}{x.Avg:F1}% 胜率{(double)x.Up / x.Count * 100:F0}%");
        MethodStatsText = "各方法准确率（按平均涨跌排序）：" + string.Join("  ｜  ", parts);
    }

    private void RemoveSelected()
    {
        var toRemove = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (toRemove.Count == 0) return;
        _store.Remove(toRemove);
        Reload();
        TradePoolChanged?.Invoke();   // 删掉的可能正在主动仓里
    }

    /// <summary>把勾选的票加进"主动仓"页——买卖信息去那边录。本页仍然保留这些票（验证样本不能被挑走，
    /// 理由见类注释）。</summary>
    private void AddSelectedToTradePool()
    {
        var ids = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (ids.Count == 0) return;
        _store.SetTradePool(ids, true);
        foreach (var e in Entries) e.IsSelected = false;
        Reload();
        TradePoolChanged?.Invoke();
    }
}

/// <summary>
/// "主动仓" tab（2026-07-31新增）——**我打算买卖、要每天盯的那一小撮票**，跟"自选股（算法验证）"
/// 分开：买入/卖出只在这里录（2026-08-11起支持多笔，金字塔式建仓+分批止盈，见 <see cref="TradeLot"/>；
/// 列表显示的是汇总的总股数和加权均价），每日晨检也只体检这里的票。
///
/// 数据上不是另一份清单，而是同一个 watchlist.json 里 <see cref="WatchlistEntry.IsInTradePool"/>
/// 为真的那些记录（显式勾进来的 + 已经填了买入价的）——这样一只票"既是算法样本又是我的持仓"不需要
/// 存两份、也不会两边不同步。
///
/// 进入方式：①"自选股"页勾选后点"加入主动仓"；②"查询"页搜到后直接"加入主动仓"（手工看好的票）。
/// </summary>
public class TradePoolTabViewModel
{
    private readonly JsonWatchlistStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IBoardRepository _boardRepository;
    private readonly TradeFeeStore _fees;

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveFromPoolCommand { get; }
    public RelayCommand ExportCommand { get; }

    /// <summary>移出主动仓后要通知晨检页重新加载——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    public TradePoolTabViewModel(JsonWatchlistStore store, IBarRepository barRepository, IBoardRepository boardRepository, TradeFeeStore fees)
    {
        _store = store;
        _barRepository = barRepository;
        _boardRepository = boardRepository;
        _fees = fees;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveFromPoolCommand = new RelayCommand(_ => RemoveFromPool());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportWatchlist(Entries));
        Reload();
    }

    /// <summary>交易费率（佣金/过户费/印花税）——用户在【交易记录】窗口里填，那里就是录成交、看费用的
    /// 地方；本页只是取它来算含费盈亏和止亏价。MainWindow 打开窗口时要把它传进去。</summary>
    public TradeFeeStore FeeStore => _fees;

    public void Reload()
    {
        Entries.Clear();
        var conceptMap = _boardRepository.GetConceptBoardsByStock();
        // 一次性取出"下一次财报日"（每只票取还没披露的最早那期），各行直接查表。
        // 抓取由 Fetcher 的【拉取财报预约日】负责，这里只读。
        var earnings = EarningsLookup.LoadUpcoming();
        // 持仓中的排最前（真金白银的先看），然后已平仓，最后只是打算买的；同组按加入时间倒序。
        foreach (var e in _store.Load().Where(e => e.IsInTradePool)
                     .OrderByDescending(e => e.IsHoldingPosition)
                     .ThenByDescending(e => e.HasBought)
                     .ThenByDescending(e => e.AddedAt))
        {
            var boards = conceptMap.TryGetValue(e.Code, out var list) && list.Count > 0
                ? string.Join("、", list)
                : "—";
            Entries.Add(new WatchlistRowViewModel(e, _barRepository, boards, _store, _fees,
                earnings.TryGetValue(e.Code, out var es) ? es : null));
        }
    }

    /// <summary>把勾选的票移出主动仓（不删除记录，它仍留在"自选股"页作为算法样本，交易记录也不清空）。
    /// 只有**未平仓的持仓**移不出去——钱还在里面就必须每天盯；已平仓的可以移出（这笔交易已经结束，
    /// 没道理继续占着每天要看的清单）。有挡下的就如实提示，不静默失败。</summary>
    private void RemoveFromPool()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 未平仓持仓 = 买过、还没全部卖出（跟 WatchlistEntry.IsInTradePool 的第①条同一判定）
        var held = selected.Where(e => e.Entry.IsHoldingPosition).Select(e => e.Name).ToList();
        var movable = selected.Where(e => !e.Entry.IsHoldingPosition).Select(e => e.Entry.Id).ToList();
        if (movable.Count > 0) _store.SetTradePool(movable, false);
        Reload();
        TradePoolChanged?.Invoke();

        if (held.Count > 0)
            System.Windows.MessageBox.Show(
                $"已移出 {movable.Count} 只。\n\n以下 {held.Count} 只是未平仓的持仓，仍留在主动仓：\n{string.Join("、", held)}\n\n" +
                "钱还在里面就必须每天盯，所以不允许移出。要么在【交易记录】里把剩余股数都卖出（平仓后就能移出了），" +
                "要么把买入记录删掉（表示这笔其实没买）。",
                "部分未移出", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
