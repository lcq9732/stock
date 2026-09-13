using System.IO;
using System.Text;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 个股分析笔记的读写（2026-08-27 新增）——一票一个 <c>data\notes\{code}.md</c>，见
/// <see cref="Data.Orchestration.AnalyzerPaths.NotesDir"/> 的注释（为什么存判断而不是存数据）。
///
/// 刻意做得很薄：没有版本、没有锁、没有并发控制。笔记是单人手写的东西，整份读、整份写就够了；
/// 真要看历史版本，这个目录可以直接进 git。
/// </summary>
public class StockNoteStore
{
    private readonly string _notesDir;

    public StockNoteStore(string notesDir)
    {
        _notesDir = notesDir;
    }

    public string PathOf(string code) => Path.Combine(_notesDir, $"{code}.md");

    /// <summary>
    /// 从笔记里摘出**一句话的个人观点**，给【观察项】页那列显示（2026-09-11）。
    ///
    /// 约定：正文里以 <c>观点：</c> 开头的那一行就是它，前面可以带 markdown 的
    /// <c>&gt;</c>、<c>-</c>、<c>#</c> 等符号。取**第一条**。例：
    /// <code>
    /// &gt; 观点：估值压到18x，压制来自政策成本+去宁化；回购是情绪转折点
    /// </code>
    ///
    /// 为什么要个标记而不是取首行：笔记是自由 markdown，首行往往是标题或日期。
    /// 但也**只认这一个标记**——不强制模板，其余内容随便写。
    /// 没写就返回 null，那一列留空，不影响任何判断。
    /// </summary>
    public string? ReadOpinion(string code)
        => Logic.Services.NoteOpinionParser.Parse(Read(code));

    public bool Exists(string code) => File.Exists(PathOf(code));

    /// <summary>读笔记；文件不存在时返回 null（调用方据此决定要不要给新建模板）。</summary>
    public string? Read(string code)
    {
        var path = PathOf(code);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception)
        {
            // 读不了（被占用/权限）不能让窗口开不出来，当成没有笔记
            return null;
        }
    }

    /// <summary>整份写回。目录不存在时自动建。内容为空白则**删除**文件——空笔记留着只会让
    /// "哪些票有笔记"这个判断失真。</summary>
    public void Save(string code, string content)
    {
        Directory.CreateDirectory(_notesDir);
        var path = PathOf(code);
        if (string.IsNullOrWhiteSpace(content))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>有笔记的股票代码集合——供列表页标记"这只票记过东西"。</summary>
    public HashSet<string> CodesWithNotes()
    {
        try
        {
            if (!Directory.Exists(_notesDir)) return new HashSet<string>(StringComparer.Ordinal);
            return Directory.EnumerateFiles(_notesDir, "*.md")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(StringComparer.Ordinal)!;
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 新建笔记时的模板。开头那段说明是有意写进文件的——半年后自己回来看，最容易犯的错就是
    /// 把能从库里重算的数据抄一遍到笔记里，然后两边不一致。
    /// </summary>
    public static string NewTemplate(string code, string name, DateTime today) =>
        $"""
        # {code} {name}

        > 分析笔记（由 A股批量分析程序管理）。
        > **能从库里算出来的数据不要抄到这里** —— 财务科目已扩到 52 个，营运资金拆解、净利率归因、
        > 收现比、有息负债、ROE 这些随时能重算，抄进来只会两边不一致。
        > 这里只记三样东西：**判断**、**疑点/待验证**、**下次要盯什么**。

        ## {today:yyyy-MM-dd}

        ### 判断

        -

        ### 疑点 / 待验证

        -

        ### 下次要盯

        -

        """;

    /// <summary>在已有笔记里插入"今日"小节——插在正文最前面（标题和说明之后、第一个 ## 之前），
    /// 让最新的分析在最上面，不用每次滚到文件底部。同一天已经有小节了就原样返回。</summary>
    public static string InsertTodaySection(string content, DateTime today)
    {
        string header = $"## {today:yyyy-MM-dd}";
        if (content.Contains(header, StringComparison.Ordinal)) return content;

        var section = $"""
            {header}

            ### 判断

            -

            ### 疑点 / 待验证

            -

            ### 下次要盯

            -


            """;

        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        int at = lines.FindIndex(l => l.StartsWith("## ", StringComparison.Ordinal));
        if (at < 0)
        {
            // 还没有任何日期小节，加到末尾
            return content.TrimEnd() + "\n\n" + section;
        }
        lines.Insert(at, section.TrimEnd('\n') + "\n");
        return string.Join("\n", lines);
    }
}
