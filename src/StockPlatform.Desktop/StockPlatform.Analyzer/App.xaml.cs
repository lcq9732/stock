using System.Windows;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Analyzer.ViewModels;

namespace StockPlatform.Analyzer;

/// <summary>Composition root — wires the data layer's concrete implementations together.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 只允许开一个实例（2026-08-04新增，见 SingleInstanceGuard 的类注释）——两个 Analyzer 同时
        // 编辑 watchlist.json 会互相覆盖（那个文件是整体读-改-写），所以这不只是体验问题。
        if (!Desktop.Shared.SingleInstanceGuard.TryAcquire("Analyzer", "A股批量分析程序"))
        {
            Shutdown();
            return;
        }

        var paths = new AnalyzerPaths();
        var barRepository = new SqliteBarRepository(paths.CurrentDb);
        barRepository.EnsureSchema();
        var fundamentalRepository = new SqliteFundamentalMetricRepository(paths.CurrentDb);
        fundamentalRepository.EnsureSchema();
        var netInflowRepository = new SqliteNetInflowRepository(paths.CurrentDb);
        netInflowRepository.EnsureSchema();
        var boardRepository = new SqliteBoardRepository(paths.CurrentDb);
        boardRepository.EnsureSchema();
        var shareholderRepository = new SqliteShareholderRepository(paths.CurrentDb);
        shareholderRepository.EnsureSchema();
        var marginRepository = new SqliteMarginRepository(paths.CurrentDb);
        marginRepository.EnsureSchema();
        var financialRepository = new SqliteFinancialRepository(paths.CurrentDb);
        financialRepository.EnsureSchema();
        var dividendRepository = new SqliteDividendRepository(paths.CurrentDb);
        dividendRepository.EnsureSchema();
        // 指数成分——晨检的"风格温度计"要拿红利/上证50的成分股算等权超额（见 StyleGauge）
        var indexConsRepository = new SqliteIndexRepository(paths.CurrentDb);
        indexConsRepository.EnsureSchema();

        var viewModel = new MainViewModel(paths, barRepository, fundamentalRepository, netInflowRepository, boardRepository, shareholderRepository, marginRepository, financialRepository, dividendRepository, indexConsRepository);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Desktop.Shared.SingleInstanceGuard.Release();
        base.OnExit(e);
    }
}
