using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// 【仓位计算器】窗口（2026-08-17新增）——把"我觉得它有几成把握涨"换算成"该买多少钱/该留多少股"。
/// 算法在 <see cref="KellyPositionSizer"/>（凯利公式＋半凯利折扣＋单票上限），这里只负责取值、
/// 实时重算和把结论讲清楚。
///
/// 两个入口共用这一个窗口：【主动仓】页顶部的按钮（空手起算），以及每行的【仓位】按钮（带上
/// 该股现价和当前持仓，直接给出"该减多少股"）。账户级参数（资金/折扣/上限）存
/// <c>data\position-sizing.json</c>，关窗时落盘。
/// </summary>
public partial class PositionSizingWindow : Window
{
    private readonly PositionSizingStore _store;
    private readonly double? _price;
    private readonly int _holdingShares;
    /// <summary>【从K线取值】要读这只票的日线来找波段——从"主动仓"某一行打开时才有；
    /// 顶部那个通用入口没有具体股票，按钮会禁用。</summary>
    private readonly IBarRepository? _barRepository;
    private readonly string? _code;

    // ── 【从K线取值】的锚点（2026-08-19新增）──
    // 斐波那契给出来的本质是**价格**（目标 13.71 / 止损 11.63 这样的档位线），"涨X%/跌Y%"只是
    // 相对当时现价的换算。所以取值后记住这两个价格，现价一改就按新现价把两个百分比重算一遍——
    // 否则百分比停在旧现价上，分批止盈计划里的目标价会变成"新现价×旧百分比"，悄悄脱离那条档位线
    // （用户 2026-08-19 指出的问题）。
    private double? _anchorTargetPrice;
    private double? _anchorStopPrice;
    /// <summary>锚点的来历（波段描述+档位名），重算时照原样带着，用户才知道这两个价是哪来的。</summary>
    private string _anchorSource = "";
    /// <summary>正在由锚点回填 Gain/Loss——期间不要把它当成"用户手动改了"而解除锚定。</summary>
    private bool _writingAnchor;

    /// <summary>带进来那个价格是**哪一天**的收盘价。盘中打开时今天的K线还没入库，带进来的就是
    /// 昨天的收盘——所以界面上不能光写"现价"，得把日期标出来让人自己决定要不要改成盘中价
    /// （用户 2026-08-19 指出）。</summary>
    private readonly DateTime? _priceDate;

    /// <summary>含费成本均价，**只显示不参与计算**（成本是沉没成本，见下面的说明文字）。
    /// 用处是让人看清"按建议减仓会实现多少盈亏""止损价是在成本之上还是之下"这类执行层面的事。</summary>
    private readonly double? _costBasis;

    /// <summary>账户费率（全程序共用那一份，见 TradeFeeStore）——只用来算"这笔卖出实际到手多少"，
    /// 跟仓位计算无关。</summary>
    private readonly TradeFeeSettings? _fees;

    public PositionSizingWindow(PositionSizingStore store, string? stockTitle = null,
        double? price = null, int holdingShares = 0,
        IBarRepository? barRepository = null, string? code = null,
        DateTime? priceDate = null, double? costBasis = null, TradeFeeSettings? fees = null)
    {
        InitializeComponent();
        _store = store;
        _price = price;
        _holdingShares = holdingShares;
        _barRepository = barRepository;
        _code = code;
        _priceDate = priceDate;
        _costBasis = costBasis;
        _fees = fees;

        TitleText.Text = stockTitle is { Length: > 0 } ? $"{stockTitle} — 仓位计算" : "仓位计算器";
        if (stockTitle is { Length: > 0 }) Title = $"仓位计算器 — {stockTitle}";

        // 没有具体股票（顶部通用入口）就没法从K线取值——禁用按钮并说明，而不是点了没反应。
        if (_barRepository == null || string.IsNullOrEmpty(_code))
        {
            FromChartButton.IsEnabled = false;
            FromChartInfoText.Text = "从【主动仓】某一行的【仓位】按钮打开，才能按那只票的K线自动取值。";
        }

        ShowPriceDateHint();
        ShowSettings();
        // 每个输入框改一个字就重算——这个窗口的价值就在于反复试参数（"概率再低5个点会怎样"）。
        foreach (var box in new[] { CapitalBox, MaxWeightBox, ProbBox, SharesBox })
            box.TextChanged += Input_Changed;
        // 现价改了要先按锚点把两个百分比重算，再走正常重算。
        PriceBox.TextChanged += Price_Changed;
        // 手动改目标/止损 = 用户自己拿主意，解除锚点，之后改现价不再自动动这两个值。
        GainBox.TextChanged += TargetOrStop_Changed;
        LossBox.TextChanged += TargetOrStop_Changed;

        Closing += (_, _) => SaveSettings();
        FitToScreen();
        Recalculate();
    }

