using System.Text;

namespace StockPlatform.FactorLab.Core;

/// <summary>输出：控制台汇总表、summary.csv、factor_corr.csv、每因子明细序列、因子手册 factors.md。</summary>
public static class Report
{
    static readonly UTF8Encoding Utf8Bom = new(true); // 带BOM，Excel直接打开不乱码

    public static void WriteAll(string outDir, MarketData md, SharedEval sh, IReadOnlyList<FactorResult> results,
        double[,] corr, Dictionary<int, string?> dedup, IReadOnlyList<PortfolioResult> portfolios,
        (CompositeFactor Factor, FactorResult Result)? picksSource)
    {
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "detail"));

        PrintConsole(sh, results, dedup);
        PrintPortfolios(portfolios);
        WriteSummaryCsv(Path.Combine(outDir, "summary.csv"), results, dedup);
        WriteCorrCsv(Path.Combine(outDir, "factor_corr.csv"), results, corr);
        foreach (var r in results)
            WriteDetailCsv(Path.Combine(outDir, "detail", Sanitize(r.Factor.Name) + ".csv"), sh, r);
        if (portfolios.Count > 0)
            WritePortfolioCsv(Path.Combine(outDir, "portfolio.csv"), sh, portfolios[^1]);
        List<string>? picks = null;
        if (picksSource is { } ps)
            picks = WritePicks(Path.Combine(outDir, "picks_latest.csv"), md, ps.Factor, ps.Result, results);
        WriteManual(Path.Combine(outDir, "factors.md"), md, sh, results, corr, dedup, portfolios, picks);
    }

    static string RoleLabel(FactorRole role, bool longForm = false) => role switch
    {
        FactorRole.PositiveControl => longForm ? "阳性对照" : "阳性",
        FactorRole.NegativeControl => longForm ? "阴性对照" : "阴性",
        FactorRole.Composite => "合成",
        _ => "候选",
    };

    static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static string Pct(double v) => double.IsNaN(v) ? "  -  " : (v * 100).ToString("+0.0;-0.0") + "%";
    static string Num(double v, string fmt = "0.000") => double.IsNaN(v) ? "-" : v.ToString(fmt);

    static void PrintConsole(SharedEval sh, IReadOnlyList<FactorResult> results, Dictionary<int, string?> dedup)
    {
        int inN = sh.Periods.Count(p => p.InSample), outN = sh.Periods.Count - inN;
        Console.WriteLine();
        Console.WriteLine($"=== 因子评估汇总（样本内 {inN} 期 / 样本外 {outN} 期，每期 {Config.HoldDays} 个交易日）===");
        Console.WriteLine($"{"因子",-18}{"类别",-6}{"角色",-6}{"IC内",8}{"ICIR内",8}{"中性IC内",9}{"多空内",10}{"IC外",8}{"中性IC外",9}{"多空外",10}{"换手",7}");
        foreach (var r in results.OrderByDescending(x => Math.Abs(x.IcIn.Mean)))
        {
            var f = r.Factor;
            Console.WriteLine(
                $"{f.Name,-18}{f.Category,-6}{RoleLabel(f.Role),-6}" +
                $"{Num(r.IcIn.Mean),8}{Num(r.IcIn.Icir, "0.00"),8}{Num(r.IcNeuIn.Mean),9}{Pct(r.LsAnnIn),10}" +
                $"{Num(r.IcOut.Mean),8}{Num(r.IcNeuOut.Mean),9}{Pct(r.LsAnnOut),10}{Num(r.AvgTurnover, "0%"),7}");
        }
        Console.WriteLine();
        Console.WriteLine("说明：IC=每期RankIC均值；中性IC=剥离市值/行业后的IC；多空=最高组-最低组年化；换手=Top组每期换手率。");

        var kept = dedup.Where(kv => kv.Value is null).Select(kv => results[kv.Key].Factor.Name).ToList();
        var dropped = dedup.Where(kv => kv.Value is not null).ToList();
        Console.WriteLine($"\n去重（|ρ|>0.8，按样本内|ICIR|优先保留）：保留 {kept.Count} 个候选：{string.Join("、", kept)}");
        foreach (var kv in dropped)
            Console.WriteLine($"  剔除 {results[kv.Key].Factor.Name} —— 与「{kv.Value}」重复");
    }

    static void PrintPortfolios(IReadOnlyList<PortfolioResult> portfolios)
    {
        if (portfolios.Count == 0) return;
        Console.WriteLine($"\n=== 组合回测（每期Top{Config.TopN}等权，扣双边成本{Config.RoundTripCost:P1}）===");
        Console.WriteLine($"{"曲线",-26}{"年化(全)",10}{"年化(内)",10}{"年化(外)",10}{"最大回撤",10}{"夏普",8}");
        foreach (var port in portfolios)
        {
            Console.WriteLine($"-- 选股因子：{port.FactorName}（空仓期占比 {1 - port.TimingLong.Count(x => x) / (double)port.TimingLong.Length:0%}）");
            foreach (var c in port.Curves)
                Console.WriteLine($"{c.Name,-26}{Pct(c.AnnFull),10}{Pct(c.AnnIn),10}{Pct(c.AnnOut),10}{Pct(c.MaxDrawdown),10}{Num(c.Sharpe, "0.00"),8}");
        }
    }

    static void WriteSummaryCsv(string path, IReadOnlyList<FactorResult> results, Dictionary<int, string?> dedup)
    {
        var years = results.SelectMany(r => r.YearlyIc.Keys).Distinct().OrderBy(y => y).ToList();
        var sb = new StringBuilder();
        sb.Append("因子,类别,角色,去重,方向,IC内,ICIR内,胜率内,期数内,中性IC内,中性ICIR内,多空年化内,Top扣费年化内,IC外,ICIR外,胜率外,期数外,中性IC外,多空年化外,Top扣费年化外,平均换手");
        foreach (var y in years) sb.Append($",IC{y}");
        for (int g = 1; g <= Config.Deciles; g++) sb.Append($",D{g}年化(全)");
        sb.AppendLine(",构造公式,作用说明");

        foreach (var (r, i) in results.Select((r, i) => (r, i)).OrderByDescending(x => Math.Abs(x.r.IcIn.Mean)))
        {
            var f = r.Factor;
            string dd = f.Role != FactorRole.Candidate ? "不参与" : dedup.GetValueOrDefault(i) is string dup ? $"与{dup}重复" : "保留";
            sb.Append($"{f.Name},{f.Category},{RoleLabel(f.Role, longForm: true)},{dd},\"{f.Direction}\"");
            sb.Append($",{Num(r.IcIn.Mean, "0.0000")},{Num(r.IcIn.Icir, "0.000")},{Num(r.IcIn.WinRate, "0.000")},{r.IcIn.N}");
            sb.Append($",{Num(r.IcNeuIn.Mean, "0.0000")},{Num(r.IcNeuIn.Icir, "0.000")}");
            sb.Append($",{Num(r.LsAnnIn, "0.0000")},{Num(r.TopNetAnnIn, "0.0000")}");
            sb.Append($",{Num(r.IcOut.Mean, "0.0000")},{Num(r.IcOut.Icir, "0.000")},{Num(r.IcOut.WinRate, "0.000")},{r.IcOut.N}");
            sb.Append($",{Num(r.IcNeuOut.Mean, "0.0000")}");
            sb.Append($",{Num(r.LsAnnOut, "0.0000")},{Num(r.TopNetAnnOut, "0.0000")},{Num(r.AvgTurnover, "0.000")}");
            foreach (var y in years) sb.Append($",{(r.YearlyIc.TryGetValue(y, out var ic) ? Num(ic, "0.0000") : "-")}");
            foreach (var d in r.DecileAnnFull) sb.Append($",{Num(d, "0.0000")}");
            sb.AppendLine($",\"{f.Formula}\",\"{f.Description}\"");
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    static void WriteCorrCsv(string path, IReadOnlyList<FactorResult> results, double[,] corr)
    {
        var sb = new StringBuilder();
        sb.AppendLine("," + string.Join(",", results.Select(r => r.Factor.Name)));
        for (int i = 0; i < results.Count; i++)
        {
            sb.Append(results[i].Factor.Name);
            for (int j = 0; j < results.Count; j++) sb.Append($",{Num(corr[i, j], "0.000")}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    static void WriteDetailCsv(string path, SharedEval sh, FactorResult r)
    {
        var sb = new StringBuilder();
        sb.Append("调仓日,样本,RankIC,中性IC");
        for (int g = 1; g <= Config.Deciles; g++) sb.Append($",D{g}期收益");
        sb.AppendLine(",多空期收益,Top换手,等权基准期收益");
        for (int p = 0; p < sh.Periods.Count; p++)
        {
            if (double.IsNaN(r.IcSeries[p])) continue;
            var dr = r.DecileRet[p];
            sb.Append($"{sh.Periods[p].Date:yyyy-MM-dd},{(sh.Periods[p].InSample ? "内" : "外")},{Num(r.IcSeries[p], "0.0000")},{Num(r.IcNeuSeries[p], "0.0000")}");
            foreach (var d in dr) sb.Append($",{Num(d, "0.0000")}");
            double ls = !double.IsNaN(dr[^1]) && !double.IsNaN(dr[0]) ? dr[^1] - dr[0] : double.NaN;
            sb.AppendLine($",{Num(ls, "0.0000")},{Num(r.TopTurnover[p], "0.000")},{Num(sh.BenchRet[p], "0.0000")}");
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    static void WritePortfolioCsv(string path, SharedEval sh, PortfolioResult port)
    {
        var sb = new StringBuilder();
        sb.Append("调仓日,样本,择时持仓");
        foreach (var c in port.Curves) sb.Append($",{c.Name}期收益,{c.Name}净值");
        sb.AppendLine();
        var navs = new double[port.Curves.Count];
        Array.Fill(navs, 1.0);
        for (int p = 0; p < sh.Periods.Count; p++)
        {
            sb.Append($"{sh.Periods[p].Date:yyyy-MM-dd},{(sh.Periods[p].InSample ? "内" : "外")},{(port.TimingLong[p] ? 1 : 0)}");
            for (int c = 0; c < port.Curves.Count; c++)
            {
                double r = port.Curves[c].PeriodRet[p];
                if (double.IsNaN(r)) r = 0;
                navs[c] *= 1 + r;
                sb.Append($",{r:0.0000},{navs[c]:0.0000}");
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    /// <summary>最新交易日的合成因子Top名单：代码/名称/得分/主要贡献因子。返回控制台/手册用的行文本。</summary>
    static List<string> WritePicks(string path, MarketData md, CompositeFactor comp, FactorResult compResult, IReadOnlyList<FactorResult> results)
    {
        var rows = Picks.Build(md, comp, compResult, results, Config.TopN);
        var componentOrder = comp.Components.Select(c => c.Factor.Name).ToList();

        var sb = new StringBuilder();
        sb.Append("名次,代码,名称,行业,融资占比,合成得分");
        foreach (var name in componentOrder) sb.Append($",{name}");
        sb.AppendLine();
        var lines = new List<string>();
        foreach (var row in rows)
        {
            var byFactor = row.Contribs.ToDictionary(c => c.Factor, c => c.Score);
            sb.Append($"{row.Rank},{row.Code},{row.Name},{row.Industry}," +
                      $"{(double.IsNaN(row.MarginRatio) ? "" : row.MarginRatio.ToString("0.0%"))},{row.Score:0.000}");
            foreach (var name in componentOrder) sb.Append($",{Num(byFactor[name], "0.000")}");
            sb.AppendLine();
            lines.Add($"{row.Rank}. {row.Code} {row.Name}[{row.Industry}]（{row.Score:0.000}；主要贡献：{row.TopContribsText}）");
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);

        Console.WriteLine($"\n=== 最新名单（{md.Dates[^1]:yyyy-MM-dd} 收盘，{comp.Name} Top{Config.TopN}，前10）===");
        foreach (var line in lines.Take(10)) Console.WriteLine("  " + line);
        Console.WriteLine("  （完整名单见 picks_latest.csv；因子排序输出，不构成买入建议）");
        return lines;
    }

    /// <summary>因子手册：每个因子是什么、为什么、评估结果如何。给人看的单一入口。</summary>
    static void WriteManual(string path, MarketData md, SharedEval sh, IReadOnlyList<FactorResult> results,
        double[,] corr, Dictionary<int, string?> dedup, IReadOnlyList<PortfolioResult> portfolios, List<string>? picks)
    {
        var sb = new StringBuilder();
        int inN = sh.Periods.Count(p => p.InSample), outN = sh.Periods.Count - inN;
        sb.AppendLine("# FactorLab 因子手册");
        sb.AppendLine();
        sb.AppendLine(md.UsingHfq
            ? $"- 数据：{md.Dates[0]} ~ {md.Dates[^1]}（{md.NDays} 个交易日，{md.NStocks} 只 A 股，**后复权**日线）"
            : $"- 数据：{md.Dates[0]} ~ {md.Dates[^1]}（{md.NDays} 个交易日，{md.NStocks} 只 A 股）\n"
              + "- ⚠️ **警告：库里没有后复权日线，本次降级用了前复权。** 数据源的前复权是减法式，回看越久"
              + "高分红股的复权价越接近零甚至为负，收益率会严重失真（实测2016~2019有27%的股票出现过物理上"
              + "不可能的单日涨跌）。**本报告的长周期结论不可信**，请先在 Fetcher 跑一次\"拉取区间数据\"补后复权。");
        if (md.DirtyBarsDropped > 0)
            sb.AppendLine($"- 脏数据剔除：{md.DirtyBarsDropped:N0} 根（价格≤0 或单日涨跌超 ±{Config.MaxDailyReturn:P0} 的物理不可能值）");
        sb.AppendLine($"- 协议：T 收盘算因子 → T+1 开盘买入 → 持有 {Config.HoldDays} 个交易日；剔除 ST/次新/停牌/一字板；双边成本 {Config.RoundTripCost:P1}");
        sb.AppendLine($"- 切分：样本内 {inN} 期（< {Config.SplitDate}），样本外 {outN} 期。**筛选只看样本内，样本外仅作验证。**");
        sb.AppendLine($"- 中性IC：因子先做 winsorize→标准化→行业内去均值→对市值回归取残差，再算 RankIC，反映剥离规模/行业风格后的独立信息（行业覆盖率约46%，无归属股票归为一组）。");
        int delistedInPool = Enumerable.Range(0, md.NStocks).Count(s => md.IsDelisted[s] && md.FirstBarIdx[s] >= 0);
        sb.AppendLine(delistedInPool > 0
            ? $"- 股票池含 {delistedInPool} 只已退市股（有K线者，缓解幸存者偏差；不做按名ST剔除，只剔近退市{Config.DelistExcludeDays}日）。其余局限：在市股ST按当前名称、股东户数披露滞后为估计值。"
            : "- 已知局限：无退市股（幸存者偏差，跑一次 Fetcher 的\"拉取区间数据\"可补齐）、ST 按当前名称、股东户数披露滞后为估计值。");
        sb.AppendLine("- ⚠️ 历史市值是**近似值**：用「最新流通市值 ÷ 最新收盘 × 当日收盘」折算。"
                      + "**送转不是问题**（后复权因子与股本变动是同一个乘数，已被正确抵消）；残留误差有两处："
                      + "①现金分红抬高后复权因子，使历史市值被系统性低估（十年累计约20~25%，高股息股最明显）；"
                      + "②增发/回购完全没有处理（无数据源）。**小市值因子与市值中性化的结论要相应打折**。");
        sb.AppendLine();
        sb.AppendLine("## 汇总");
        sb.AppendLine();
        sb.AppendLine("| 因子 | 类别 | 角色 | 去重 | IC内 | ICIR内 | 中性IC内 | 多空年化内 | IC外 | 中性IC外 | 多空年化外 | Top扣费年化外 | 换手 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (r, i) in results.Select((r, i) => (r, i)).OrderByDescending(x => Math.Abs(x.r.IcIn.Mean)))
        {
            var f = r.Factor;
            string dd = f.Role != FactorRole.Candidate ? "—" : dedup.GetValueOrDefault(i) is string dup ? $"与{dup}重复" : "**保留**";
            sb.AppendLine($"| {f.Name} | {f.Category} | {RoleLabel(f.Role, longForm: true)} | {dd} | {Num(r.IcIn.Mean)} | {Num(r.IcIn.Icir, "0.00")} | {Num(r.IcNeuIn.Mean)} | {Pct(r.LsAnnIn)} | {Num(r.IcOut.Mean)} | {Num(r.IcNeuOut.Mean)} | {Pct(r.LsAnnOut)} | {Pct(r.TopNetAnnOut)} | {Num(r.AvgTurnover, "0%")} |");
        }
        sb.AppendLine();
        var keptNames = dedup.Where(kv => kv.Value is null).Select(kv => results[kv.Key].Factor.Name).ToList();
        sb.AppendLine($"**去重结论**（截面相关|ρ|>0.8 视为重复，按样本内|ICIR|优先保留）：保留 {keptNames.Count} 个候选 —— {string.Join("、", keptNames)}。");
        sb.AppendLine();
        sb.AppendLine("## 因子明细");
        foreach (var r in results)
        {
            var f = r.Factor;
            string role = f.Role switch
            {
                FactorRole.PositiveControl => "阳性对照（应有信号，否则框架有bug）",
                FactorRole.NegativeControl => "阴性对照（应无信号，否则框架有前视）",
                FactorRole.Composite => "多因子合成（入选/方向/权重均由样本内决定）",
                _ => "正式候选",
            };
            sb.AppendLine();
            sb.AppendLine($"### {f.Name}（{f.Category}）");
            sb.AppendLine();
            sb.AppendLine($"- **角色**：{role}");
            sb.AppendLine($"- **构造**：`{f.Formula}`");
            sb.AppendLine($"- **方向**：{f.Direction}");
            sb.AppendLine($"- **作用**：{f.Description}");
            sb.AppendLine($"- **中性化后**：IC内 {Num(r.IcNeuIn.Mean)}（ICIR {Num(r.IcNeuIn.Icir, "0.00")}），IC外 {Num(r.IcNeuOut.Mean)} —— 与原始IC差距大说明信号主要来自规模/行业暴露");
            sb.AppendLine($"- **逐年IC**：{string.Join("，", r.YearlyIc.Select(kv => $"{kv.Key}={Num(kv.Value)}"))}");
            sb.AppendLine($"- **十分组年化（D1最低→D10最高，全期未扣费）**：{string.Join(" | ", r.DecileAnnFull.Select(Pct))}");
        }
        sb.AppendLine();
        if (portfolios.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## 组合回测（每期Top{Config.TopN}等权，扣双边成本{Config.RoundTripCost:P1}，明细见 portfolio.csv）");
            sb.AppendLine();
            sb.AppendLine("择时规则：调仓日上证指数收盘 < MA" + Config.TimingMaWindow + " 则整期空仓（现金零收益），重新进场付全额建仓成本。基准择时不计成本，仅作信息参照。");
            sb.AppendLine();
            sb.AppendLine("| 曲线 | 年化(全) | 年化(内) | 年化(外) | 最大回撤 | 夏普 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var port in portfolios)
            {
                sb.AppendLine($"| **选股因子：{port.FactorName}**（空仓期占比 {1 - port.TimingLong.Count(x => x) / (double)port.TimingLong.Length:0%}） | | | | | |");
                foreach (var c in port.Curves)
                    sb.AppendLine($"| {c.Name} | {Pct(c.AnnFull)} | {Pct(c.AnnIn)} | {Pct(c.AnnOut)} | {Pct(c.MaxDrawdown)} | {Num(c.Sharpe, "0.00")} |");
            }
        }
        if (picks is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"## 最新名单（{md.Dates[^1]:yyyy-MM-dd} 收盘，合成因子Top{Config.TopN}，全名单见 picks_latest.csv）");
            sb.AppendLine();
            sb.AppendLine("**注意：这是因子排序输出，不是买入建议**——绝对收益被幸存者偏差高估，且未考虑任何个股基本面/消息面。");
            sb.AppendLine();
            foreach (var line in picks.Take(20)) sb.AppendLine($"- {line}");
        }
        sb.AppendLine();
        sb.AppendLine("## 因子相关性（截面Spearman时序均值，|ρ|>0.8 视为重复）");
        sb.AppendLine();
        sb.AppendLine("| |" + string.Join("|", results.Select(r => r.Factor.Name)) + "|");
        sb.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", results.Count)));
        for (int i = 0; i < results.Count; i++)
        {
            sb.Append($"|{results[i].Factor.Name}|");
            for (int j = 0; j < results.Count; j++) sb.Append($"{Num(corr[i, j], "0.00")}|");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }
}
