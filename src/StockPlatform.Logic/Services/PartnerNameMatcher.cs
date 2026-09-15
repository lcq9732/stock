using System.Text.RegularExpressions;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把年报里的**交易对手名**还原成 A 股代码（2026-09-08，2026-09-15 大改）。纯计算，不碰库、不联网。
///
/// ════ 六档，按置信度从高到低 ════
///   <see cref="Exact"/>       全称一字不差
///   <see cref="Short"/>       等于该公司的证券简称（5561 个简称**零重名**，所以也是精确的）
///   <see cref="Qualified"/>   全称 + 限定词（"…股份有限公司<b>及其子公司</b>"、"…<b>宁夏分公司</b>"）
///   <see cref="Subsidiary"/>  年报「合并财务报表范围」里的子公司 → 归并到母公司
///   <see cref="ParentGroup"/> 对手是那家上市公司的**非上市母集团**（"中国铝业集团有限公司"）
///   <see cref="Normalized"/>  去空白/括号/公司后缀之后相等
///
/// <b>仍然不做"含简称"的模糊匹配</b>。2026-09-15 又验了一次，结论没变：按"名字里含某上市公司简称"
/// 去捞，能多出 5038 个名字，但里面混着大量地名和通用词简称造的假阳性——「连云港威勒斯新能源」
/// 撞 601008 连云港、「海宁正泰太阳能」撞 000591 太阳能、「钛虎机器人科技」撞 300024 机器人。
/// 产业链数据有错边比没有更糟：没有的时候你知道自己不知道，有错边的时候你会照着它做判断。
///
/// ════ ⚠ 2026-09-15：归一化档曾经有三分之二是错的 ════
/// 后缀正则原来包含 <c>集团有限公司$</c> 和 <c>集团$</c> 两项，而**区分母公司和上市子公司的
/// 唯一信息就在这两个后缀里**：
/// <code>
///   中国铝业集团有限公司  →去后缀→  中国铝业    ← 非上市母公司
///   中国铝业股份有限公司  →去后缀→  中国铝业    ← 601600
/// </code>
/// 它把答案擦掉，然后问这两个是不是同一家。实测 2025 年报 938 条边里 146 条是这么来的
/// （占 17%，但母集团个头大，吃掉 44% 的金额）。删掉那两项后，144 条自动消失、
/// 73 条正确的边一条没伤。剩下的（"申能(集团)有限公司"这种括号写法）由
/// <see cref="IsParentGroup"/> 兜住。
///
/// 这些边**不是垃圾**——"贝肯能源 74% 的营收来自中石油集团"这句话是真的，错的只是把
/// 中石油集团贴上了 601857 这个代码。所以归 <see cref="ParentGroup"/> 单独一档，不删。
///
/// ════ 实测的天花板 ════
/// 2025 年报 43511 行对手名里，**匿名占位 29636 行（68%）**——"客户一""第二名"这种，
/// 永远解不了，这才是真天花板。剩下 13875 行真名里，绝大多数是非上市小公司（"苏州同成化工"），
/// 也解不了。改版前命中真名的 6.9%。
/// </summary>
public static class PartnerNameMatcher
{
    /// <summary>
    /// 匹配规则的版本。**改了判据就 +1**，任务据此对已经匹配过的名字全量重算
    /// （见 CustomerSupplierTask.MatchPartners）——跟 SubsidiaryParser.ParserVersion 同一套路。
    ///
    /// ⚠ 没有它的话，规则改好了也只对"还没匹配过"的名字生效：
    ///   GetPartnerNamesToMatch 默认只捞 <c>partner_code IS NULL</c> 的，而错边的
    ///   partner_code 不是 NULL、是错值，永远不会被重新评估。
    ///
    /// v1 → v2（2026-09-15）：后缀正则删「集团」两项、新增 Short/Qualified/ParentGroup 三档。
    /// </summary>
    public const int MatcherVersion = 2;

