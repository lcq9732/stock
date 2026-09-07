using System.Text;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 读**东财终端客户端**落在本地的板块成分股文件（2026-09-06 新增）。
///
/// ════ 这是什么 ════
/// 东财 PC 客户端（东方财富终端）启动时会从服务端下发一批数据落盘，其中
/// <c>data\hs_bk_crc_data_new.dat</c> 就是全市场板块的成分股名单：1031 个板块、9.4 万条成分记录、
/// 不到 900 KB。**是东财自己的口径**（BK 编码、板块划分都和网页端一致），不是第三方近似——
/// 实测拿 BK0475 银行Ⅱ 跟 push2 接口对，两边都是 42 只；跟库里已抓到的 405 个板块逐只比，
/// 402 个完全一致，差异的 3 个是新股上市、库里还没收录。
///
/// 为什么值得专门读它：成分股原来要逐个板块打 push2（另外三条通道都是这么干的），
/// 一个板块一到三个请求、上千个板块跑一轮三小时起，还随时可能被限流打断——
/// 实测抓了几天也只攒到 405 个板块 21,041 条。这个文件一次读盘就是全量，零网络请求。
///
/// ════ 代价：依赖客户端定期启动 ════
/// 文件只在客户端启动时刷新。人不开客户端，文件就一直是旧的，而且**旧得毫无征兆**——
/// 内容完整、格式正确、解析毫无问题，只是停留在上次开客户端那天。所以
/// <see cref="Load"/> 强制检查文件时间，超过 <see cref="DefaultMaxAge"/> 直接判定不可用：
/// 宁可这一轮不更新，也不能把陈数据写进库当成新的（写进去还会顺带刷新
/// BoardMemberFetchState 的时间戳，接下来 7 天都不会再抓，陈旧会被放大一倍）。
///
/// ════ 格式（实测出来的，没有文档）════
/// 首行是全局 CRC，之后每行一个板块，分号分隔：
/// <code>
///   18694189;90.BK0475;890698431;2;475;银行Ⅱ;0.000001,0.001227,...,1.603323,
///   ── crc1 ──  板块码   ── crc2 ─ 级别 数字码 板块名 ── 成分股：市场.代码 ──
/// </code>
/// 级别：1=地域 2=行业 3=概念 4=指数/风格；成分股市场号 0=深 1=沪。
///
/// 三个踩过的坑，改这里之前先看：
///   ① 编码是 **GBK**，而整行是按 Latin1 解码的（每字节映射一个字符、永不抛异常），
///      只有板块名那一个字段单独转回字节、用 GBK 解一次（见 <see cref="DecodeGbk"/>）。
///      为什么不整行用 GBK：成分股那串代码占了全行 99% 的长度，全是 ASCII；万一哪天
///      混进一个非法字节，整行 GBK 解码会连代码一起毁掉，而 Latin1 不会。
///      板块名解不出来也只是名字难看，不影响成分股——**坏一个字段不该带走整行**。
///   ② 行尾**会变**：2026-09-04 那版是 CRLF，09-06 那版变成了 LF。所以不能只按 \n 切完就用，
///      必须把行尾的 \r 也去掉——否则每个板块的最后一只成分股会带上 \r，比对时凭空多出差异
///      （实测踩过：解析出来"1031 个板块全部发生变化"，全是这个 \r 造成的假象）。
///   ③ 成分股列表**带尾随逗号**，切完最后会多一个空串，要丢掉。
/// </summary>
public sealed class EastMoneyTerminalBoardFile
{
    /// <summary>东财终端的默认安装位置。装到别处就在设置里配 <c>TerminalBoardFile</c>。</summary>
    public const string DefaultPath = @"C:\eastmoney\dfcf\data\hs_bk_crc_data_new.dat";

    /// <summary>
    /// 文件多久没刷新就算不可用，默认 3 天。
    ///
    /// 为什么不取 7 天（跟 FetchOrchestrator.BoardMemberFreshFor 一致）：那个值的含义是
    /// "成分股抓过 7 天之内不用重抓"。如果这里也放到 7 天，就会出现"用一份 7 天前的文件
    /// 更新库，并把抓取时间标记成今天"，接下来 7 天不再抓——库里的数据最长会陈到 14 天。
    /// 取一半，让两者叠加之后仍在 10 天以内。
    /// </summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(3);

    /// <summary>
    /// 板块数下限。低于这个数就认为文件是半成品或者格式变了，整份拒绝。
    ///
    /// 实测正常是 1031 个（31 地域 + 496 行业 + 400 概念 + 104 指数/风格）。取 200 是留足余量：
    /// 东财下架一批概念板块也远到不了这个数；而真出问题时（文件写了一半、格式改版）
    /// 通常是断崖式地少，不会不多不少正好落在 200 以上。
    /// </summary>
    private const int MinBoardCount = 200;

