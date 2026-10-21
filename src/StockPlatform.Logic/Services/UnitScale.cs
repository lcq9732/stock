namespace StockPlatform.Logic.Services;

/// <summary>
/// 入库单位统一（2026-10-20，见 doc/unit-normalization-inventory.md）：一律存基本单位——股、元、份；
/// 比例一律存百分数。数据源给万股/万元/亿元/小数的，在 provider 解析时用这里换算，存量迁移也用同一个函数，
/// 保证"迁移出来的值"跟"新抓的值"逐位相同。
///
/// 按 decimal 乘：double 直接乘会乘出 4.44×1e8 = 444000000.00000006 这种尾巴
/// （腾讯成交额那串 224509199.99999997 就是这么来的）。double→decimal 取 15 位有效数字，
/// 源数据的位数远小于这个，所以换算是精确的。
/// </summary>
public static class UnitScale
{
    /// <summary>万 → 个（万股→股、万元→元）。</summary>
    public const decimal Wan = 10_000m;

    /// <summary>亿 → 个（亿元→元）。</summary>
    public const decimal Yi = 100_000_000m;

    /// <summary>小数 → 百分数（0.4268 → 42.68）。</summary>
    public const decimal Percent = 100m;

    public static double? Apply(double? value, decimal factor) => value is { } v ? Apply(v, factor) : null;

    public static double Apply(double value, decimal factor)
    {
        if (factor == 1m || double.IsNaN(value) || double.IsInfinity(value)) return value;
        try
        {
            return (double)((decimal)value * factor);
        }
        catch (OverflowException)
        {
            return value * (double)factor;
        }
    }
}
