namespace StockPlatform.Data.Orchestration;

/// <summary>Local folder/file layout for the data-fetcher program, all rooted next to the executable.</summary>
public class FetchPaths
{
    public string BaseDir { get; }
    public string CurrentDb => Path.Combine(BaseDir, "local", "current.sqlite");
    public string ManifestPath => Path.Combine(BaseDir, "local", "manifest.json");

    /// <summary>运行日志落盘位置（2026-07-08新增）——每次程序启动时清空重写，逐行写入并立即
    /// flush，这样即使程序异常退出（崩溃/被强制结束），也能打开这个文件看到崩溃前最后发生了
    /// 什么，不需要依赖还开着的界面窗口。</summary>
    public string LogFilePath => Path.Combine(BaseDir, "local", "fetch.log");

    /// <summary>历次运行的日志归档目录（2026-08-21新增，见 Fetcher 的 MainViewModel.ArchivePreviousLog）——
    /// 上一轮的 fetch.log 在下次启动时挪到这里，不再被直接冲掉。</summary>
    public string LogArchiveDir => Path.Combine(BaseDir, "local", "logs");

    /// <summary>银行财报 PDF 的本地缓存（2026-08-29 新增）——不良率/拨备覆盖率/资本充足率这些
    /// 监管指标只在财报正文里，得下载 PDF 解析。留着原件是为了解析规则改了能本地重放，不用
    /// 重新下载 42 家 × N 期（而且新浪的公告 URL 带内网 IP、老链接容易失效）。
    /// 约 250MB/年，**不进数据库**——几百 MB 的 BLOB 会让 7.5GB 的库备份和 VACUUM 都难受。</summary>
    public string ReportsDir => Path.Combine(BaseDir, "reports");

    /// <summary>
    /// 全市场年报 PDF（2026-09-11 新增）——给「年报子公司名单」解析用，见 SubsidiaryParser。
    ///
    /// ⚠ **故意跟 <see cref="ReportsDir"/> 分开**，这是踩过的坑：把非金融年报放进那个目录后，
    ///   【重解析已有PDF】扫到它们，用 BankReportParser.LooksLikeReport 一判——那个判据是为
    ///   "拦截下错的问询函"写的，看前 3 页有没有年报结构关键词，而非金融年报前几页是封面和
    ///   图片，撞不上 → 判成下错文件 → **把 PDF 删了**。实测一轮删掉 14 份。
    ///   两种用途的数据共用一个目录，迟早还会互相踩，所以分开。
    /// </summary>
    public string AnnualReportsDir => Path.Combine(BaseDir, "annual-reports");

    /// <summary>抓取程序自己的界面设置（2026-08-27新增）——目前只有"空闲时自动补财务数据"这个
    /// 开关。跟 manifest.json 分开：那个是数据状态（抓到哪天了），这个是用户偏好。</summary>
    public string SettingsPath => Path.Combine(BaseDir, "fetcher-settings.json");

    /// <summary>
    /// 抓取计划（2026-08-31 新增）——排好的任务顺序、各自的触发时间和重复规则，以及每项上次
    /// 跑的结果。跟 <see cref="SettingsPath"/> 分开：那个是零散开关，这个是一份有结构、
    /// 会被用户反复编辑的清单，混在一起以后加字段两边都难受。
    /// </summary>
    public string PlanPath => Path.Combine(BaseDir, "fetch-plan.json");

    /// <summary>
    /// 「板块 → 该盯的行业指标」规则（2026-09-11 新增，见 doc/watch-item-design.md §5）。
    /// 又一个独立文件而不是塞进 <see cref="SettingsPath"/>：那个是零散开关，这个是一份**会长**的
    /// 清单（一个板块一行、还带理由），混进去以后两边都难读。同 <see cref="PlanPath"/> 的理由。
    /// </summary>
    public string WatchIndicatorRulesPath => Path.Combine(BaseDir, "watch-indicator-rules.json");

    /// <summary>计划执行的当日报告（<c>plan-yyyy-MM-dd.txt</c>）落在日志归档目录里——
    /// 无人值守跑完，第二天早上看这一份就知道昨晚每项什么时候跑的、结果如何。</summary>
    public string PlanReportPath(DateTime day) =>
        Path.Combine(LogArchiveDir, $"plan-{day:yyyy-MM-dd}.txt");

    /// <summary>
    /// 换数据源之前的整表备份（2026-09-09 新增）——**独立的 sqlite 文件，不在主库里**。
    ///
    /// 为什么不做成库内的 Xxx_backup 表：备份的意义是"主库出事时它还在"，跟主库同生共死的
    /// 副本只是把 23GB 的库撑得更大。落成单独文件，要比对时 ATTACH 回来即可，不用了直接删。
    /// </summary>
    public string BackupDbPath(string table, DateTime day) =>
        Path.Combine(BaseDir, "local", "backup", $"{table}-{day:yyyyMMdd-HHmmss}.sqlite");

    public FetchPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(Path.Combine(BaseDir, "local"));
    }
}