    /// <summary>匹配结果的档次，落库进 <c>match_type</c>。</summary>
    public const string Exact = "exact";
    public const string Normalized = "normalized";

    /// <summary>
    /// 对手名 == 该公司的**证券简称**（2026-09-15）。年报里大量对手方就写"三一重工""东方电气"。
    ///
    /// 这一档跟"含简称的模糊匹配"是两回事：这里要求**完全相等**，而 5561 个 A 股简称实测
    /// <b>零重名</b>，所以它跟全称精确一样没有歧义空间。实测能多认出 651 个名字。
    ///
    /// ⚠ **必须排在 <see cref="ParentGroup"/> 护栏之前**：「美的集团」「宁德时代」这类简称
    ///   本身带"集团"二字，护栏会把它们当母集团拦掉。
    ///
    /// ⚠ 只收 ≥3 字的简称。2 字简称只有"柳工"一个，放进去挡不住误撞。
    /// </summary>
    public const string Short = "short";

    /// <summary>
    /// 全称 + 限定词（2026-09-15）。两种形态，语义上都仍然是**那家上市公司**：
    ///   · 合并口径 —— "宁德时代新能源科技股份有限公司<b>及其子公司</b>"
    ///   · 分支机构 —— "中国电信股份有限公司<b>宁夏分公司</b>"（分公司不是独立法人，就是本人）
    ///
    /// 所以置信度跟 <see cref="Exact"/> 一个量级，**高于** <see cref="Subsidiary"/>
    /// （那一档是"两个法人有控制关系"，是假设）。实测能多认出 726 个名字。
    ///
    /// 风险很小：判据是"以某上市公司全称开头"，而 6040 个全称里前缀冲突只有 2 对，
    /// 还是同一家公司的注销记录（上海百联 vs 上海百联(注销)）。
    /// </summary>
    public const string Qualified = "qualified";

    /// <summary>
    /// 对手是那家上市公司的**非上市母集团**（2026-09-15）。partner_code 仍然指向那个上市平台，
    /// 但语义完全不同，用之前必须看清这一档：
    ///   · 「哪些小公司深度绑定央企」—— <b>能用</b>，"贝肯能源 74% 营收来自中石油集团"是真的
    ///   · 「查 601857 中国石油的供应链」—— <b>不能用</b>，贝肯能源不是 601857 的供应商
    ///
    /// 2025 年报实测 146 条。不合并进 <see cref="Normalized"/>，也不删。
    /// </summary>
    public const string ParentGroup = "parent_group";

    /// <summary>
    /// 对手名是**某家上市公司的子公司**，归并到母公司代码（2026-09-11）。
    /// 名单来自年报「合并财务报表范围」那张表，见 CompanySubsidiary。
    ///
    /// ⚠ **置信度低于前几档**，因为它是个假设：子公司跟你做生意不等于母公司跟你做生意。
    ///   前几档是"这两个名字指同一个法人主体"，这一档是"这两个法人有控制关系"——
    ///   完全不同的断言。单独标一档就是为了用的时候能把它们分开。
    ///
    /// ⚠ **这一档不套 <see cref="IsParentGroup"/> 护栏**。它的权威来源是年报解析出来的名单本身，
    ///   名字里带"集团"很正常——「中国电建集团华东勘测设计研究院有限公司」是 601669 的真子公司，
    ///   套上护栏会误杀。2026-09-15 我就误判过一次，把这类 8 条算成了错边。
    /// </summary>
    public const string Subsidiary = "subsidiary";

    /// <summary>
    /// 匿名披露的对手名——**不参与匹配**。
    /// 万一真有公司叫"第一名"，也不能让"第一名"这种占位符去撞上它。
    /// </summary>
    private static readonly Regex Anonymous = new(
        // ⚠ 数字有两种写法，两种都要认："前5名客户" 和 "前五名客户" 都是占位符。
        //   只写 \d 的话中文那种会漏掉，然后跑去跟真公司名做匹配。
        @"^(第[\d一二三四五六七八九十百]+名|客户\s*\d+|供应商\s*\d+|其余(客户|供应商)|" +
        @"前[\d一二三四五六七八九十]+名.*|合计|" +
        @"客户[A-Za-z]|供应商[A-Za-z]|客户[一二三四五六七八九十]|供应商[一二三四五六七八九十]|" +
        @"单位\d+|[A-Z]公司)$",
        RegexOptions.Compiled);

