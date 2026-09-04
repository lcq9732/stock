using System.Text.Json;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财 JSON 里那几个**同一个含义、写法却各不相同**的字段的统一解析（2026-09-04）。
/// </summary>
public static class EastMoneyJson
{
    /// <summary>
    /// 布尔字段。东财**同一批接口里就有三种写法**：业绩预告的 <c>IS_LATEST</c> 给的是
    /// 字符串 <c>"T"</c>，个股题材的 <c>IS_PRECISE</c> 给的是数字 <c>1</c>，还有的给 <c>"true"</c>。
    ///
    /// 这个函数是踩出来的：业绩预告原来写死 <c>== "1"</c>，接口给的却是 <c>"T"</c>，
    /// 结果 14.5 万行 <c>is_latest</c> **全是 0**——不报错、不缺行，只是这一列悄悄全错。
    /// 这类"值域错但结构对"的问题体检时最难发现，所以布尔一律走这里，不要在各处自己比。
    /// </summary>
    public static bool Bool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => (v.GetString() ?? "").Trim().ToUpperInvariant()
                                    is "T" or "1" or "TRUE" or "Y" or "YES",
            _ => false,
        };
    }
}
