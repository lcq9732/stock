using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.FactorLab.Core;
using StockPlatform.Logic.Models;
using FLConfig = StockPlatform.FactorLab.Config;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"因子法"里因子表的一行。构造时只有元数据（不用跑评估就能看因子是什么、有什么用），
/// 评估跑完后带上 IC 等数字。</summary>
public class FactorRowViewModel
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Role { get; init; } = "";
    public string Formula { get; init; } = "";
    public string Direction { get; init; } = "";
    public string Description { get; init; } = "";
    public string DedupText { get; init; } = "";
    public string IcInText { get; init; } = "—";
    public string IcirInText { get; init; } = "—";
    public string IcNeuInText { get; init; } = "—";
    public string IcOutText { get; init; } = "—";
    public string LsAnnOutText { get; init; } = "—";
    public string TurnoverText { get; init; } = "—";
    public string YearlyIcText { get; init; } = "";
    /// <summary>右侧详情面板的完整说明文本。</summary>
    public string DetailText =>
        $"【{Name}】（{Category} / {Role}）\n\n构造：{Formula}\n\n方向：{Direction}\n\n作用：{Description}"
        + (YearlyIcText.Length > 0 ? $"\n\n逐年IC：{YearlyIcText}" : "")
        + (DedupText.Length > 0 ? $"\n\n去重：{DedupText}" : "");
}

/// <summary>"因子法"里最新名单的一行。</summary>
public class FactorPickRowViewModel : ISelectableRow
{
    public bool IsSelected { get; set; }
    public int Rank { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string ScoreText { get; init; } = "";
    public string ContribText { get; init; } = "";
    public string CloseText { get; init; } = "";
    public DateTime DataDate { get; init; }
    public double LatestClose { get; init; }
}

/// <summary>
/// "因子法" tab —— FactorLab 因子评估框架的界面入口（框架说明见 doc/factorlab-design.md）。
/// 打开就能看到全部因子及其作用说明（元数据不用跑评估）；点"运行因子选股"后台跑完整评估
/// （加载全量日线约1分钟），得到各因子 IC、去重结论和"合成-ICIR加权"的最新 Top50 名单。
/// 入选/方向/权重全由样本内（&lt;2025-07）统计决定，与控制台版 FactorLab 输出完全一致。
/// </summary>
public class FactorTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly AnalyzerPaths _paths;
    private readonly Watchlist.JsonWatchlistStore _watchlistStore;

    public ObservableCollection<FactorRowViewModel> FactorRows { get; } = new();
    public ObservableCollection<FactorPickRowViewModel> PickRows { get; } = new();