    private static readonly Regex Bracketed = new(@"[（(][^）)]*[）)]", RegexOptions.Compiled);

    /// <summary>
    /// 归一化要去掉的公司后缀。
    ///
    /// ⚠ **绝不能把「集团」加回来**（2026-09-15 的教训，见类注释）。"XX集团有限公司" 是
    ///   非上市母公司的标准写法，"XX股份有限公司" 是上市主体的——去掉"集团"就等于抹掉了
    ///   区分两者的唯一线索，归一化档会有三分之二变成错边。
    /// </summary>
    private static readonly Regex Suffix = new(
        @"(股份)?有限(责任)?公司$|有限公司$|公司$", RegexOptions.Compiled);

    /// <summary>末尾的括号附注："…(合并)"、"…(含其子公司)"。判母集团前要先剥掉。</summary>
    private static readonly Regex TrailingNote = new(@"([（(][^）)]*[）)])+$", RegexOptions.Compiled);

    /// <summary>「集团」结尾（后面最多再跟"有限/责任/公司"）。</summary>
    private static readonly Regex GroupTail = new(@"集团(有限)?(责任)?(公司)?$", RegexOptions.Compiled);

    /// <summary>
    /// <see cref="Qualified"/> 档允许的限定词。**宁可漏不可错**：收不进来的只是保持未匹配，
    /// 放错了就是一条错边。实测 1034 个候选里收 726、拒 308，被拒的多是
    /// "、"串起来的多主体长串和脏数据（"1"、"*3"、"-供应商20"）。
    /// </summary>
    private static readonly Regex Qualifier = new(
        // ① 合并口径：及/与/和 + 其(全资|控股) + 子公司/关联方/控制企业/所属公司/同一控制下…
        @"^[及与和](其)?(全资|控股)?(子公司|下属公司|下属子公司|控股子公司|关联方|关联公司|" +
        @"关联企业|关联单位|控制企业|控制公司|控制的公司|所属公司|所属企业|附属公司|附属企业|" +
        @"同一控制[^、,，]{0,12}|其他关联方)$" +
        // ② 无"及"字的口径词
        @"|^(下属(子)?公司|所属公司|关联企业|控制的?(企业|公司)|同一控制[^、,，]{0,12})$" +
        // ③ 分支机构。⚠ 限定 ≤12 字并禁止顿号，挡住"A分公司、B公司、C公司"那种多主体长串
        @"|^[^、,，]{0,12}(分公司|分行|支行|总行|分部|分厂|事业部|办事处|信用卡中心|营业部)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 只给 <see cref="ParentGroup"/> 那一档用的后缀集——**这是 v1 那个闯祸的正则**，
    /// 原样留着是因为它恰好能回答"这个母集团是谁的母集团"：
    /// 「中国铝业集团有限公司」按它归一化成「中国铝业」，正好对上 601600 的「中国铝业股份有限公司」。
    ///
    /// ⚠ 它**只在 <see cref="IsParentGroup"/> 已经判真之后**才允许使用。
    ///   拿它做通用归一化就是 v1 的病根：那样「中国铝业集团有限公司」会被当成 601600 本人。
    /// </summary>
    private static readonly Regex GroupSuffix = new(
        @"(股份)?有限(责任)?公司$|集团有限公司$|有限公司$|集团$|公司$", RegexOptions.Compiled);

    /// <summary>去掉所有空白（含全角空格）。精确匹配比的就是这个。</summary>
    public static string Compact(string s) => s.Replace(" ", "").Replace("\t", "")
                                               .Replace("　", "").Replace("\r", "").Replace("\n", "");

    /// <summary>归一化：去空白 → 去括号内容 → 去公司后缀。**不吃「集团」**，理由见 <see cref="Suffix"/>。</summary>
    public static string Normalize(string s) => Suffix.Replace(Bracketed.Replace(Compact(s), ""), "");

    /// <summary>母集团专用归一化：连「集团」一起去掉，用来找它挂在哪家上市公司名下。</summary>
    public static string NormalizeGroup(string s)
    {
        var t = Bracketed.Replace(Compact(s), "");
        // 去两轮：「中国铝业集团有限公司」先掉"有限公司"、再掉"集团"
        for (int i = 0; i < 2; i++)
        {
            var next = GroupSuffix.Replace(t, "");
            if (next == t) break;
            t = next;
        }
        return t;
    }

    public static bool IsAnonymous(string name)
        => string.IsNullOrWhiteSpace(name) || Anonymous.IsMatch(name.Trim());

    /// <summary>
    /// 这个名字是不是**非上市母集团**的写法（2026-09-15）。
    ///
    /// 判据：剥掉末尾括号附注、去掉中间括号的符号（保留内容）之后，
    ///   · 含「股份」→ 不是。"XX集团股份有限公司" 本身就是上市主体（紫金矿业、京东方、上汽）。
    ///   · 以「集团」结尾 → 是。"中国铝业集团有限公司"、"宜宾五粮液集团"、"申能(集团)有限公司"。
    ///
    /// ⚠ 「集团」必须在**末尾**。"中国电建集团华东勘测设计研究院有限公司" 的集团在中间，
    ///   那是真子公司，不能判成母集团。
    ///
    /// ⚠ 这条**只给 <see cref="Normalized"/> 那一档当护栏**，别拿去筛 <see cref="Subsidiary"/>
    ///   （理由见 <see cref="Subsidiary"/> 的注释）；也别拿去筛 <see cref="Short"/>
    ///   （「美的集团」是简称，会被误杀）。
    ///
    /// 用 CompanyProfile.holder_name（控股股东）做过交叉验证：它命中的 39 个是这条判据命中的
    /// 103 个的**真子集**，一个都没多抓到——因为那一列只记直接控股股东，"东方电气集团"持有
    /// 600875 是穿透中间层的。所以护栏用这条，股东表只能当第二佐证。
    /// </summary>
    public static bool IsParentGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = TrailingNote.Replace(Compact(name), "");
        s = s.Replace("（", "").Replace("(", "").Replace("）", "").Replace(")", "");
        if (s.Contains("股份", StringComparison.Ordinal)) return false;
        return GroupTail.IsMatch(s);
    }

