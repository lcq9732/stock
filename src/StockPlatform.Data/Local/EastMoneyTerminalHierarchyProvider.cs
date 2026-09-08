using System.IO;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Local;

/// <summary>
/// 从**东财终端的本地文件**读板块层级树（2026-09-07）。不联网，不消耗任何抓取配额。
///
/// 为什么需要它：我们库里只有"股票 → 属于哪个板块 + 第几级"，**没有"板块 → 父板块"**。
/// 少了这层关系，就没法把三级行业往上卷成一级行业来看——而风口分析里"这波钱落在哪个大行业"
/// 恰恰要按一级聚合。东财网页侧不提供这个数据（查过：/cjhy/ 不存在、站内搜只有资讯文章、
/// 板块页面连"产业链""上游"字样都没有），但终端把它落在了本地。
///
/// ════ 文件格式（逆向出来的，2026-09-07）════
/// <c>C:\eastmoney\dfcf\data\IndustryBlockRelation.dat</c>，约 14.5KB：
///
///   0x00-0x43  头部（32 字节像 MD5 的十六进制串 + 一些元数据）
///   0x45 起    定长记录区，**每条 9 字节**：
///                [0..5] 板块代码 "BK0420"
///                [6]    0x00 分隔
///                [7]    0x00（恒为 0，用途不明）
///                [8]    层级 1/2/3
///              全 0x00 的 9 字节是**空槽**（占位/分段用），跳过即可。
///
/// 验算：(14577 - 0x45) / 9 = 1612 槽 = 1273 条有代码 + 339 个空槽，分毫不差。
///
/// ⚠ 记录是**成对的父子边** (父,子)(父,子)…，但**不能按位置死配**：有效记录 1273 条是奇数，
///   中间夹着 338 条落单的，从第 145 条起就整体错开半格。所以配对要能**重新同步**：
///   下一条的层级正好是这一条 +1 才算一对、前进两格；否则把这一条当落单的，只前进一格。
///
/// ⚠⚠ 这里走过一次弯路，值得写下来：先用的是"层级栈"（记住最近见到的 N 级，后面的 N+1 级
///    就挂到它下面）。那个版本同样解出 467 条边、层级数字**全对**，看着毫无破绽——
///    实际上 51 条边的父是错的，"银行Ⅱ 挂在石油石化下""房地产开发挂在食品饮料下"。
///    错位之后每个子板块还是会挂到某个层级正确的父上，所以**光对层级数字发现不了**。
///    教训：校验要对**父子归属**，不能只对层级。
///
/// 实测结果：467 条边（一级→二级 128 条、二级→三级 339 条），跟 StockIndustryEm 还原出的
/// 真实父子链逐条比对 **465 条一模一样、0 条父不同、0 条遗漏**，另有 2 条是文件独有
/// （BK1362 / BK1324，我们的板块表里没有这两个）。
/// </summary>
public class EastMoneyTerminalHierarchyProvider
{
    /// <summary>
    /// 东财终端默认装在这儿；换了地方就在设置里配 <c>TerminalHierarchyFile</c>。
    /// 跟 <see cref="Remote.EastMoneyTerminalBoardFile.DefaultPath"/> 一个套路——存全路径而不是目录，
    /// 免得两处配置一个填目录一个填文件、用的人得记住哪个是哪个。
    /// </summary>
    public const string DefaultPath = @"C:\eastmoney\dfcf\data\IndustryBlockRelation.dat";

    private const int HeaderSize = 0x45;
    private const int RecordSize = 9;

    public event Action<string>? OnStatus;

    public EastMoneyTerminalHierarchyProvider(string? filePath = null)
        => FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath!;

    public string FilePath { get; }

    /// <summary>
    /// 文件在不在。不在**不是错误**——别人机器上可能压根没装东财终端，
    /// 那就是这一项没数据，不该让整个板块抓取跟着失败。
    /// </summary>
    public bool FileExists => File.Exists(FilePath);

    /// <summary>
    /// 读出所有父子关系。文件不存在或格式不对时返回空列表（调用方按"没数据"处理）。
    /// </summary>
    public List<BoardHierarchyEdge> Read()
    {
        if (!FileExists)
        {
            OnStatus?.Invoke($"没找到东财终端的层级文件（{FilePath}），跳过板块层级树。");
            return [];
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"⚠ 读东财终端层级文件失败：{ex.Message}，跳过板块层级树。");
            return [];
        }

        return Parse(raw);
    }

    /// <summary>
    /// 纯解析，不碰文件系统——这样能拿真实字节做单元测试。格式说明见类注释。
    /// </summary>
    public static List<BoardHierarchyEdge> Parse(byte[] raw)
    {
        var edges = new List<BoardHierarchyEdge>();
        if (raw.Length <= HeaderSize) return edges;

        // ── 先把有效记录挑出来 ──
        // 空槽只是占位，跳过就行，**不能因此中断扫描**——文件里有 339 个，
        // 早期版本遇到它就停，结果只读出了一小截。
        var recs = new List<(string Code, int Level)>();
        for (int off = HeaderSize; off + RecordSize <= raw.Length; off += RecordSize)
        {
            if (raw[off] == 0 && raw[off + 1] == 0) continue;
            if (raw[off] != (byte)'B' || raw[off + 1] != (byte)'K') continue;

            int level = raw[off + 8];
            if (level is < 1 or > 3) continue;      // 只认 1/2/3，别的当脏数据
            recs.Add((System.Text.Encoding.ASCII.GetString(raw, off, 6), level));
        }

        // ── 两两配成 (父,子)，配不上就重新同步 ──
        // 死按位置配是不行的：1273 条有效记录是奇数，中间的落单记录会把后面整体错开半格。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < recs.Count - 1; )
        {
            var (parent, parentLevel) = recs[i];
            var (code, level) = recs[i + 1];

            if (level != parentLevel + 1)
            {
                i++;        // 这条落单了（实测 338 条），只前进一格，下一轮拿它当父再试
                continue;
            }

            // 同一个子板块只记第一次——实测没有多父冲突，真出现了也以先到的为准，
            // 而不是让后面的把前面的覆盖掉（那样结果会依赖文件顺序，不可复现）。
            if (seen.Add(code))
                edges.Add(new BoardHierarchyEdge(code, parent, level, parentLevel));
            i += 2;
        }
        return edges;
    }
}
