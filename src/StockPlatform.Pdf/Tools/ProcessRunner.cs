using System.Diagnostics;
using System.Text;

namespace StockPlatform.Pdf.Tools;

/// <summary>一次外部程序调用的结果。</summary>
/// <param name="Completed">跑完了没（没超时、没启动失败）。false 时另外三个字段无意义。</param>
internal readonly record struct ProcessResult(bool Completed, int ExitCode,
                                              string StdOut, string StdErr)
{
    /// <summary>跑完了**而且**退出码为 0。</summary>
    public bool Ok => Completed && ExitCode == 0;
}

/// <summary>
/// 跑一个外部命令行程序。无状态纯函数，所以留作静态——"对象化"针对的是有状态、有协作的类，
/// 这种一进一出的工具函数包成对象只是多一层。
///
/// 拆出来是因为原来 PdfTextExtractor 和 PdfOcrExtractor 各写了一份**一模一样**的
/// 启动/读流/超时/强杀逻辑，而这段逻辑有个非改不可的坑（见 Run 里的说明），
/// 复制两份迟早改漏一份。
///
/// ⚠ **成功与否由调用方判**，这里只如实回报 <see cref="ProcessResult.ExitCode"/>。
///   这两个调用方的判据本来就不一样，不能统一：
///     · pdftotext ── 只看输出文件在不在，**不看退出码**（它对字体有问题的 PDF 会报非 0，
///       但文本照样转出来了；改成看退出码会让那些 PDF 的兜底路径直接失效）
///     · tesseract / pdftoppm ── 看 ExitCode == 0
/// </summary>
internal static class ProcessRunner
{
    public static ProcessResult Run(string exe, string args, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return default;

            // ⚠ **重定向了就必须把流读掉，否则会死锁。**
            // pdftotext 对字体有问题的 PDF 会往 stderr 狂刷警告（中国人保那份每页都报
            // "Mismatch between font type and embedded font file"）。父进程不读，管道缓冲区
            // 一满子进程就卡住写不动，这边 WaitForExit 干等到超时、把它杀掉、返回失败——
            // 表现就是"手动在终端跑 0.6 秒出结果，程序里却说读不出文本层"。
            //
            // 同步 ReadToEnd 两条流会互相阻塞（一条读完才轮到另一条），所以用异步收集。
            var so = new StringBuilder();
            var se = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return default;
            }
            // WaitForExit(int) 返回时异步读取可能还没收完，无参版会把剩下的等干净
            try { proc.WaitForExit(); } catch { }

            return new ProcessResult(true, proc.ExitCode, so.ToString(), se.ToString());
        }
        catch { return default; }
    }
}