    /// <summary>
    /// 上市公司名字的四份索引。字典多到该有个名字了——别再往 <see cref="Match"/> 上加参数。
    /// </summary>
    /// <param name="ByFull">去空白的全称 → 代码。</param>
    /// <param name="ByNorm">归一化全称 → 代码（**不含「集团」处理**）。</param>
    /// <param name="ByShort">证券简称 → 代码。零重名，所以也是精确的。</param>
    /// <param name="ByGroup">
    /// 连「集团」一起去掉之后的名字 → 代码。**只给母集团那一档用**，
    /// 拿它做通用匹配就是 v1 的病根（见 <see cref="GroupSuffix"/>）。
    /// </param>
    /// <param name="SelfNamedGroup">
    /// **自己名字里就带「集团」的上市公司**（万华化学集团、上海医药集团、中信泰富特钢集团…）。
    ///
    /// 有它才分得清两件事：
    ///   ·「中国铝业集团有限公司」—— 601600 全称里没有"集团"，所以这是**另一个实体**，真母集团
    ///   ·「万华化学集团有限公司」—— 600309 全称就是"万华化学集团股份有限公司"，
    ///      对手只是漏写了"股份"，**那就是这家公司本人**
    ///
    /// 实测 185 个母集团候选里有 66 个属于后者（上海医药被提及 104 次，判错影响不小）。
    /// </param>
    public sealed record CompanyIndex(
        Dictionary<string, string> ByFull,
        Dictionary<string, string> ByNorm,
        Dictionary<string, string> ByShort,
        Dictionary<string, string> ByGroup,
        HashSet<string> SelfNamedGroup);

