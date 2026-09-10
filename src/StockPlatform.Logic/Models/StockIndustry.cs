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

/// <summary>
/// <c>StockIndustry.source</c> 的取值（2026-09-10）。
///
/// 这一列的用处**不只是"分得清哪行是谁给的"**：两个源的大类名分属证监会分类的不同修订版
/// （"开采辅助活动" vs "开采专业及辅助性活动"、"广播、电视、电影和影视录音制作业" vs
/// "…录音制作业"），新旧名一旦在库里并存，行业中性化就会把同一个行业拆成两个分组。
/// 所以这张表只能**整表一次换完**（见 SqliteIndustryRepository.ReplaceAll），
/// 而这一列就是"整表现在是哪一版"的凭据。
/// </summary>
public static class IndustrySources
{
    /// <summary>两所门类 + 新浪大类（2026-09-10 前的默认）。</summary>
    public const string Sina = "sina";

    /// <summary>东财 F10 两级 + 两所门类字母（2026-09-10 起的默认）。</summary>
    public const string EastMoney = "eastmoney";
}
