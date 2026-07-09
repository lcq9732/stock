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

        var paths = new AnalyzerPaths();
        var barRepository = new SqliteBarRepository(paths.TotalDb);
        barRepository.EnsureSchema();
        var fundamentalRepository = new SqliteFundamentalMetricRepository(paths.TotalDb);
        fundamentalRepository.EnsureSchema();
        var netInflowRepository = new SqliteNetInflowRepository(paths.TotalDb);
        netInflowRepository.EnsureSchema();

        var viewModel = new MainViewModel(paths, barRepository, fundamentalRepository, netInflowRepository);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