    /// <summary>
    /// 建索引。<paramref name="companies"/> 是 (代码, 全称, 简称)；简称为空就不进简称索引。
    ///
    /// ⚠ 同一个全称会命中多个代码——**A 股和 B 股是同一家公司**，全称一模一样。
    ///   实测："京东方科技集团股份有限公司"同时对应 000725(A) 和 200725(B)。
    ///   不处理的话结果取决于哪条后写入，而我们要的永远是 A 股那个。
    /// </summary>
    public static CompanyIndex BuildIndex(
        IEnumerable<(string Code, string FullName, string? Abbr)> companies)
    {
        var byFull = new Dictionary<string, string>(StringComparer.Ordinal);
        var byNorm = new Dictionary<string, string>(StringComparer.Ordinal);
        var byShort = new Dictionary<string, string>(StringComparer.Ordinal);
        var byGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        var selfGroup = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (code, full, abbr) in companies)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(full)) continue;
            Put(byFull, Compact(full), code);
            Put(byNorm, Normalize(full), code);
            Put(byGroup, NormalizeGroup(full), code);
            if (full.Contains("集团", StringComparison.Ordinal)) selfGroup.Add(code);

            // ⚠ 只收 ≥3 字的简称：2 字的只有"柳工"一个，挡不住误撞
            var s = Compact(abbr ?? "");
            if (s.Length >= 3) Put(byShort, s, code);
        }
        return new CompanyIndex(byFull, byNorm, byShort, byGroup, selfGroup);

        static void Put(Dictionary<string, string> map, string key, string code)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var existing)) { map[key] = code; return; }
            map[key] = Preferred(existing, code);
        }
    }

    /// <summary>只有 (代码, 全称) 的重载——没有简称就没有简称档，其余照常。</summary>
    public static CompanyIndex BuildIndex(IEnumerable<(string Code, string FullName)> companies)
        => BuildIndex(companies.Select(c => (c.Code, c.FullName, (string?)null)));

    /// <summary>
    /// 同一全称的两个代码，选哪个。B 股（<c>200</c>/<c>900</c> 开头）让位给 A 股。
    /// 都不是 B 股就保留先到的——结果必须跟输入顺序无关，否则不可复现。
    /// </summary>
    public static string Preferred(string a, string b)
    {
        bool ba = IsBShare(a), bb = IsBShare(b);
        if (ba && !bb) return b;
        if (bb && !ba) return a;
        return string.CompareOrdinal(a, b) <= 0 ? a : b;
    }

    private static bool IsBShare(string code)
        => code.StartsWith("200", StringComparison.Ordinal)
        || code.StartsWith("900", StringComparison.Ordinal);

    /// <summary>全称至少要这么长才允许做前缀匹配——太短的前缀容易撞上别的公司。</summary>
    private const int MinPrefixLength = 8;

    /// <summary>
    /// 匹配一个对手名。返回 (代码, 档次)；匹配不上返回 (null, null)——**不猜**。
    ///
    /// 顺序是判据强度排的，改动之前先想清楚为什么：
    ///   ① 全称精确        —— 零歧义
    ///   ② 简称精确        —— 零重名，**必须在护栏之前**（"美的集团"是简称不是母集团）
    ///   ③ 全称+限定词     —— "…及其子公司"、"…宁夏分公司"，仍是本人
    ///   ④ 母集团护栏      —— 拦住"中国铝业集团有限公司"这类，标 <see cref="ParentGroup"/> 而不是丢弃
    ///   ⑤ 归一化          —— 只剩写法差异这一种用途了
    ///   ⑥ 子公司名单      —— 置信度最低，所以垫底：名字本身是上市公司就该是它自己
    /// </summary>
    public static (string? Code, string? MatchType) Match(
        string partnerName,
        CompanyIndex index,
        Dictionary<string, string>? bySubsidiary = null)
    {
        if (IsAnonymous(partnerName)) return (null, null);

        var name = partnerName.Trim();
        var compact = Compact(name);

        // ① 全称精确
        if (index.ByFull.TryGetValue(compact, out var c1)) return (c1, Exact);

        // ② 简称精确（零重名）。在母集团那一档前面，否则「美的集团」「宁德时代」会被当母集团
        if (index.ByShort.TryGetValue(compact, out var c2)) return (c2, Short);

        // ③ 全称 + 限定词。按前缀长度逐段查字典，别去遍历六千个全称——
        //    实测遍历法处理 11 万个名字要两分钟，切片查字典是毫秒级。
        for (int k = MinPrefixLength; k < compact.Length; k++)
        {
            if (!index.ByFull.TryGetValue(compact[..k], out var c3)) continue;
            if (Qualifier.IsMatch(compact[k..])) return (c3, Qualified);
            break;      // 前缀冲突实测只有 2 对（同一家公司的注销记录），命中一次就够
        }

        // ④ 非上市母集团。**必须在归一化之前**，而且用的是专属索引：
        //    删掉「集团」后缀之后，「中国铝业集团有限公司」在 ByNorm 里已经查不到了
        //    （那正是修复生效的地方），所以要靠 ByGroup 才能答出"它是谁的母集团"。
        //    不返回 null——这些边不是垃圾，只是主体标错了，标一档让调用方自己决定用不用。
        //
        //    ⚠ 但要先排掉"公司自己名字就带集团"那种：600309 全称是「万华化学集团股份有限公司」，
        //      对手写「万华化学集团有限公司」只是漏了"股份"，那是本人不是母集团。
        //      实测 185 个候选里 66 个属于这种（上海医药被提及 104 次，判错影响不小）。
        if (IsParentGroup(name) && index.ByGroup.TryGetValue(NormalizeGroup(name), out var c4))
            return (c4, index.SelfNamedGroup.Contains(c4) ? Normalized : ParentGroup);

        // ⑤ 归一化：只剩"有限公司 vs 股份有限公司"这种写法差异了
        if (index.ByNorm.TryGetValue(Normalize(name), out var c5)) return (c5, Normalized);

        // ⑥ 子公司名单垫底：一个名字如果本身就是上市公司（前面几档命中），那它就是它自己，
        //    不该被归并到谁的名下。⚠ 这一档**不套母集团判据**，理由见 Subsidiary 的注释。
        if (bySubsidiary != null)
        {
            if (bySubsidiary.TryGetValue(compact, out var c6)) return (c6, Subsidiary);
            if (bySubsidiary.TryGetValue(Normalize(name), out var c7)) return (c7, Subsidiary);
        }
        return (null, null);
    }

    /// <summary>
    /// 给子公司名单建索引：全称和归一化两个 key 都指向母公司代码，装进同一个字典
    /// （两种 key 不会撞——归一化只会更短，撞了也是同一家）。
    /// </summary>
    public static Dictionary<string, string> BuildSubsidiaryIndex(
        IEnumerable<(string Name, string ParentCode)> subsidiaries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, parent) in subsidiaries)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent)) continue;
            var full = Compact(name);
            var norm = Normalize(name);
            // 同一个 key 落到不同母公司 = 歧义，**两边都删掉**。硬挑一个就是在造错边。
            if (full.Length > 0) Put(map, full, parent);
            if (norm.Length > 0 && norm != full) Put(map, norm, parent);
        }

        // 把冲突标记（空串）清掉——歧义的 key 一个都不留
        return map.Where(kv => kv.Value.Length > 0)
                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        static void Put(Dictionary<string, string> map, string key, string parent)
        {
            if (map.TryGetValue(key, out var existing) && existing != parent)
                map[key] = "";          // 冲突标记，下面统一清掉
            else map[key] = parent;
        }
    }
}
