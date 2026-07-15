using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>板块榜里的一行（概念/行业）。涨跌幅、成交额、领涨股来自新浪板块口径（Fetcher 抓的
/// 快照），成分股数也来自快照。</summary>
public class BoardRowViewModel
{
    public string BoardCode { get; init; } = "";
    public string Name { get; init; } = "";
    public int MemberCount { get; init; }
    public double ChangePct { get; init; }
    public double Amount { get; init; }
    public string LeaderName { get; init; } = "";

    public string ChangePctText => $"{(ChangePct >= 0 ? "+" : "")}{ChangePct:F2}%";
    public Brush ChangeColor => ChangePct >= 0 ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0, 180, 0));
    /// <summary>成交额（亿元）——数值列，单位放在表头，这样点表头能按数字排序（不是按"592.5亿"这种
    /// 字符串排）。</summary>
    public double AmountYi => Amount / 1e8;
}

/// <summary>点开某个板块后，它的成分股一行——名称来自本地 StockMeta，最新涨跌幅用本地日K现算。</summary>
public class BoardMemberRowViewModel
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public double ChangePct { get; init; }
    public bool HasQuote { get; init; }

    public string ChangePctText => HasQuote ? $"{(ChangePct >= 0 ? "+" : "")}{ChangePct:F2}%" : "—";
    public Brush ChangeColor => !HasQuote ? Brushes.Gray : ChangePct >= 0 ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0, 180, 0));
}

/// <summary>"板块热度" tab —— 展示 Fetcher 抓来的概念/题材板块和行业板块行情榜（按涨跌幅从高到低），
/// 点某个板块列出其成分股，成分股可再点"K线详情"看行情。数据是"截至上次拉取"的快照（见
/// IBoardRepository / FetchOrchestrator.RunFetchBoardsAsync），跟其它 Tab 一样离线读本地库。</summary>
public class BoardTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly IBoardRepository _boardRepository;
    private readonly IBarRepository _barRepository;
    private readonly AnalyzerPaths _paths;
    private Dictionary<string, string>? _names;

    public ObservableCollection<BoardRowViewModel> Boards { get; } = new();
    public ObservableCollection<BoardMemberRowViewModel> Members { get; } = new();

    // 当前类型(概念/行业)的全量板块，Boards 是按 FilterText 过滤后的展示子集。
    private readonly List<BoardRowViewModel> _all = new();

    private string _filterText = "";
    /// <summary>板块名/领涨股关键字过滤（边打边筛）——板块几百个，靠这个快速定位。</summary>
    public string FilterText
    {
        get => _filterText;
        set { if (_filterText == value) return; _filterText = value; Raise(nameof(FilterText)); ApplyFilter(); }
    }

    private bool _showConcept = true;
    /// <summary>true=概念/题材，false=行业。绑到两个单选按钮（见 MainWindow.xaml）。</summary>
    public bool ShowConcept
    {
        get => _showConcept;
        set { if (_showConcept == value) return; _showConcept = value; Raise(nameof(ShowConcept)); Raise(nameof(ShowIndustry)); LoadBoards(); }
    }

    /// <summary>行业单选按钮的镜像属性（跟 ShowConcept 互斥），省得为两个 RadioButton 写反转转换器。</summary>
    public bool ShowIndustry
    {
        get => !_showConcept;
        set => ShowConcept = !value;
    }

    private BoardRowViewModel? _selectedBoard;
    public BoardRowViewModel? SelectedBoard
    {
        get => _selectedBoard;
        set { _selectedBoard = value; Raise(nameof(SelectedBoard)); LoadMembers(); }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set { _statusText = value; Raise(nameof(StatusText)); } }

    private string _membersHeader = "点左侧板块查看成分股";
    public string MembersHeader { get => _membersHeader; private set { _membersHeader = value; Raise(nameof(MembersHeader)); } }

    public RelayCommand RefreshCommand { get; }

    public BoardTabViewModel(IBoardRepository boardRepository, IBarRepository barRepository, AnalyzerPaths paths)
    {
        _boardRepository = boardRepository;
        _barRepository = barRepository;
        _paths = paths;
        RefreshCommand = new RelayCommand(_ => { _names = null; LoadBoards(); });
        LoadBoards();
    }

    private void LoadBoards()
    {
        _all.Clear();
        Boards.Clear();
        Members.Clear();
        MembersHeader = "点左侧板块查看成分股";

        var asOf = _boardRepository.GetLatestAsOf();
        if (asOf == null)
        {
            StatusText = "本地还没有板块数据——请在 Fetcher 里点\"拉取板块\"，再把数据库拷贝过来后点\"刷新\"";
            return;
        }

        var type = _showConcept ? BoardType.Concept : BoardType.Industry;
        foreach (var b in _boardRepository.QueryBoards(type))
            _all.Add(new BoardRowViewModel
            {
                BoardCode = b.BoardCode,
                Name = b.Name,
                MemberCount = b.MemberCount,
                ChangePct = b.ChangePct,
                Amount = b.Amount,
                LeaderName = b.LeaderName,
            });

        _boardTypeLabel = _showConcept ? "概念/题材" : "行业";
        _asOf = asOf.Value;
        ApplyFilter();
    }

    private string _boardTypeLabel = "";
    private DateTime _asOf;

    private void ApplyFilter()
    {
        Boards.Clear();
        var q = (_filterText ?? "").Trim();
        var shown = _all.Where(b => q.Length == 0
            || b.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || b.LeaderName.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var b in shown) Boards.Add(b);

        StatusText = q.Length == 0
            ? $"{_boardTypeLabel}板块 {_all.Count} 个，按涨跌幅从高到低；数据截至 {_asOf:yyyy-MM-dd HH:mm}"
            : $"{_boardTypeLabel}板块 命中 {Boards.Count}/{_all.Count} 个（关键字\"{q}\"）；数据截至 {_asOf:yyyy-MM-dd HH:mm}";
    }

    private void LoadMembers()
    {
        Members.Clear();
        if (_selectedBoard == null) { MembersHeader = "点左侧板块查看成分股"; return; }

        _names ??= SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);
        var codes = _boardRepository.QueryMembers(_selectedBoard.BoardCode);

        var rows = new List<BoardMemberRowViewModel>();
        foreach (var code in codes)
        {
            var bars = _barRepository.Query(code, Granularity.Day);
            bool hasQuote = bars.Count > 0;
            // 用最近两天收盘价现算涨跌幅，不读库里存的 pct_chg——单日/增量入库的那一列常年是0
            // （抓取器按日期截窗后再算 pct，单日窗口没有前一天可比，见 TencentBarFetcher/SinaBarFetcher）。
            double chg = 0;
            if (bars.Count >= 2 && bars[^2].Close > 0)
                chg = (bars[^1].Close - bars[^2].Close) / bars[^2].Close * 100;
            rows.Add(new BoardMemberRowViewModel
            {
                Code = code,
                Name = _names.GetValueOrDefault(code, code),
                ChangePct = chg,
                HasQuote = hasQuote,
            });
        }
        foreach (var r in rows.OrderByDescending(r => r.HasQuote).ThenByDescending(r => r.ChangePct))
            Members.Add(r);

        MembersHeader = $"「{_selectedBoard.Name}」成分股 {Members.Count} 只（按最新涨跌幅排序）";
    }
}