    /// <summary>窗口默认开得比较大（结果分两栏铺开，就是为了不出滚动条）——但屏幕可能装不下，
    /// 超出工作区就收到工作区以内，并居中。这里不用 WindowStartupLocation 自己算：CenterOwner
    /// 是相对主窗口居中的，主窗口最大化时正好等于居中，改了尺寸后仍然对。</summary>
    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        if (Width > work.Width) Width = work.Width - 20;
        if (Height > work.Height) Height = work.Height - 20;
    }

    /// <summary>
    /// 把"这个价是哪天的"写在现价框旁边。带进来的是本地K线最新一根的收盘价——盘中打开程序时
    /// 今天的K线还没入库，那就是**昨天的收盘价**，界面上直接叫"现价"会让人以为是实时价
    /// （用户 2026-08-19 指出）。今天的日期就说"今日收盘"，更早的把日期和差了几个交易日都写出来。
    ///
    /// 顺便把含费成本均价也显示在这里（如果这只票已经买过）——它**不参与任何计算**，只是让人
    /// 在执行时心里有数，见下面那句说明。
    /// </summary>
    private void ShowPriceDateHint()
    {
        var parts = new List<string>();

        if (_priceDate is { } d)
        {
            int days = (int)(DateTime.Today - d.Date).TotalDays;
            parts.Add(days <= 0
                ? $"带入的是 {d:MM-dd} 收盘价"
                : $"⚠ 带入的是 {d:MM-dd} 的收盘价（{days} 天前），不是实时价——盘中请改成当前成交价");
        }
        else if (_price is > 0)
        {
            parts.Add("带入的是本地最新收盘价，不是实时价");
        }

        // 成本只作参考。放在这里而不是当输入项，是刻意的——见左下角那句"成本价不是输入项"。
        if (_costBasis is > 0)
            parts.Add($"你的含费成本 {_costBasis.Value:F3}（仅供参考，不参与仓位计算）");

        PriceHintText.Text = string.Join("；", parts);
    }

    private void ShowSettings()
    {
        var s = _store.Current;
        CapitalBox.Text = s.Capital > 0 ? s.Capital.ToString("0.##") : "";
        MaxWeightBox.Text = (s.MaxWeight * 100).ToString("0.##");
        GainBox.Text = s.GainPct.ToString("0.##");
        LossBox.Text = s.LossPct.ToString("0.##");
        ProbBox.Text = "";                      // 概率每次都要重新想，不给默认值免得顺手用了上次的
        PriceBox.Text = _price is > 0 ? _price.Value.ToString("F2") : "";
        SharesBox.Text = _holdingShares > 0 ? _holdingShares.ToString() : "0";

        FractionCombo.SelectedIndex = s.KellyFraction switch
        {
            <= 0.3 => 1,     // 1/4 凯利
            >= 0.9 => 2,     // 满凯利
            _ => 0,          // 半凯利
        };
    }

    private void SaveSettings()
    {
        var s = _store.Current;
        if (TryPositive(CapitalBox.Text, out var capital)) s.Capital = capital;
        if (TryPositive(MaxWeightBox.Text, out var maxWeight)) s.MaxWeight = Math.Clamp(maxWeight / 100, 0, 1);
        if (TryPositive(GainBox.Text, out var gain)) s.GainPct = gain;
        if (TryPositive(LossBox.Text, out var loss)) s.LossPct = loss;
        s.KellyFraction = SelectedFraction();
        _store.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── 从K线取值（斐波那契回撤位）──

    /// <summary>短线法回测出来的执行口径（见 ShortTermAnalysisEngine.TargetPct/StopPct）——
    /// 斐波那契取出来的值要跟它对照：那组参数是回测验证过的，斐波那契只是几何刻度，
    /// 两者差太多时该心里有数。这里只作展示对照，不参与计算。</summary>
    private const double ShortTermTargetPct = 10;
    private const double ShortTermStopPct = 10;

    /// <summary>止损放在支撑位下方的缓冲（%）——正好压在支撑价上会被日内噪音扫掉，
    /// 要跌破了才算这段支撑失效。</summary>
    private const double StopBufferPct = 1;

    /// <summary>取值时目标/止损档位离现价的最小距离（%）。现价常常正好贴着某一档：那种档位当目标
    /// 意味着"涨1.5%就走"（盈亏比根本不成立），当止损意味着一个正常波动就出局。所以决策口径要
    /// 跳过太近的档位取下一档；信息栏里"最近的支撑/阻力"仍如实显示，两者用途不同。</summary>
    private const double MinTargetGapPct = 3;
    private const double MinStopGapPct = 2;

    private void FromChart_Click(object sender, RoutedEventArgs e)
    {
        if (_barRepository == null || string.IsNullOrEmpty(_code)) return;

        var bars = _barRepository.Query(_code, Granularity.Day);
        if (bars.Count < 2)
        {
            FromChartInfoText.Text = "本地没有这只票的日线数据，取不了值。";
            return;
        }

        var swing = FibonacciRetracement.FindSwing(bars, FibonacciRetracement.ShortLookback);
        if (swing == null)
        {
            FromChartInfoText.Text =
                $"最近 {FibonacciRetracement.ShortLookback} 个交易日里没有幅度超过 {FibonacciRetracement.MinSwingPct:0}% 的波段"
                + "（横盘行情），画不出有意义的回撤位。可以在【行情详情】里勾上斐波那契、换更长的窗口或手动选点再看。";
            return;
        }

        // 现价优先用界面上填的（用户可能改成了盘中价），没填就用最新收盘。
        double px = TryPositive(PriceBox.Text, out var typed) && typed > 0 ? typed : bars[^1].Close;
        if (px <= 0)
        {
            FromChartInfoText.Text = "现价取不到，先在上面填一个现价。";
            return;
        }
        if (!TryPositive(PriceBox.Text, out var current) || current <= 0) PriceBox.Text = px.ToString("F2");

        // 决策口径：跳过紧贴现价的档位（理由见 MinTargetGapPct）。
        var support = FibonacciRetracement.SupportBelow(swing, px, MinStopGapPct);
        var resistance = FibonacciRetracement.ResistanceAbove(swing, px, MinTargetGapPct);
        if (support is not { } sup || resistance is not { } res)
        {
            FromChartInfoText.Text = support is null
                ? $"现价下方 {MinStopGapPct:0}% 以外没有可用的回撤位——要么已经跌破整段波段，要么紧贴着最低那档，"
                  + "这段行情的回撤位没法给出一个站得住的止损。"
                : $"现价上方 {MinTargetGapPct:0}% 以外没有可用的回撤位——已经站上这段波段的最高扩展位了，"
                  + "换更长的回看窗口再看。";
            return;
        }

        // 被跳过的那档（如果有）要说出来，否则用户对着图会疑惑"明明上面那条线更近，为什么没用它"。
        var nearestRes = FibonacciRetracement.ResistanceAbove(swing, px);
        var nearestSup = FibonacciRetracement.SupportBelow(swing, px);
        var skipped = new List<string>();
        if (nearestRes is { } nr && Math.Abs(nr.Price - res.Price) > 1e-9)
            skipped.Add($"上方 {nr.Label} {nr.Price:F2} 只高 {(nr.Price - px) / px * 100:F1}%");
        if (nearestSup is { } ns && Math.Abs(ns.Price - sup.Price) > 1e-9)
            skipped.Add($"下方 {ns.Label} {ns.Price:F2} 只低 {(px - ns.Price) / px * 100:F1}%");

        double stopPrice = sup.Price * (1 - StopBufferPct / 100);
        double lossPct = (px - stopPrice) / px * 100;
        double gainPct = (res.Price - px) / px * 100;

        // 锚在**价格**上，不是锚在百分比上：档位线不随现价漂移，现价一改就按新现价重算百分比。
        _anchorTargetPrice = res.Price;
        _anchorStopPrice = stopPrice;
        _anchorSource = $"目标={res.Label} {res.Price:F2}｜止损={sup.Label} {sup.Price:F2} 下方{StopBufferPct:0}%";
        WriteAnchorPercents(px, gainPct, lossPct);

        var notes = new List<string>
        {
            $"{swing.Describe()}；现价 {px:F2} 已{(swing.IsUpSwing ? "回撤" : "反弹")} "
                + $"{FibonacciRetracement.RetracedPct(swing, px):F1}%",
            $"目标 {res.Label} {res.Price:F2}（+{gainPct:F1}%）｜"
                + $"止损 {sup.Label} {sup.Price:F2} 下方{StopBufferPct:0}% = {stopPrice:F2}（−{lossPct:F1}%）",
            $"对照短线法回测口径 +{ShortTermTargetPct:0}%/−{ShortTermStopPct:0}%（4.62%、胜率73.2%）",
        };

        if (skipped.Count > 0)
            notes.Add($"（跳过太近的档位：{string.Join("、", skipped)}）");

        // 差太多要明说。回撤位是几何刻度、没有回测背书，跟验证过的纪律冲突时该以纪律为准。
        if (Math.Abs(lossPct - ShortTermStopPct) > 5 || Math.Abs(gainPct - ShortTermTargetPct) > 5)
            notes.Add("⚠ 跟回测口径差得较远。斐波那契只是几何刻度、没有回测背书，"
                    + "偏离太多时建议仍按 ±10% 执行。");

        FromChartInfoText.Text = string.Join("\n", notes);
        Recalculate();
    }

    private void Input_Changed(object sender, EventArgs e) => Recalculate();

    /// <summary>改现价：先按锚点（如果有）把目标/止损两个百分比重算，再走正常重算。</summary>
    private void Price_Changed(object sender, EventArgs e)
    {
        ReanchorToCurrentPrice();
        Recalculate();
    }

    /// <summary>用户手动改了目标/止损——解除锚点：从此这两个数由他自己负责，改现价不再自动动它们。
    /// 程序自己回填时（<see cref="_writingAnchor"/>）不算。</summary>
    private void TargetOrStop_Changed(object sender, EventArgs e)
    {
        if (!_writingAnchor && _anchorTargetPrice != null)
        {
            _anchorTargetPrice = null;
            _anchorStopPrice = null;
            FromChartInfoText.Text = "已改为手动填写的目标/止损，不再跟随现价自动重算。"
                                   + "要回到按K线档位算，重新点一次【从K线取值】。";
        }
        Recalculate();
    }

    /// <summary>把锚定的两个价位按给定现价换算成百分比写进输入框（期间抑制"用户手动改"的判定）。</summary>
    private void WriteAnchorPercents(double px, double gainPct, double lossPct)
    {
        _writingAnchor = true;
        GainBox.Text = gainPct.ToString("0.##");
        LossBox.Text = lossPct.ToString("0.##");
        _writingAnchor = false;
    }

    /// <summary>
    /// 现价变了以后，按锚定的目标价/止损价重新算出"对了能涨%/错了止损%"。
    ///
    /// 这是【从K线取值】真正的语义：斐波那契给的是两条价格线，百分比只是相对现价的换算。不这么做的话
    /// 百分比会停在取值那一刻的现价上，分批止盈计划里的目标价变成"新现价×旧百分比"，跟那条档位线对不上。
    /// 现价一旦涨过目标价或跌破止损价，锚点就失效了（那两条线已经不在现价两侧），提示重新取值。
    /// </summary>
    private void ReanchorToCurrentPrice()
    {
        if (_anchorTargetPrice is not { } target || _anchorStopPrice is not { } stop) return;
        if (!TryPositive(PriceBox.Text, out var px) || px <= 0) return;

        if (px >= target || px <= stop)
        {
            _anchorTargetPrice = null;
            _anchorStopPrice = null;
            FromChartInfoText.Text =
                $"现价 {px:F2} 已经{(px >= target ? $"到/超过目标价 {target:F2}" : $"跌破止损价 {stop:F2}")}，"
                + "原来那两条档位线不再一上一下夹着现价，取值失效——请重新点【从K线取值】按新位置算。";
            return;
        }

        double gainPct = (target - px) / px * 100;
        double lossPct = (px - stop) / px * 100;
        WriteAnchorPercents(px, gainPct, lossPct);

        FromChartInfoText.Text =
            $"现价改成 {px:F2}，已按锚定的档位线重算：{_anchorSource}\n"
            + $"目标 {target:F2}（+{gainPct:F1}%）｜止损 {stop:F2}（−{lossPct:F1}%）——"
            + "两条线是固定价位，不随现价漂移，所以百分比会跟着现价变。";
    }

    /// <summary>凯利折扣下拉——XAML 直接绑的是这个（事件处理器的签名要跟委托一致，不能复用
    /// <see cref="Input_Changed"/> 那个宽签名）。</summary>
    private void Fraction_Changed(object sender, SelectionChangedEventArgs e) => Recalculate();

    private double SelectedFraction()
        => FractionCombo.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, out var f)
            ? f
            : 0.5;

    private static bool TryPositive(string? text, out double value)
        => double.TryParse((text ?? "").Trim(), out value) && value >= 0;

    /// <summary>金额的统一写法：上万了顺带标一个"万元"，不然一串零很难一眼读出量级。</summary>
    private static string Money(double amount)
        => Math.Abs(amount) >= 10000
            ? $"{amount:N0}元（{amount / 10000:F1}万）"
            : $"{amount:N0}元";

    private void Recalculate()
    {
        // 界面控件在 InitializeComponent 之前不存在——构造里挂事件之后才会走到这里，不用额外判空。
        double fraction = SelectedFraction();
        bool hasProb = TryPositive(ProbBox.Text, out var probPct) && probPct > 0;
        TryPositive(CapitalBox.Text, out var capital);
        TryPositive(GainBox.Text, out var gain);
        TryPositive(LossBox.Text, out var loss);
        TryPositive(MaxWeightBox.Text, out var maxWeightPct);
        double? price = TryPositive(PriceBox.Text, out var p) && p > 0 ? p : null;
        int shares = int.TryParse((SharesBox.Text ?? "").Trim(), out var sh) && sh > 0 ? sh : 0;

        if (!hasProb || capital <= 0 || gain <= 0 || loss <= 0)
        {
            HeadlineText.Text = "先把左边填全";
            ActionText.Text = "";
            WarnText.Text = "";
            DetailText.Text = "需要：可投资总资金、上涨概率、对了能涨多少、错了在哪止损。"
                            + "\n止损那一项不能省——没有它就没有盈亏比，也就算不出仓位。";
            PlanText.Text = "—";
            SensitivityText.Text = "—";
            return;
        }

        var input = new PositionSizingInput
        {
            WinProbability = Math.Min(probPct, 100) / 100,
            GainPct = gain,
            LossPct = loss,
            Capital = capital,
            KellyFraction = fraction,
            MaxWeight = maxWeightPct / 100,
        };
        var r = KellyPositionSizer.Calculate(input);

        ShowHeadline(r, price);
        ShowAction(r, price, shares);
        ShowDetail(r, input);
        ShowPlan(r, input, price, shares);
        ShowSensitivity(r, input);
    }

    private void ShowHeadline(PositionSizingResult r, double? price)
    {
        if (r.Binding == SizingBinding.NoEdge)
        {
            HeadlineText.Text = "建议仓位 0 —— 这笔不值得做";
            return;
        }

        var text = $"建议持有 {Money(r.Amount)}，占总资金 {r.FinalWeight * 100:F1}%";
        if (price is > 0)
        {
            int lots = TargetShares(r.Amount, price.Value);
            text += $"\n≈ {lots:N0} 股（{lots / 100} 手，按 {price.Value:F2} 元算）";
        }
        HeadlineText.Text = text;
    }

    /// <summary>建议金额折成股数——A股按100股一手，向下取整到整手（宁可少买一手，不要凑不满）。</summary>
    private static int TargetShares(double amount, double price) => (int)(amount / price / 100) * 100;

    private void ShowAction(PositionSizingResult r, double? price, int shares)
    {
        RealizedHintText.Text = "";
        if (price is not > 0)
        {
            ActionText.Text = "填上现价，就会算出该买/该卖多少股。";
            ActionText.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }

        int target = TargetShares(r.Amount, price.Value);
        ShowRealizedHint(price.Value, shares, target);
        int delta = target - shares;
        double currentValue = shares * price.Value;

        if (shares == 0)
        {
            ActionText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ActionText.Text = target > 0
                ? $"还没买 → 买入 {target:N0} 股（约 {Money(target * price.Value)}）"
                : "还没买 → 这个价位下建议的仓位不足一手，不用买";
            return;
        }

        // 差不到一手就别动——为了几十股来回交易，手续费和心力都不划算。
        if (Math.Abs(delta) < 100)
        {
            ActionText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ActionText.Text = $"现在拿着 {shares:N0} 股（{Money(currentValue)}）——跟建议仓位差不多，不用动。";
            return;
        }

        if (delta < 0)
        {
            int sell = -delta;
            ActionText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ActionText.Text = $"现在拿着 {shares:N0} 股（{Money(currentValue)}）→ 卖出 {sell:N0} 股，"
                            + $"留 {target:N0} 股（约 {Money(target * price.Value)}）";
        }
        else
        {
            ActionText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ActionText.Text = $"现在拿着 {shares:N0} 股（{Money(currentValue)}）→ 还可以再买 {delta:N0} 股，"
                            + $"加到 {target:N0} 股（约 {Money(target * price.Value)}）";
        }
    }

    /// <summary>
    /// "按这个建议减仓，会实现多少盈亏"（2026-08-19新增，用户要求）——卖出金额扣掉卖出那头的
    /// 佣金/过户费/印花税，再减去这些股票的含费成本，跟【交易记录】窗口是同一套口径。
    ///
    /// **它只是记账信息，刻意不参与任何计算**：仓位该多大只取决于从现在往后的概率和赔率。这个
    /// 数字最容易被拿来倒过来做决定（"还亏着，等回本再卖"），所以后面跟了一句话点破这件事。
    /// 只有"要卖"时才显示——加仓不产生已实现盈亏。
    /// </summary>
    private void ShowRealizedHint(double px, int currentShares, int targetShares)
    {
        if (_costBasis is not { } cost || cost <= 0 || _fees == null || currentShares <= 0) return;

        int sell = currentShares - targetShares;
        if (sell < 100) return;   // 不卖，或不足一手（一手以下的差额本来也不建议动）

        double amount = sell * px;
        double fee = _fees.FeeFor(amount, TradeSide.Sell);
        // 成本用的是**含费成本均价**（买入总支出÷买入股数），所以这里只需再扣卖出那头的费用。
        double realized = amount - fee - sell * cost;
        double pct = realized / (sell * cost) * 100;

        RealizedHintText.Text =
            $"这笔卖出 {sell:N0} 股按 {px:F2} 成交，将实现 {realized:+#,0;-#,0} 元"
            + $"（{pct:+0.0;-0.0}%，已扣卖出费用 {fee:N0} 元，成本按含费均价 {cost:F3}）"
            + "　※ 记账用，不该拿它决定卖不卖——赚了才肯卖、亏了就死扛，正是要避免的那种决策。";
        RealizedHintText.Foreground = realized >= 0
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.SeaGreen;
    }

    private void ShowDetail(PositionSizingResult r, PositionSizingInput input)
    {
        double p = input.WinProbability * 100;
        var lines = new List<string>
        {
            $"盈亏比 b = {input.GainPct:0.##} ÷ {input.LossPct:0.##} = {r.Odds:F2}",
            $"期望收益 {r.ExpectedReturnPct:+0.00;-0.00}%/元"
                + $"（{p:0.##}%×+{input.GainPct:0.##}% − {100 - p:0.##}%×−{input.LossPct:0.##}%）",
            $"满凯利 (p·b−q)÷b = {r.FullKelly * 100:F1}%  ×折扣{input.KellyFraction:0.##} = {r.ScaledKelly * 100:F1}%"
                + $"  →上限{input.MaxWeight * 100:0.##}%后 = {r.FinalWeight * 100:F1}%",
            $"一次止损的代价：总资金 {r.DrawdownPctOfCapital:F2}%"
                + $"（≈ {Money(r.DrawdownPctOfCapital / 100 * input.Capital)}）",
            $"保本概率 {r.BreakEvenProbability * 100:F1}%——低于它就是负期望，该 0 仓位",
        };
        DetailText.Text = string.Join("\n", lines);

        WarnText.Text = r.Binding switch
        {
            SizingBinding.NoEdge =>
                $"⚠ 按你填的数，这笔交易期望收益是 {r.ExpectedReturnPct:+0.00;-0.00}%——赔率不够补偿胜率。"
                + $"要么上涨概率得高于 {r.BreakEvenProbability * 100:F1}%，要么止损收窄/目标放大，否则不该有仓位。",
            SizingBinding.MaxWeightCap =>
                $"ⓘ 公式算出来是 {r.ScaledKelly * 100:F1}%，被单票上限 {input.MaxWeight * 100:0.##}% 压下来了。"
                + "这是有意的：再好的判断也不该把身家押在一只票上。",
            _ => "",
        };
    }

    private void ShowPlan(PositionSizingResult r, PositionSizingInput input, double? price, int shares)
    {
        if (price is not > 0)
        {
            PlanText.Text = "填上现价，这里会给出止损价和两档止盈价。";
            return;
        }

        double px = price.Value;
        // 计划按**调整到建议仓位之后**的股数排，不是按现在拿着的——上面刚说了该减到多少，
        // 这里再按原仓位排就自相矛盾了。
        int planShares = TargetShares(r.Amount, px);
        if (planShares <= 0)
        {
            PlanText.Text = shares > 0
                ? $"建议仓位为 0——{shares:N0} 股全部卖出，没有需要排的止盈计划。"
                : "建议仓位为 0，没有可执行的止盈计划。";
            return;
        }

        int third = Math.Max(planShares / 3 / 100 * 100, 100);   // 取整到手，至少一手
        double stop = px * (1 - input.LossPct / 100);
        double t1 = px * (1 + input.GainPct / 200);              // 一半的目标涨幅
        double t2 = px * (1 + input.GainPct / 100);

        PlanText.Text = string.Join("\n", new[]
        {
            shares == planShares
                ? $"按现在的 {planShares:N0} 股排："
                : $"按调整到位后的 {planShares:N0} 股排（先按左边那句加/减）：",
            $"止损 {stop:F2}（−{input.LossPct:0.##}%）：无条件走人，"
                + $"从现价算起亏 {Money(planShares * (px - stop))}。",
            $"目标① {t1:F2}（+{input.GainPct / 2:0.##}%）：卖 {third:N0} 股（约1/3）。",
            $"目标② {t2:F2}（+{input.GainPct:0.##}%）：再卖 {third:N0} 股。",
            $"剩 {Math.Max(planShares - 2 * third, 0):N0} 股：移动止盈（跌破20日线或从高点回撤10%）。",
            "—— 事前写死这四行，比盘中临时估概率靠谱。",
        });
    }

    private void ShowSensitivity(PositionSizingResult r, PositionSizingInput input)
    {
        double p = input.WinProbability * 100;
        double pLow = Math.Max(p - 10, 0);
        double shrink = Math.Max(r.FinalWeight - r.FinalWeightIfOverconfident, 0) * 100;
        var lines = new List<string>
        {
            $"真实概率若只有 {pLow:0.##}%（你填 {p:0.##}%，估高10个点很常见），"
                + $"仓位应为 {r.FinalWeightIfOverconfident * 100:F1}%"
                + $"（≈ {Money(r.FinalWeightIfOverconfident * input.Capital)}），"
                + (shrink >= 0.05 ? $"少 {shrink:F1} 个百分点。"
                    // 两边一样，但原因有两种，别混为一谈：本来就是0仓位 vs 都顶在单票上限上。
                    : r.FinalWeight <= 0 ? "——本来就该是 0 仓位，概率再低也一样。"
                    : "跟上面一样——单票上限已经先一步把它压住了，这道硬线本身就是对估高概率的保护。"),
            $"低于 {r.BreakEvenProbability * 100:F1}% 就完全不该有仓位。",
            "凯利对高估极敏感：按满凯利下注而真实胜率偏低，长期收益直接转负——这就是默认半凯利的原因。",
        };
        SensitivityText.Text = string.Join("\n", lines);
    }
}
