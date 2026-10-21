using System.Text;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【导入手工数据】挪进日更后的判据（2026-10-21 用户定）：清单不存在算没活；
/// 自动跑只导"人改过（文件比生成时新）、还没导过"的那一版；手动点就导。
///
/// 防的是两件静默的事：
///   ① 监管指标刚生成清单、紧接着的自动导入就把所有 OCR 值标成"已核对"——人一眼都没看过；
///   ② 人填好清单还没导入，监管指标重新生成把它覆盖掉——填的数没了，没有任何提示。
/// </summary>
public class ManualFillImportRuleTests : IDisposable
{
    private static readonly DateTime Gen = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = Gen.AddMinutes(30);

    private readonly string _dir;
    private readonly FetchPaths _paths;

    public ManualFillImportRuleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"manualFill_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        Directory.CreateDirectory(_paths.ReportsDir);
        new SqliteBankRegulatoryRepository(_paths.CurrentDb).EnsureSchema();
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 判据本身（纯计算）─────────────────────────────────────────

    [Theory]
    [InlineData(false, false, ManualImportDecision.NoFile)]
    [InlineData(false, true, ManualImportDecision.NoFile)]       // 手动点也一样：没文件就是没活
    public void 清单不存在_没活(bool hasState, bool manual, ManualImportDecision expected)
    {
        var st = hasState ? new ManualFillState(Gen, null) : null;
        Assert.Equal(expected, ManualFillImportRule.Decide(false, DateTime.MinValue, st, manual));
    }

    [Fact]
    public void 生成后没改过_自动跑不导()
        => Assert.Equal(ManualImportDecision.NotEdited,
            ManualFillImportRule.Decide(true, Gen, new ManualFillState(Gen, null), manual: false));

    [Fact]
    public void 改过且这一版没导过_导入()
        => Assert.Equal(ManualImportDecision.Import,
            ManualFillImportRule.Decide(true, Edited, new ManualFillState(Gen, null), manual: false));

    [Fact]
    public void 这一版已经导过_不再导()
        => Assert.Equal(ManualImportDecision.AlreadyImported,
            ManualFillImportRule.Decide(true, Edited, new ManualFillState(Gen, Edited), manual: false));

    [Fact]
    public void 导过之后又改了_再导()
        => Assert.Equal(ManualImportDecision.Import,
            ManualFillImportRule.Decide(true, Edited.AddHours(1), new ManualFillState(Gen, Edited), manual: false));

    [Fact]
    public void 没有生成记录的旧清单_自动跑不导()
        => Assert.Equal(ManualImportDecision.NoRecord,
            ManualFillImportRule.Decide(true, Edited, null, manual: false));

    [Fact]
    public void 手动点_清单在就导()
    {
        Assert.Equal(ManualImportDecision.Import, ManualFillImportRule.Decide(true, Gen, new ManualFillState(Gen, null), manual: true));
        Assert.Equal(ManualImportDecision.Import, ManualFillImportRule.Decide(true, Edited, null, manual: true));
    }

    [Fact]
    public void 改过没导的才不能覆盖()
    {
        Assert.True(ManualFillImportRule.HasUnimportedEdits(true, Edited, new ManualFillState(Gen, null)));
        Assert.False(ManualFillImportRule.HasUnimportedEdits(true, Gen, new ManualFillState(Gen, null)));        // 没改过
        Assert.False(ManualFillImportRule.HasUnimportedEdits(true, Edited, new ManualFillState(Gen, Edited)));   // 已导入
        Assert.False(ManualFillImportRule.HasUnimportedEdits(true, Edited, null));                               // 旧清单照原来覆盖
        Assert.False(ManualFillImportRule.HasUnimportedEdits(false, DateTime.MinValue, new ManualFillState(Gen, null)));
    }

    // ── 打在真文件上：生成 → 没改不导 → 改了自动导 → 再跑没活 → 重新生成不覆盖 ──

    private string Csv => Path.Combine(_paths.ReportsDir, ManualFillWorklist.FileName);

    /// <summary>造一份"刚生成"的清单：只有表头，状态里记着它的生成时刻。</summary>
    private void Generated()
    {
        File.WriteAllText(Csv, "机构代码,机构类型,报告期,指标key,指标名称,去哪里找,翻到第几页,打开PDF,OCR识别值,填这里\n",
                          new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(Csv, Gen);
        ManualFillStateStore.Save(_paths.ReportsDir, new ManualFillState(Gen, null));
    }

    private void UserSaved(DateTime at) => File.SetLastWriteTimeUtc(Csv, at);

    private async Task<TaskRunResult> Import(bool manual = false)
        => await new ManualImportTask(_paths).RunAsync(new TaskRunArgs(Manual: manual), CancellationToken.None);

    [Fact]
    public async Task 清单不存在_任务记没活_不报错()
    {
        var r = await Import();
        Assert.Equal(TaskState.Completed, r.State);
        Assert.True(r.NothingToDo);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task 整条链_没改不导_改了自动导_导过不再导()
    {
        Generated();
        await Import();
        Assert.Null(ManualFillStateStore.Load(_paths.ReportsDir)!.ImportedFileTimeUtc);   // 没导

        UserSaved(Edited);
        await Import();
        Assert.Equal(Edited, ManualFillStateStore.Load(_paths.ReportsDir)!.ImportedFileTimeUtc);

        // 同一版再跑：没活（记录不变）
        var again = await Import();
        Assert.True(again.NothingToDo);
        Assert.Equal(Edited, ManualFillStateStore.Load(_paths.ReportsDir)!.ImportedFileTimeUtc);
    }

    [Fact]
    public async Task 手动导入旧清单_顺手补上记录()
    {
        File.WriteAllText(Csv, "表头\n", new UTF8Encoding(true));
        UserSaved(Edited);
        await Import(manual: true);

        var st = ManualFillStateStore.Load(_paths.ReportsDir)!;
        Assert.Equal(Edited, st.ImportedFileTimeUtc);
        // 同一版：自动跑不再导；之后人再改，自动跑就会导
        Assert.NotEqual(ManualImportDecision.Import, ManualFillImportRule.Decide(true, Edited, st, manual: false));
        Assert.Equal(ManualImportDecision.Import, ManualFillImportRule.Decide(true, Edited.AddHours(1), st, manual: false));
    }

    [Fact]
    public void 改过没导入的清单_重新生成时不覆盖()
    {
        Generated();
        File.AppendAllText(Csv, "#601939,银行,#2026-06-30,npl_ratio,不良率,年报,第 1 页,,,1.23\n");
        UserSaved(Edited);
        var before = File.ReadAllText(Csv);

        var path = ManualFillWorklist.Generate(_paths.CurrentDb, _paths.ReportsDir, _paths.ReportsDir, []);

        Assert.Equal(Csv, path);
        Assert.Equal(before, File.ReadAllText(Csv));            // 人填的那一行还在
        Assert.Equal(Edited, File.GetLastWriteTimeUtc(Csv));
    }
}
