namespace StockPlatform.Logic.Models;

/// <summary>
/// 板块树上的一条父子边（2026-09-07）。
///
/// 只有**行业板块**有层级：实测 488/496 个行业在树里，概念板块 504 个和地区板块 31 个
/// 一个都没有——它们本来就是平的，没有上下级这回事。
/// 一级行业就是申万那 23 个（公用事业/电子/计算机/机械设备…）。
/// </summary>
/// <param name="BoardCode">子板块。</param>
/// <param name="ParentCode">父板块。</param>
/// <param name="Level">子板块**自己**的层级（2 或 3）。</param>
/// <param name="ParentLevel">父板块的层级（1 或 2）。存下来只为校验，不入库——
/// 入库后顺着 ParentCode 找到那一行读它的 board_level 就是。</param>
public readonly record struct BoardHierarchyEdge(
    string BoardCode, string ParentCode, int Level, int ParentLevel);
