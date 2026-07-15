using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockPlatform.Data.Sqlite;
using StockPlatform.Data.Sync;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Mobile.Services;

/// <summary>
/// 定位/打开本地数据库（从服务端下载来的 total.sqlite），并封装"从服务端更新数据"。手机端不抓数据，
/// 只下载现成库 + 离线分析。桌面版那套 SQLite 读取仓库直接复用。
/// </summary>
public class DataService
{
    /// <summary>App 存放已下载数据库的正式位置——各平台"本地应用数据目录"下 StockPlatform/。
    /// Android 落到 App 私有目录，桌面落到 %LOCALAPPDATA%。</summary>
    public string DataDir { get; }
    public string OfficialDbPath { get; }

    public DataService()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockPlatform");
        Directory.CreateDirectory(DataDir);
        OfficialDbPath = Path.Combine(DataDir, "total.sqlite");
    }

    /// <summary>读库用的路径：正式位置有就用它；桌面开发期兜底用电脑上 Fetcher 的库（Android 上不存在、忽略）。</summary>
    public string DbPath
    {
        get
        {
            const string desktopDev = @"C:\Chingli\Git\stock\publish\data\local\total.sqlite";
            return File.Exists(OfficialDbPath) ? OfficialDbPath
                 : File.Exists(desktopDev) ? desktopDev
                 : OfficialDbPath;
        }
    }

    public bool DbExists => File.Exists(DbPath);

    public IBarRepository BarRepository => new SqliteBarRepository(DbPath);
    public INetInflowRepository NetInflowRepository => new SqliteNetInflowRepository(DbPath);
    public IFundamentalMetricRepository FundamentalRepository => new SqliteFundamentalMetricRepository(DbPath);
    public IBoardRepository BoardRepository => new SqliteBoardRepository(DbPath);

    public DateTime? LatestDay =>
        DbExists ? BarRepository.GetOverallLatestPeriodStart(Granularity.Day) : null;

    public Dictionary<string, string> StockNames() =>
        DbExists ? SqliteStockMetaUpsert.GetAll(DbPath).ToDictionary(s => s.Code, s => s.Name)
                 : new Dictionary<string, string>();

    /// <summary>从服务端下载/合并数据到正式位置（复用共享的 DataDownloadService）。</summary>
    public System.Threading.Tasks.Task UpdateFromServerAsync(IProgress<string> status, System.Threading.CancellationToken ct = default)
        => new DataDownloadService(OfficialDbPath, DataDir).UpdateAsync(status, ct);
}
