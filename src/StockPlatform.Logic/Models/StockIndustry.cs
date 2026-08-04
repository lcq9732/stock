namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只股票的证监会行业分类。分两级：
/// **门类**（A~S 共19类，如 "C 制造业"）——两所官网提供，覆盖沪深全部，但太粗（制造业占全市场六成）；
/// **大类**（84类，如 "汽车制造业"）——新浪提供，粒度合适，但只覆盖约 3200 只。
/// 所以两个都存：展示和中性化都优先用大类、缺失时退回门类，见 <see cref="Best"/>。
/// </summary>
public class StockIndustry
{
    public string Code { get; set; } = "";
    /// <summary>门类代码（单字母 A~S），无则空。</summary>
    public string ClassCode { get; set; } = "";
    /// <summary>门类名称，如"制造业"。</summary>
    public string ClassName { get; set; } = "";
    /// <summary>大类名称，如"汽车制造业"；新浪没收录该股时为空。</summary>
    public string MajorName { get; set; } = "";

    /// <summary>可用的最细一级：优先大类，退回门类，都没有则空字符串。</summary>
    public string Best => !string.IsNullOrEmpty(MajorName) ? MajorName : ClassName;
}
