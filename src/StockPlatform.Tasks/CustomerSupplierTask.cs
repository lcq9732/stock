using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【客户与供应商】（2026-09-08 改成新式任务）。东财 <c>RPT_F10_BUSINESS_CUSTSUPP</c>。
///
/// ════ 它是什么 ════
/// 公司年报里披露的前五大客户和前五大供应商，带交易金额和占比。**供应商＝上游、客户＝下游**——
/// 这是找"产业链上/中/下游"标签一路找空之后能拿到的最硬的产业链数据：不是别人的分类判断，
/// 是年报里的金额。（标签那条路已经查死：东财网页、终端本地文件、终端「数据」/「分析」菜单、
/// F10 全部栏目都没有；F10 前端代码里有 003=产业链 的分支，但线上一条数据都没有。）
///
/// 76.5 万行 / 2002 年至今 / 2025 年覆盖 5284 只（92%）。
///
/// ════ 三件事 ════
///   ① 按年切片抓（全表 1531 页，单年只有约 128 页——这个项目在深分页上吃过亏）
///   ② 每年抓完记一次**完成度**到 CustSuppYearState
///   ③ 抓完在 <see cref="OnCompletedAsync"/> 里做**实体消歧**，回填 partner_code——纯本地，零请求
///
/// ════ ② 为什么必须有 ════
/// 骨架会在 <see cref="TaskRunArgs.Deadline"/> / <see cref="TaskRunArgs.MaxItems"/> 到点时
/// **从批中间收尾**，而且那算正常完成（走 OnCompletedAsync、返回 Completed、界面上打勾）。
/// 判据要是只看"这一年有没有数据"，抓了 6000/66000 行的年份下轮就会被当成抓过了跳过，
/// 剩下 6 万行永远不来，**毫无征兆**。今年去年靠"每轮都重抓"能自愈，2002-2024 不能。
///
/// 一般原则：<b>任务的水位线粒度必须细于骨架的截断粒度</b>。截断粒度是批（2000 行），
/// 所以水位线不能是"年（有/无）"，得是"这一年落了多少 / 该有多少"。
///
/// ════ ③ 消歧只做两档 ════
/// 精确（全称一字不差）和归一化（去空白/括号/公司后缀后相等）。**不做"含简称"的模糊匹配**——
/// 实测只多 5 个百分点命中率，却会造出"看着像、其实不是"的错边；产业链数据有错边比没有更糟，
/// 因为你会照着它做判断。实测这两档命中真名的 7.3%，约 1865 条边/年。
/// </summary>
public sealed class CustomerSupplierTask(
    ICustomerSupplierRepository repository,
    ICompanyProfileRepository profiles,
    EastMoneyCustomerSupplierProvider provider) : FetchTaskBase<CustomerSupplier>
{
    public override FetchActionId Id => FetchActionId.StepCustomerSupplier;

    /// <summary>
    /// 本轮每年的 (接口自报, 已落库, 主动丢弃)，边抓边记，收尾时写进 CustSuppYearState。
    ///
    /// 三个数都要：reported 含非 A 股主体（约 16.4%），落库的只有 A 股——
    /// 不记 skipped 的话，主动过滤会被当成"没抓齐"，那几年每轮重抓且永远抓不齐。
    /// </summary>
    private readonly Dictionary<int, (int Reported, int Saved, int Skipped)> _thisRun = [];

    /// <summary>当前正在抓哪一年——<c>SaveBatchAsync</c> 要靠它把落库数记到对的年份上。</summary>
    private int _currentYear;

    /// <summary>是不是首次整段回补。决定消歧要不要全量重算。</summary>
    private bool _rebuild;

    protected override async IAsyncEnumerable<IReadOnlyList<CustomerSupplier>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _rebuild = args.Mode == FetchMode.FirstBackfill;

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            var states = repository.GetYearStates();
            var years = EastMoneyCustomerSupplierProvider.PlanYearsToFetch(
                DateTime.Now.Year, states, _rebuild);

            if (years.Count == 0)
            {
                Report("客户/供应商：所有年份都抓齐了，本轮无事可做。");
                yield break;
            }

            Report($"客户/供应商：本轮 {years.Count} 个年份（{years[^1]}-{years[0]}）"
                 + (_rebuild ? "，首次整段回补" : ""), 0, years.Count, "抓取");

            int doneYears = 0;
            foreach (var year in years)
            {
                ct.ThrowIfCancellationRequested();
                _currentYear = year;
                _thisRun[year] = (0, 0, 0);

                await foreach (var batch in provider.StreamYearAsync(
                                   year,
                                   onReported: n => _thisRun[year] = (n, _thisRun[year].Saved, _thisRun[year].Skipped),
                                   onSkipped: k => _thisRun[year] = (_thisRun[year].Reported, _thisRun[year].Saved, k),
                                   ct: ct))
                {
                    yield return batch;
                }

                doneYears++;
                var (rep, saved, skipped) = _thisRun[year];
                // 每年抓完立刻落一次完成度：被 Deadline 截断时，**已经抓完的年份要留下记录**，
                // 否则下轮又从头来一遍。
                repository.SaveYearState(year, rep, repository.CountByYear(year), skipped);
                Report($"{year} 年：{saved} 行"
                     + (skipped > 0 ? $"（接口自报 {rep}，丢弃 {skipped} 行非 A 股主体）" : $"（接口自报 {rep}）"),
                       doneYears, years.Count, "抓取");
            }
        }
        finally { provider.OnStatus -= Forward; }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<CustomerSupplier> batch, CancellationToken ct)
    {
        repository.Upsert(batch);
        if (_thisRun.TryGetValue(_currentYear, out var st))
            _thisRun[_currentYear] = (st.Reported, st.Saved + batch.Count, st.Skipped);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 被取消时，把**当前这一年抓到哪了**记下来——不记的话下轮它的完成度还是旧的。
    /// ⚠ 这时 ct 已经取消，只做同步的库操作。
    /// </summary>
    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        if (_currentYear > 0 && _thisRun.TryGetValue(_currentYear, out var st))
            repository.SaveYearState(_currentYear, st.Reported,
                                     repository.CountByYear(_currentYear), st.Skipped);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 被 Deadline/MaxItems 截断时，正在抓的那一年也要落完成度——
        // 骨架是 break 出来的，上面 foreach 里那句 SaveYearState 没轮到执行。
        if (_currentYear > 0 && _thisRun.TryGetValue(_currentYear, out var cur))
            repository.SaveYearState(_currentYear, cur.Reported,
                                     repository.CountByYear(_currentYear), cur.Skipped);

        var errors = new List<string>();

        // ── 对账：哪几年还没抓齐 ──
        var states = repository.GetYearStates();
        // 判据跟 PlanYearsToFetch 一致：saved + skipped 才是"这一年我们收到的全部"
        var short_ = states.Where(kv => kv.Value.Saved + kv.Value.Skipped < kv.Value.Reported)
                           .OrderByDescending(kv => kv.Key).ToList();
        if (short_.Count > 0)
        {
            var sample = string.Join("、", short_.Take(5).Select(
                kv => $"{kv.Key}年 {kv.Value.Saved}+{kv.Value.Skipped}/{kv.Value.Reported}"));
            Report($"⚠ {short_.Count} 个年份还没抓齐（{sample}），下轮会重抓这几年。", phase: "对账");
        }

        // ── 实体消歧（纯本地，零请求）──
        MatchPartners(errors);

        var (rows, stocks, first, last) = repository.GetStats();
        var (matched, unmatched, anon) = repository.GetMatchStats();
        var summary = $"客户/供应商 {rows} 行、{stocks} 只股票"
                    + (first != null ? $"、{first:yyyy}-{last:yyyy}" : "")
                    + $"；对手方已还原 {matched} 行（{unmatched} 行有名字没对上、{anon} 行是匿名披露）";
        Report(summary);

        return Task.FromResult<TaskRunResult?>(
            errors.Count > 0
                ? new TaskRunResult(TaskState.Completed, errors, Progress: summary)
                : TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }

    /// <summary>
    /// 把对手名还原成股票代码。只做精确和归一化两档，理由见类注释。
    ///
    /// <b>档案库空着就直接返回</b>——绝不把已有的 partner_code 抹成 NULL。
    /// 这跟"空集合是空操作"是同一条铁律：拉不到新数据时，库里上一次的结果仍然有效。
    /// </summary>
    private void MatchPartners(List<string> errors)
    {
        var companies = profiles.GetAllNames();
        if (companies.Count == 0)
        {
            Report("公司档案还是空的，这一轮没法做对手方还原——先跑一次【公司档案】。"
                 + "库里已有的还原结果保持不动。", phase: "消歧");
            return;
        }

        // 规则改过就得全量重算，否则只补新抓进来的那些
        var names = repository.GetPartnerNamesToMatch(all: _rebuild);
        if (names.Count == 0)
        {
            Report("没有待还原的对手名。", phase: "消歧");
            return;
        }

        Report($"对手方还原：{names.Count} 个不同的名字，比对 {companies.Count} 家上市公司...", phase: "消歧");

        var (byFull, byNorm) = PartnerNameMatcher.BuildIndex(companies);
        var hits = new Dictionary<string, (string Code, string MatchType)>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var (code, type) = PartnerNameMatcher.Match(name, byFull, byNorm);
            if (code != null && type != null) hits[name] = (code, type);
        }

        int rows = repository.ApplyMatches(hits);
        Report($"对手方还原完成：{hits.Count}/{names.Count} 个名字对上了上市公司，回填 {rows} 行。",
               phase: "消歧");
    }
}