    /// <summary>
    /// .NET Core 默认不带 GBK（936），要先把 CodePages 提供程序注册进去，不然
    /// <c>Encoding.GetEncoding(936)</c> 直接抛。放静态构造里，用到这个类时自动生效。
    /// 重复注册是安全的（内部幂等），所以不用管别处有没有也注册过。
    /// </summary>
    static EastMoneyTerminalBoardFile()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly string _path;
    private readonly TimeSpan _maxAge;

    private Dictionary<string, List<string>>? _members;
    private Dictionary<string, int>? _levels;
    private Dictionary<string, string>? _names;
    private DateTime _loadedFileTime;
    private long _loadedFileLength;

    public EastMoneyTerminalBoardFile(string? path = null, TimeSpan? maxAge = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    public string Path => _path;

    /// <summary>文件本身的最后写入时间——也就是"客户端上一次下发数据"的时刻。</summary>
    public DateTime FileTime => _loadedFileTime;

    public bool IsLoaded => _members != null;
    public int BoardCount => _members?.Count ?? 0;
    public int MemberCount => _members?.Values.Sum(v => v.Count) ?? 0;

    /// <summary>
    /// 加载并校验。<b>不抛异常</b>：返回 false 加一句人话，由调用方决定是报警还是换通道——
    /// 这是"通道能不能用"的判定，不是"这一次取数失败"，抛出去只会让上层分不清两者。
    /// </summary>
    public bool Load(out string message)
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists)
            {
                message = $"找不到东财终端的板块文件：{_path}。"
                        + "请确认已安装东方财富终端；装在别处就在设置里配 TerminalBoardFile。";
                return false;
            }

            var age = DateTime.Now - fi.LastWriteTime;
            if (age > _maxAge)
            {
                message = $"东财终端的板块文件已经 {age.TotalDays:F1} 天没刷新了"
                        + $"（{fi.LastWriteTime:yyyy-MM-dd HH:mm}，上限 {_maxAge.TotalDays:F0} 天）。"
                        + "这个文件只在客户端启动时下发——请开一次东方财富终端，等两分钟让它下发完再跑。";
                return false;
            }

            var (members, levels, names) = Parse(File.ReadAllBytes(_path));
            if (members.Count < MinBoardCount)
            {
                message = $"东财终端的板块文件只解析出 {members.Count} 个板块（正常上千个），"
                        + "文件可能写了一半或者格式变了，本轮不用它。";
                return false;
            }

            _members = members;
            _levels = levels;
            _names = names;
            _loadedFileTime = fi.LastWriteTime;
            _loadedFileLength = fi.Length;

