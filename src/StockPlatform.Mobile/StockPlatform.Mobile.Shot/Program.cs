using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using StockPlatform.Mobile;
using StockPlatform.Mobile.Services;
using StockPlatform.Mobile.ViewModels;
using StockPlatform.Mobile.Views;

string outDir = @"C:\Users\chingli\AppData\Local\Temp\claude\C--Chingli-Git-stock\a1a9388d-0f27-47e0-a088-ea4f6a59c8de\scratchpad";

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var vm = new MainViewModel();
vm.SelectedMethod = ScreeningMethods.All[5]; // 短线法（4个可调参数）
var win = new MainWindow { DataContext = vm, Width = 440, Height = 500 };
win.Show();
Dispatcher.UIThread.RunJobs();
var frame = win.CaptureRenderedFrame();
var path = System.IO.Path.Combine(outDir, "shot_params.png");
frame?.Save(path);
Console.WriteLine($"params -> {path} ({(frame == null ? "null" : "ok")})");

Environment.Exit(0);
