using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// 界面上的一行观察项。
///
/// ⚠ **故意不带 取值器/参数/来源/仓位**（2026-09-11 按用户反馈砍掉）：它们是实现细节，
/// 而且信息本来就已经在 <see cref="Reason"/> 那句话里了——
/// "L0：限售解禁"＝取值器+参数，"主动仓：跌破 MA20"＝仓位，
/// "回购方案进行中（价格上限 573 元）"一看就是规则算出来的。
/// 再单独列一遍只会把真正要读的那列挤窄。
/// </summary>
public class WatchItemRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";

    /// <summary>事项名称（"L0：限售解禁"）。界面显示的是 <see cref="Display"/>。</summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// 这件事**当前已知的内容**——公告发布时就写明的那些数（解禁多少股、预告预增多少）。
    /// 取不到就是空。
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// 左表那一列：**日期 + 事项 + 公告里的数据**，例如
    /// <c>2026-11-08（还有 58 天）限售解禁 100 万股，占流通 2%</c>。
    ///
    /// ⚠ 日期排在最前面（2026-09-11 按用户给的样例定的）：左表是一份待办，
    /// 人先看"什么时候"再看"什么事"，日期埋在句子中间就得逐行读完才能排优先级。
    ///
    /// ⚠ 这一列只放**公告发布时就写明的数据**，返工过两次：
    ///   · 「解禁 100 万股」「预告预增 54%」——公告里写着的，是"要跟踪的是什么事"的一部分；
    ///   · 「已回购 80.2 亿元」——**随时间变的进展**，归右表。
    /// 判据是"这个数会不会随时间变"：不变的是事项内容，会变的是进展。
    ///
    /// 本来就没有数据的事项（跌破 MA20）就只有名字，不编。
    /// </summary>
    public string Display => Logic.Services.WatchItemDisplay.Compose(Reason, Detail);

    /// <summary>L2 手写项才有。</summary>
    public string Thesis { get; init; } = "";
}

/// <summary>
/// 界面上的一条进展。
/// 事项名和进展分成两列：拼一起的话事项名会把数据挤出显示宽度，
/// 进展看着就"笼统"（2026-09-11 用户反馈）。
/// </summary>
public class WatchHitRow : INotifyPropertyChanged
{
    /// <summary>对应落库那条触发记录；进展是实时算的，靠 WatchService 回填。
    /// 为空表示这条进展还没被记成触发（没进窗口），标记不了。</summary>
    public Guid HitId { get; init; }