            var total = members.Values.Sum(v => v.Count);
            message = $"东财终端板块文件已加载：{members.Count} 个板块、{total:N0} 条成分"
                    + $"（文件时间 {fi.LastWriteTime:yyyy-MM-dd HH:mm}）。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"读东财终端板块文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>文件被客户端刷新过（时间或长度变了）就重新加载；没变则什么也不做。</summary>
    public bool ReloadIfChanged(out string message)
    {
        message = "";
        if (_members == null) return Load(out message);
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists) return true;                       // 加载过就先用着，下一轮 Load 会报错
            if (fi.LastWriteTime == _loadedFileTime && fi.Length == _loadedFileLength) return true;
            return Load(out message);
        }
        catch { return true; }                                 // 探测失败不影响已经加载好的内容
    }

    /// <summary>
    /// 取某个板块的成分股（6 位代码，已去掉市场前缀）。
    /// 板块不在文件里返回 null——调用方**必须**把它跟"这个板块成分股为空"区别对待：
    /// 上游 UpsertBoards 是快照语义，拿一个空列表去更新会把这个板块的成分股全删掉。
    /// </summary>
    public List<string>? TryGetMembers(string boardCode)
        => _members != null && _members.TryGetValue(boardCode, out var v) ? v : null;

    /// <summary>板块级别：1=地域 2=行业 3=概念 4=指数/风格；不在文件里返回 0。</summary>
    public int LevelOf(string boardCode)
        => _levels != null && _levels.TryGetValue(boardCode, out var v) ? v : 0;

    /// <summary>板块名（GBK 解出来的）；没有返回空串。</summary>
    public string NameOf(string boardCode)
        => _names != null && _names.TryGetValue(boardCode, out var v) ? v : "";

    /// <summary>
    /// 把文件里的板块拼成一份**名单**，给板块列表那一步用（2026-09-06）。
    ///
    /// 级别怎么映射到 <see cref="BoardType"/>：
    ///   · 1（地域）→ <see cref="BoardType.Region"/>
    ///   · 2（行业）→ <see cref="BoardType.Industry"/>
    ///   · 3（概念）、4（指数/风格）→ <see cref="BoardType.Concept"/>
    ///
    /// 级别 4 是"指数/风格"（HS300_、AH股、AB股这些），归进概念而不是单开一类：
    /// 它们在东财自己的界面上也是跟概念混在一起的，而且只有 104 个。
    ///
    /// 冒出没见过的级别时**跳过**而不是猜一个：宁可少一类，也不要把一批来路不明的板块
    /// 混进概念，那会让概念的快照护栏（按数量比对）失真。
    ///
    /// 行情字段（涨跌幅/成交额/领涨股）全部留 0/空：这一步本来就不取行情
    /// （由【板块指数合成】用本地日K回填），而且 CommitBoardListFromMenu 会把库里
    /// 现有的值带过来，不会把已经填好的清零。
    /// </summary>
    public List<Board> BuildBoardList()
    {
        var list = new List<Board>();
        if (_levels == null) return list;

        foreach (var (code, level) in _levels)
        {
            BoardType type;
            switch (level)
            {
                case 1: type = BoardType.Region; break;
                case 2: type = BoardType.Industry; break;
                case 3:
                case 4: type = BoardType.Concept; break;
                default: continue;                 // 将来可能冒出来的新级别，宁可少一类
            }
            list.Add(new Board
            {
                BoardCode = code,
                Type = type,
                Name = NameOf(code),
                MemberCount = _members != null && _members.TryGetValue(code, out var m) ? m.Count : 0,
                AsOf = _loadedFileTime,
            });
        }
        return list;
    }

    /// <summary>所有板块代码。</summary>
    public IReadOnlyCollection<string> BoardCodes
        => (IReadOnlyCollection<string>?)_members?.Keys ?? Array.Empty<string>();

    /// <summary>
    /// 解析文件内容。独立成静态方法是为了能直接喂字节数组测，不必在测试里造真实路径。
    /// </summary>
    internal static (Dictionary<string, List<string>> Members, Dictionary<string, int> Levels,
                     Dictionary<string, string> Names)
        Parse(byte[] bytes)
    {
        // Latin1：单字节逐一映射，遇到 GBK 中文也不会抛（见类注释①）。板块名那一个字段
        // 单独转回字节用 GBK 解，其余全是 ASCII，直接用。
        var text = Encoding.Latin1.GetString(bytes);
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = text.Split('\n');
        for (int i = 1; i < lines.Length; i++)                  // 从 1 开始：首行是全局 CRC
        {
            var line = lines[i].TrimEnd('\r');                  // CRLF/LF 都出现过（见类注释②）
            if (line.Length == 0) continue;

            var parts = line.Split(';');
            if (parts.Length < 7) continue;

            // parts[1] 形如 "90.BK0475"，取点号之后那截
            var code = StripMarket(parts[1]);
            if (code.Length == 0) continue;

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parts[6].Split(','))
            {
                if (item.Length == 0) continue;                 // 尾随逗号留下的空串（见类注释③）
                var s = StripMarket(item);
                if (s.Length > 0 && seen.Add(s)) list.Add(s);
            }

            members[code] = list;
            levels[code] = int.TryParse(parts[3], out var lv) ? lv : 0;
            names[code] = DecodeGbk(parts[5]);
        }

        return (members, levels, names);
    }

    /// <summary>去掉 "90.BK0475" / "0.000001" 里的市场前缀，并去掉首尾空白。</summary>
    private static string StripMarket(string s)
    {
        int dot = s.LastIndexOf('.');
        return (dot >= 0 ? s[(dot + 1)..] : s).Trim();
    }

    /// <summary>
    /// 把 Latin1 读进来的那一段转回原始字节，再按 GBK 解一次——整行是 Latin1 解的，
    /// 而 Latin1 是单字节一一映射，所以转回去是无损的。
    ///
    /// 解不出来就返回空串而不是抛：板块名坏掉只是名字难看，成分股照样能用，
    /// 不该因为一个字段把整份文件废掉。
    /// </summary>
    private static string DecodeGbk(string latin1)
    {
        try
        {
            return Encoding.GetEncoding(936).GetString(Encoding.Latin1.GetBytes(latin1)).Trim();
        }
        catch { return ""; }
    }
}