    private FactorRowViewModel? _selectedFactor;
    public FactorRowViewModel? SelectedFactor
    {
        get => _selectedFactor;
        set { Set(ref _selectedFactor, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFactorDetail))); }
    }
    public string SelectedFactorDetail => _selectedFactor?.DetailText ?? "点击左侧因子行查看构造公式和作用说明";

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

    private string _statusText = "点\"运行因子选股\"开始（需加载全量日线，约1分钟）；不运行也可以浏览因子说明";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _picksHeaderText = "最新名单（先运行因子选股）";
    public string PicksHeaderText { get => _picksHeaderText; set => Set(ref _picksHeaderText, value); }

    public RelayCommand RunCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }

    public FactorTabViewModel(AnalyzerPaths paths, Watchlist.JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _watchlistStore = watchlistStore;
        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning);
        AddToWatchlistCommand = new RelayCommand(_ => AddSelectedToWatchlist());

        // 未运行评估时也把因子元数据摆出来——"每个因子是什么、有什么用"不依赖回测
        foreach (var f in FactorRegistry.BuildAll())
            FactorRows.Add(new FactorRowViewModel
            {
                Name = f.Name, Category = f.Category, Role = FactorRegistry.RoleLabel(f.Role),
                Formula = f.Formula, Direction = f.Direction, Description = f.Description,
            });
    }

    private async Task RunAsync()
    {
        if (!File.Exists(_paths.TotalDb))
        {
            StatusText = "本地还没有数据文件，先把 Fetcher 产出的数据库拷贝过来";
            return;
        }

        IsRunning = true;
        var progress = new Progress<string>(s => StatusText = s);
        try
        {
            var outcome = await Task.Run(() => Evaluate(_paths.TotalDb, progress));

            FactorRows.Clear();
            foreach (var row in outcome.Factors) FactorRows.Add(row);
            PickRows.Clear();
            foreach (var row in outcome.Picks) PickRows.Add(row);

            PicksHeaderText = $"最新名单（{outcome.DataDate:yyyy-MM-dd} 收盘，合成-ICIR加权 Top{FLConfig.TopN}）——因子排序输出，不构成买入建议";
            StatusText = $"完成：{outcome.Factors.Count} 个因子（含合成），名单 {outcome.Picks.Count} 只；协议与局限见 doc/factorlab-design.md";
        }
        catch (Exception ex)
        {
            StatusText = $"运行失败：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private sealed record Outcome(List<FactorRowViewModel> Factors, List<FactorPickRowViewModel> Picks, DateOnly DataDate);

    /// <summary>后台线程：完整跑一遍 FactorLab 管线（与控制台版一致），返回界面行。</summary>
    private static Outcome Evaluate(string dbPath, IProgress<string> progress)
    {
        var md = MarketData.Load(dbPath, s => progress.Report("加载数据：" + s));

        progress.Report("构建每期收益与可交易掩码…");
        var shared = Evaluator.BuildShared(md);

        var factors = FactorRegistry.BuildAll();
        var results = new List<FactorResult>();
        for (int i = 0; i < factors.Count; i++)
        {
            progress.Report($"评估因子 {i + 1}/{factors.Count}：{factors[i].Name}");
            results.Add(Evaluator.Evaluate(factors[i], md, shared));
        }

        progress.Report("因子相关性与去重…");
        var corr = Evaluator.CorrelationMatrix(results, shared);
        var dedup = Evaluator.Deduplicate(results, corr);

        progress.Report("构建合成因子与最新名单…");
        var comp = CompositeFactor.Build("合成-ICIR加权", icirWeighted: true, results, dedup, md, shared);
        var compResult = Evaluator.Evaluate(comp, md, shared);
        var pickRows = Picks.Build(md, comp, compResult, results, FLConfig.TopN);

        var factorRows = new List<FactorRowViewModel>();
        var indexed = results.Select((r, i) => (r, i)).ToList();
        indexed.Add((compResult, -1)); // 合成因子放最后构建，展示时按|IC内|排序自然靠前
        foreach (var (r, i) in indexed.OrderByDescending(x => Math.Abs(x.r.IcIn.Mean)))
        {
            var f = r.Factor;
            string dedupText = f.Role != StockPlatform.FactorLab.Core.FactorRole.Candidate ? ""
                : dedup.GetValueOrDefault(i) is string dup ? $"与「{dup}」重复" : "保留";
            factorRows.Add(new FactorRowViewModel
            {
                Name = f.Name, Category = f.Category, Role = FactorRegistry.RoleLabel(f.Role),
                Formula = f.Formula, Direction = f.Direction, Description = f.Description,
                DedupText = dedupText,
                IcInText = r.IcIn.Mean.ToString("0.000"),
                IcirInText = r.IcIn.Icir.ToString("0.00"),
                IcNeuInText = r.IcNeuIn.Mean.ToString("0.000"),
                IcOutText = r.IcOut.Mean.ToString("0.000"),
                LsAnnOutText = double.IsNaN(r.LsAnnOut) ? "—" : r.LsAnnOut.ToString("+0.0%;-0.0%"),
                TurnoverText = double.IsNaN(r.AvgTurnover) ? "—" : r.AvgTurnover.ToString("0%"),
                YearlyIcText = string.Join("，", r.YearlyIc.Select(kv => $"{kv.Key}={kv.Value:0.000}")),
            });
        }

        var picks = pickRows.Select(p => new FactorPickRowViewModel
        {
            Rank = p.Rank,
            Code = p.Code,
            Name = p.Name,
            ScoreText = p.Score.ToString("0.000"),
            ContribText = p.TopContribsText,
            CloseText = double.IsNaN(p.LatestClose) ? "—" : p.LatestClose.ToString("0.00"),
            DataDate = p.DataDate.ToDateTime(TimeOnly.MinValue),
            LatestClose = p.LatestClose,
        }).ToList();

        return new Outcome(factorRows, picks, md.Dates[^1]);
    }

    /// <summary>"加入自选"——与其他方法Tab同语义（Code+Method+DataDate去重），Method="因子法"，
    /// 价格/日期用评估时的最新收盘。加入后进入"自选股"跟踪与"每日晨检"体检。</summary>
    private void AddSelectedToWatchlist()
    {
        var selected = PickRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "先勾选要加入自选的行";
            return;
        }

        var entries = selected.Select(r => new Watchlist.WatchlistEntry
        {
            Code = r.Code,
            Name = r.Name,
            Method = "因子法",
            Granularity = Granularity.Day,
            DataDate = r.DataDate,
            PriceAtPick = r.LatestClose,
            AddedAt = DateTime.Now,
            SatisfiedCount = 0,
            TotalCount = 0,
        }).ToList();

        int added = _watchlistStore.Add(entries);
        foreach (var r in selected) r.IsSelected = false;
        StatusText = added > 0
            ? $"已加入自选 {added} 只" + (added < entries.Count ? $"，{entries.Count - added} 只已在自选中" : "")
            : "勾选的股票都已经在自选里了";
    }
}