    private bool _handled;
    /// <summary>人已经看过/处理过。勾掉之后默认列表里就不再显示它。</summary>
    public bool Handled
    {
        get => _handled;
        set { _handled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Handled))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Date { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";

    /// <summary>哪件事（＝左表那条事项）。</summary>
    public string ItemName { get; init; } = "";

    /// <summary>进展本身，用数据说话。</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// 【观察项】页（2026-09-11），见 doc/watch-item-design.md M3。
///
/// 这一页的产出是**触发**，不是清单——清单只是让人能看见"现在在盯什么、为什么盯"。
/// 点【重算并求值】＝跑一遍 <see cref="WatchService.Run"/>：
/// 重算派生项（挂/摘）→ 逐条求值 → 记录新触发。
///
/// ════ 「个人观点」写在个股笔记里 ════
/// 那一列的内容来自 <c>notes/{code}.md</c> 里以「观点：」开头的那一行，点行尾的 📝 就能编辑
/// （2026-09-11 加的入口）。为什么不做成表格里直接填：
///   · 笔记本来就是判断的去处——"数据能重算，判断不能"（见 AnalyzerPaths.NotesDir）；
///   · "要写成一段话存进文件"这件事本身是个门槛，能拦住随手许愿。观察项要过
///     "可判定／能改变动作／有归属层"三条准入（设计文档 §1），做成随手能填的输入框就成许愿池了。
///
/// ⚠ 观察项本身（挂什么、判据是什么）仍然只读：派生项由规则维护，
/// 手写观察项目前要编辑 <c>watch/items.json</c>。
/// </summary>
public class WatchTabViewModel : INotifyPropertyChanged
{
    private readonly WatchService _service;
    private readonly StockNoteStore _notes;

    /// <summary>上一轮求出的事项内容（观察项 Id → 人话）。
    /// 【刷新】只读文件不重算，沿用上一轮的；【重算并求值】会刷新它。</summary>
    private IReadOnlyDictionary<Guid, string> _detail = new Dictionary<Guid, string>();

    /// <summary>按实际的量重算出来的档位（解禁 74 股不该标 A）。见 WatchPriority。</summary>
    private IReadOnlyDictionary<Guid, string> _priority = new Dictionary<Guid, string>();

    public ObservableCollection<WatchItemRow> Items { get; } = [];
    public ObservableCollection<WatchHitRow> Hits { get; } = [];

    private string _status = "点【重算并求值】开始。";
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    private string _summary = "";
    public string Summary
    {
        get => _summary;
        private set { _summary = value; OnPropertyChanged(); }
    }

    public ICommand RunCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>把右表里勾上的那些标成已处理。</summary>
    public ICommand MarkHandledCommand { get; }

    /// <summary>把勾上的取消已处理（标错了能撤）。</summary>
    public ICommand UnmarkHandledCommand { get; }

    private bool _hideHandled = true;
    /// <summary>默认把处理过的藏起来——右表的意义是"还有什么要管"，不是历史档案。</summary>
    public bool HideHandled
    {
        get => _hideHandled;
        set { _hideHandled = value; OnPropertyChanged(); FillHits(_lastProgress); }
    }

    /// <summary>上一轮算出的进展，切换"只看未处理"时不用重算。</summary>
    private IReadOnlyList<WatchHit> _lastProgress = [];

    public WatchTabViewModel(WatchService service, StockNoteStore notes)
    {
        _service = service;
        _notes = notes;
        RunCommand = new RelayCommand(_ => Run());
        RefreshCommand = new RelayCommand(_ => Refresh());
        MarkHandledCommand = new RelayCommand(_ => SetHandled(true));
        UnmarkHandledCommand = new RelayCommand(_ => SetHandled(false));
    }

    /// <summary>只读当前状态，不重算——启动时和切页时用。</summary>
    public void Refresh()
    {
        try
        {
            FillItems(_service.LoadItems());
            FillHits(_service.LoadHits(DateTime.Today.Year));
            Status = $"当前 {Items.Count} 条观察项，{Hits.Count} 条触发记录（本年）。";
        }
        catch (Exception ex)
        {
            Status = $"读取失败：{ex.Message}";
        }
    }

    private void Run()
    {
        try
        {
            Status = "重算中…";
            var r = _service.Run();
            _detail = r.Detail;            // 必须在 FillItems 之前赋值
            _priority = r.Priority;

            FillItems(_service.LoadItems());
            FillHits(r.Progress);          // 右表＝每个事项的最近进展，不是触发流水

            Summary = $"观察项 {r.ItemCount} 条（新挂 {r.Added}、摘掉 {r.Expired}）；"
                      + $"求值 {r.Evaluated} 条，命中 {r.Hits} 条，其中新记录 {r.NewHits} 条。";
            Status = r.Hits == 0
                ? "跑完了：没有任何观察项触发。"
                : $"跑完了：{r.Hits} 条触发，A 档 {r.TopHits.Count(h => h.Priority == "A")} 条。";
        }
        catch (Exception ex)
        {
            Status = $"重算失败：{ex.Message}";
        }
    }

    private void FillItems(List<WatchItem> items)
    {
        Items.Clear();

        // 个人观点来自个股笔记（notes/{code}.md 里以「观点：」开头的那行）。
        // 一只票的所有观察项共用同一条观点，所以按 code 读一次就够——
        // 62 只票读 62 个小文件，别在下面的循环里对着 387 条重复读。
        var opinions = new Dictionary<string, string>();
        foreach (var code in items.Select(i => i.Code).Distinct())
            if (_notes.ReadOpinion(code) is { } op) opinions[code] = op;

        foreach (var i in items
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Code))
        {
            Items.Add(new WatchItemRow
            {
                Code = i.Code, Name = i.Name,
                // 按量重算的档优先；没重算过的用挂上时定的
                Priority = _priority.TryGetValue(i.ItemId, out var pr) ? pr : i.Priority,
                Reason = i.Reason,
                // 手写观察项自带的一句话优先；没有就用笔记里的观点
                Thesis = !string.IsNullOrEmpty(i.Thesis) ? i.Thesis
                         : opinions.TryGetValue(i.Code, out var op) ? op : "",
                Detail = _detail.TryGetValue(i.ItemId, out var d) ? d : "",
            });
        }
    }

    /// <summary>
    /// 填右表。传进来的是**每个事项的最近一次进展**（<c>WatchRunResult.Progress</c>），
    /// 不是触发流水——触发只收窗口内的，业绩预告一年 4 次，右表会常年空着。
    /// 【刷新】没有重算结果可用，退回读触发历史，聊胜于无。
    /// </summary>
    /// <summary>
    /// 把右表里**勾上**的行标成已处理／取消已处理。
    ///
    /// ⚠ 只对有 HitId 的行生效：右表的进展是每轮实时算的，只有落过库的那些
    /// （＝进过触发窗口的）才有 HitId，标记记在它身上才存得住。
    /// </summary>
    private void SetHandled(bool handled)
    {
        var ids = Hits.Where(h => h.Handled && h.HitId != Guid.Empty)
                      .Select(h => h.HitId).ToList();
        if (ids.Count == 0) { Status = "先勾选要标记的行。"; return; }

        var n = _service.MarkHandled(ids, handled);
        Status = handled ? $"已标记 {n} 条为已处理。" : $"已取消 {n} 条的已处理。";
        FillHits(_lastProgress);
    }

    private void FillHits(IReadOnlyList<WatchHit> hits)
    {
        _lastProgress = hits;
        Hits.Clear();
        foreach (var h in hits
            .Where(h => !HideHandled || !h.Handled)
            .OrderByDescending(h => h.TriggerTradeDate)
            .ThenBy(h => h.Priority)
            .Take(300))
        {
            Hits.Add(new WatchHitRow
            {
                HitId = h.HitId, Handled = h.Handled,
                Date = h.TriggerTradeDate.ToString("yyyy-MM-dd"),
                Code = h.Code, Name = h.Name, Priority = h.Priority,
                ItemName = h.ItemName, Message = h.Message,
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
