namespace StockPlatform.Logic.Services;

/// <summary>
/// 个股行业/题材归属抓取时「这一批什么时候提交」的判据（2026-09-22）——纯函数，零 IO。
///
/// ════ 为什么这条规则值得单独有个名字 ════
/// 落库那侧是**按票整只替换**（先删这只票的旧行、再插新的，见
/// <c>IStockBoardMapRepository.ReplaceForStocks</c>）。于是"一批从哪切"不再只是性能取舍，
/// 而是**正确性前提**：一只票的行被切成两批的话，第二批的 DELETE 会把第一批刚写进去的
/// 那几行删掉——表现出来只是"这只票的归属少了几条"，没有任何报错。
///
/// 所以攒够行数之后还要等**下一只票开始**才提交。同一只票的行必然连续，这一点由那条排序键
/// （<c>SECURITY_CODE,BOARD_CODE</c>）保证——它本来是为了翻页不重不漏才加的，这里正好用上。
/// </summary>
public static class StockBoardMapBatchRule
{
    /// <summary>一批攒多少行才考虑提交。9.4 万行全市场 ⇒ 约 20 批。</summary>
    public const int BatchRows = 5000;

    /// <summary>
    /// 现在该提交这一批吗。
    ///
    /// ⚠ 调用点必须在**把当前这一行加进批之前**判：加进去之后再提交的话，新那只票的第一行
    /// 会跟着上一批走，而它其余的行落在下一批——下一批整只替换时就把那第一行删了。
    /// </summary>
    /// <param name="pendingRows">批里现在攒了多少行（行业 + 题材）。</param>
    /// <param name="lastCode">上一行那只票的代码；null＝这一批还什么都没有。</param>
    /// <param name="currentCode">当前这一行那只票的代码。</param>
    public static bool ShouldFlush(int pendingRows, string? lastCode, string currentCode)
        => pendingRows >= BatchRows
           && lastCode != null
           && !string.Equals(lastCode, currentCode, StringComparison.Ordinal);
}
