using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 公告搜索的按年切片。存在的理由见 <see cref="CalendarYearSlicer"/>：巨潮那个搜索源对单次搜索
/// 有 30 页硬上限，区间回补一填二十几年就会翻满即停、剩下的**静默丢掉**。
///
/// 这里最要紧的是 <see cref="SlicesCoverExactlyTheOriginalRange"/>：切片的并集必须严格等于原区间，
/// 首尾两片不能被扩成整年——扩了就会去搜用户没要的日期，缩了就是又一处静默漏数据。
/// </summary>
public class CalendarYearSlicerTests
{
    [Fact]
    public void SingleYearRangeStaysOnePiece()
    {
        var slices = CalendarYearSlicer.Split(new DateOnly(2024, 3, 1), new DateOnly(2024, 9, 30));

        Assert.Single(slices);
        Assert.Equal((new DateOnly(2024, 3, 1), new DateOnly(2024, 9, 30)), slices[0]);
    }

    /// <summary>日常增量走的就是这条路（同一天的窗口）——切片不该改变它的行为。</summary>
    [Fact]
    public void SingleDayRangeStaysOnePiece()
    {
        var day = new DateOnly(2026, 9, 7);
        var slices = CalendarYearSlicer.Split(day, day);

        Assert.Single(slices);
        Assert.Equal((day, day), slices[0]);
    }

    [Fact]
    public void SplitsOnCalendarYearBoundariesKeepingOriginalEnds()
    {
        var slices = CalendarYearSlicer.Split(new DateOnly(2022, 5, 10), new DateOnly(2024, 2, 20));

        Assert.Equal(3, slices.Count);
        Assert.Equal((new DateOnly(2022, 5, 10), new DateOnly(2022, 12, 31)), slices[0]);  // 首片保留原起日
        Assert.Equal((new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31)), slices[1]);   // 中间片是整年
        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 20)), slices[2]);    // 末片保留原止日
    }

    /// <summary>切片的并集 == 原区间：不多搜一天、也不漏一天。</summary>
    [Fact]
    public void SlicesCoverExactlyTheOriginalRange()
    {
        var start = new DateOnly(1990, 12, 19);   // A股开市首日，区间回补钳到的那天
        var end = new DateOnly(2026, 9, 7);
        var slices = CalendarYearSlicer.Split(start, end);

        Assert.Equal(start, slices[0].Start);
        Assert.Equal(end, slices[^1].End);
        for (int i = 1; i < slices.Count; i++)
            Assert.Equal(slices[i - 1].End.AddDays(1), slices[i].Start);   // 首尾相接、无缝无叠
    }

    /// <summary>三十几年的区间要切出三十几片——这正是"每片各享 30 页额度"的由来。</summary>
    [Fact]
    public void LongRangeYieldsOnePiecePerYear()
    {
        var slices = CalendarYearSlicer.Split(new DateOnly(1990, 12, 19), new DateOnly(2026, 9, 7));

        Assert.Equal(2026 - 1990 + 1, slices.Count);
    }

    [Fact]
    public void InvertedRangeYieldsNothing()
    {
        Assert.Empty(CalendarYearSlicer.Split(new DateOnly(2024, 5, 1), new DateOnly(2024, 4, 30)));
    }
}
