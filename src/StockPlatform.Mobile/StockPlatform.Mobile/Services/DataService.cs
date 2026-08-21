using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Mobile.Services;

/// <summary>
/// 定位/打开本地数据库。手机端不抓数据，只读现成的库做离线分析，桌面版那套 SQLite 读取仓库直接复用。
///
/// 库从哪来（2026-08-21 改）：**跟桌面分析程序读同一个 current.sqlite**。原先是从 GitHub Releases
/// 下载全量基线+每日增量（DataDownloadService），但库长到 7.5GB、压完 1.8GB 贴着 GitHub 单资产 2GB
/// 上限，传不上去也下不下来，那套下载已整体删除（见 doc/data-platform-design.md 的 2026-08-21 记录）。
///
/// 查找顺序：① 逐级向上找 publish/data/local/current.sqlite——桌面变体(StockPlatform.Mobile.Desktop)
/// 跟 Fetcher/Analyzer 在同一台机器上跑，直接读它们那个库，零拷贝零配置；② 找不到就用 App 私有目录下
/// 的同名文件——真机(Android/iOS)上没有 publish 目录，只能由用户自己把库拷进去。
/// </summary>
public class DataService
{
    /// <summary>App 自己的状态目录（自选股等）——各平台"本地应用数据目录"下 StockPlatform/。
    /// Android 落到 App 私有目录，桌面落到 %LOCALAPPDATA%。</summary>
    public string DataDir { get; }

    /// <summary>真机上放库的位置（没有 publish 目录时的落点，需用户手动拷贝进来）。</summary>
    public string LocalDbPath { get; }

    public DataService()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockPlatform");
        Directory.CreateDirectory(DataDir);
        LocalDbPath = Path.Combine(DataDir, "current.sqlite");
    }

    /// <summary>读库用的路径：优先桌面上那份共享库，其次 App 私有目录（见类注释）。</summary>
    public string DbPath => ProbeSharedDb() ?? LocalDbPath;

    /// <summary>从当前目录和程序目录逐级向上找 publish/data/local/current.sqlite——跟
    /// FactorLab.ProbeDb 同一套查找。真机上必然找不到（没有这个目录），返回 null 交给
    /// <see cref="LocalDbPath"/>。</summary>
    private static string? ProbeSharedDb()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "publish", "data", "local", "current.sqlite");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public bool DbExists => File.Exists(DbPath);

    public IBarRepository BarRepository => new SqliteBarRepository(DbPath);
    public INetInflowRepository NetInflowRepository => new SqliteNetInflowRepository(DbPath);
    public IFundamentalMetricRepository FundamentalRepository => new SqliteFundamentalMetricRepository(DbPath);
    public IBoardRepository BoardRepository => new SqliteBoardRepository(DbPath);
    // 下面三个是彬哥法/短线法要用的（桌面版引擎 2026-08 加了这些依赖，见 ScreeningMethods）。
    public IShareholderRepository ShareholderRepository => new SqliteShareholderRepository(DbPath);
    public IMarginRepository MarginRepository => new SqliteMarginRepository(DbPath);
    public IFinancialRepository FinancialRepository => new SqliteFinancialRepository(DbPath);

    public DateTime? LatestDay =>
        DbExists ? BarRepository.GetOverallLatestPeriodStart(Granularity.Day) : null;

    public Dictionary<string, string> StockNames() =>
        DbExists ? SqliteStockMetaUpsert.GetAll(DbPath).ToDictionary(s => s.Code, s => s.Name)
                 : new Dictionary<string, string>();
}
