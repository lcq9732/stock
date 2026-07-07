using System.Windows;
using StockPlatform.Data.CloudStorage;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Fetcher.ViewModels;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Fetcher;

/// <summary>
/// Composition root — wires the data layer's concrete implementations together.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new FetchPaths();

        // Each source gets its own rate limiter — they're independent servers with independent
        // budgets. Each source also bundles the stock-list provider it should use for the
        // "获取全市场股票列表" step, so switching sources routes BOTH steps away from a vendor
        // that's blocked/rate-limited on this machine, not just bar fetching — see
        // doc/data-platform-design.md section 3.4. Tencent has no public full-list endpoint
        // (only per-code lookups), so its list step is paired with Sina instead — a different
        // domain from eastmoney.com either way, which is what actually matters here.
        var sources = new List<NamedBarSource>
        {
            new("EastMoney", new EastMoneyBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new EastMoneyStockListProvider()),
            new("Tencent", new TencentBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new SinaStockListProvider()),
        };

        var manifestStore = new JsonManifestStore(paths.ManifestPath);
        var cloudStorage = new ManualUploadPrompter(paths.OutboxDir, paths.InboxDir);
        var orchestrator = new FetchOrchestrator(paths, manifestStore, cloudStorage);

        var viewModel = new MainViewModel(orchestrator, sources);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
