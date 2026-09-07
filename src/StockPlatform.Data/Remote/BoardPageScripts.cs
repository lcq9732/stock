namespace StockPlatform.Data.Remote;

/// <summary>
/// 在东财行情中心的**板块页**上操作用的那几段 JS（2026-09-06 抽出来共用）。
///
/// 为什么要单独放一份：现在有两个浏览器实现都要跑这些脚本——
/// <c>ChromeCdpBoardPageScraper</c>（真 Chrome，走 CDP）和 <c>WebView2JsonFetcher</c>（Edge 内核，回退）。
/// 选择器写在各自的类里，早晚会改一处漏一处，而漏掉的后果是**静默的**：
/// 点不中表头就按涨跌幅翻页、跨页重复和遗漏，名单少几只谁也不会发现。
///
/// ════ 这几段脚本对应的页面结构（2026-09-06 实地查的）════
/// 分页控件：<code>DIV.qtpager &gt; A["&lt;"] , A["1"] , A.acitve["2"] , A["3"] , A["&gt;"] , FORM.gotoform</code>
///   · 「下一页」在页面上**显示为 <c>&gt;</c>**，DOM 里没有"下一页"三个字
///     （辅助功能树会把它读成"下一页"，照着那个写文本匹配是找不到的——踩过）；
///   · 当前页那个 a 的 class 是 <c>acitve</c>——东财自己拼错的（active 少个 t），两种都认。
/// 表头「代码」：排序挂在单元格上，不是规规矩矩的 <c>&lt;th&gt;</c>，所以按**文本精确等于"代码"**找。
/// </summary>
public static class BoardPageScripts
{
    /// <summary>板块页地址。<c>#boards2-90.BKxxxx</c> 就是人从行情中心点进某个板块时的那个地址。</summary>
    public static string BoardPageUrl(string boardCode) =>
        $"https://quote.eastmoney.com/center/gridlist.html#boards2-90.{boardCode}";

    /// <summary>
    /// **在同一个文档里切到另一个板块**——等价于人点左边菜单里的板块（那些菜单项的 href
    /// 就是 <c>#boards2-90.BKxxxx</c>）。
    ///
    /// ⚠ 这是这条通道最要紧的一个细节（2026-09-06 用户指出）：板块页是**单页应用**，
    /// 靠 <c>hashchange</c> 驱动。原来每个板块都用 <c>Page.navigate</c> 换地址，等于整个文档
    /// 重新加载，而首次加载时地址里**已经带着 hash**、<c>hashchange</c> 根本不会触发——
    /// 页面于是压根不去要数据，表格永远空着。日志里的表现就是
    /// "期间页面一个成分股请求都没发出去（不是被拒，是压根没发）"。
    ///
    /// 只改 hash 就不一样：文档不重载，页面收到 hashchange，自己去取这个板块的数据，
    /// 跟人点菜单一模一样。
    ///
    /// 目标 hash 跟当前相同时（同一个板块重试）不会触发事件，所以先把 hash 换到别处再换回来。
    /// </summary>
    public static string SwitchToBoard(string boardCode) => $$"""
        (function () {
          var target = '#boards2-90.{{boardCode}}';

          // 能点就点：页面上要是有指向这个板块的链接（左边菜单里的板块项），
          // 点它才是真正的"点菜单"，路由怎么走完全由页面自己决定。
          var links = document.querySelectorAll('a[href$="' + target + '"]');
          for (var i = 0; i < links.length; i++) {
            if (links[i].offsetParent !== null) { links[i].click(); return 'clicked'; }
          }

          // 找不到链接就自己换 hash。对页面来说这跟点链接是一回事——
          // 路由监听的是 hashchange，不关心 hash 是被点出来的还是被设出来的。
          // hash 跟当前相同时（同一个板块重试）不会触发事件，所以先换到别处再换回来。
          if (location.hash === target) location.hash = '#hs_a_board';
          location.hash = target;
          return 'ok';
        })()
        """;

    /// <summary>
    /// 切到另一个列表（沪深京 A 股）——表格渲染不出来时"去别处转一圈"用的那一下。
    /// 同样是能点链接就点，点不到才换 hash。
    /// </summary>
    public const string SwitchToOtherList = """
        (function () {
          var target = '#hs_a_board';
          var links = document.querySelectorAll('a[href$="' + target + '"]');
          for (var i = 0; i < links.length; i++) {
            if (links[i].offsetParent !== null) { links[i].click(); return 'clicked'; }
          }
          location.hash = target;
          return 'ok';
        })()
        """;

    /// <summary>
    /// 表头「代码」在不在（＝成分股表格渲染好了没）。
    ///
    /// 用它当"表格好了没"的判据是因为：它既是标志、又正好是下一步要点的那个东西——
    /// 它在＝点必中，它不在＝点多少次都是白点。所以查找条件必须跟
    /// <see cref="ClickCodeHeader"/> **一模一样**，否则会出现"探测说在、点的时候找不到"。
    /// </summary>
    public const string HasCodeHeader = """
        (function () {
          var cells = Array.prototype.slice.call(document.querySelectorAll('th, td, div, span'));
          for (var i = 0; i < cells.length; i++) {
            var el = cells[i];
            if ((el.innerText || el.textContent || '').trim() !== '代码') continue;
            if (el.offsetParent === null) continue;
            return 'yes';
          }
          return 'no';
        })()
        """;

    /// <summary>
    /// 点表头「代码」，让页面自己改用 <c>fid=f12</c> 重新请求。
    ///
    /// 为什么非点不可：页面默认按 <c>fid=f3</c>（涨跌幅）排序，那**不是唯一键**——
    /// 一个板块里涨跌幅相同的股票很多（停牌一片 0.00%、涨停一片 10.00%），
    /// 并列项之间服务端不保证每次返回的次序一样，翻页就会跨页重复和遗漏。
    /// 代码唯一，排序才稳。
    /// </summary>
    public const string ClickCodeHeader = """
        (function () {
          var cells = Array.prototype.slice.call(document.querySelectorAll('th, td, div, span'));
          for (var i = 0; i < cells.length; i++) {
            var el = cells[i];
            if ((el.innerText || el.textContent || '').trim() !== '代码') continue;
            if (el.offsetParent === null) continue;
            (el.querySelector('a') || el).click();
            return 'ok';
          }
          return 'not-found';
        })()
        """;

    /// <summary>
    /// 点「下一页」（页面上那个 <c>&gt;</c>）。找不到可点的＝已经是最后一页，返回 not-found。
    /// 退路：万一哪天 <c>&gt;</c> 变了，就点"当前页码 +1"那个数字。
    /// </summary>
    public const string ClickNextPage = """
        (function () {
          var pager = document.querySelector('.qtpager');
          if (!pager) return 'not-found';
          var links = pager.querySelectorAll('a');
          var cur = pager.querySelector('a.acitve, a.active');
          var curNum = cur ? parseInt((cur.textContent || '').trim(), 10) : NaN;

          for (var i = 0; i < links.length; i++) {
            var t = (links[i].textContent || '').trim();
            if (t !== '>' && t !== '下一页') continue;
            if (/disabled|end|nolink|unable/i.test(links[i].className || '')) return 'not-found';
            links[i].click();
            return 'ok';
          }

          if (!isNaN(curNum)) {
            for (var j = 0; j < links.length; j++) {
              if ((links[j].textContent || '').trim() === String(curNum + 1)) {
                links[j].click();
                return 'ok';
              }
            }
          }
          return 'not-found';
        })()
        """;

    /// <summary>
    /// 用分页控件里的「转到」表单跳到第 <paramref name="page"/> 页。
    ///
    /// 为什么需要它：网页端**连续翻页最多到第 30 页**（600 只），再往后要登录。
    /// 但把页面重开一次、重新点代码排序、然后直接跳到第 31 页，就能接着往下取——
    /// 那 30 页是"一次会话里连续翻"的限制，不是这个板块只能看 600 只。
    /// 所以大板块按 30 页一段来取：每满 30 页重开一次、跳到下一段起点。
    ///
    /// 优先点表单里的 GO 按钮（东财这个分页是 AJAX 的，点 GO 只发一个接口请求、不整页刷新）；
    /// 实在找不到按钮才退回 form.submit()。
    /// </summary>
    public static string GotoPage(int page) => $$"""
        (function () {
          var pager = document.querySelector('.qtpager');
          if (!pager) return 'not-found';
          var form = pager.querySelector('form.gotoform') || pager.querySelector('form') || document.querySelector('.gotoform');
          if (!form) return 'not-found';
          var input = form.querySelector('input[type=text]')
                   || form.querySelector('input:not([type=submit]):not([type=button])');
          if (!input) return 'not-found';

          input.value = '{{page}}';
          input.dispatchEvent(new Event('input', { bubbles: true }));
          input.dispatchEvent(new Event('change', { bubbles: true }));

          var btn = form.querySelector('input[type=submit]') || form.querySelector('button') || form.querySelector('a');
          if (btn) { btn.click(); return 'ok'; }
          if (typeof form.submit === 'function') { form.submit(); return 'ok'; }
          return 'not-found';
        })()
        """;

    /// <summary>
    /// 出问题时把页面当时的样子抓一小段，给日志用。
    ///
    /// 光报"找不到表头"没法定位——表格还没渲染、弹了验证、弹了登录框、东财真改了结构，
    /// 这四种表现一模一样但处置完全不同。带上标题、有没有表格/分页、正文头一段就能分辨。
    /// </summary>
    public const string DescribePage = """
        (function () {
          var t = (document.title || '').trim();
          var b = document.body ? (document.body.innerText || '').replace(/\s+/g, ' ').trim() : '';
          return '标题「' + t.slice(0, 40) + '」 表格=' + !!document.querySelector('tbody tr')
               + ' 分页=' + !!document.querySelector('.qtpager')
               + ' 正文：' + b.slice(0, 220);
        })()
        """;

    /// <summary>
    /// 页面上是不是摆着图片验证。
    ///
    /// ⚠ 必须扫**全文**：东财的验证是**浮层**，页面本身照常渲染，前几百字全是顶部导航
    /// （网站首页/加收藏/移动客户端/…/大盘星图/自选股/特色行情…），
    /// 浮层上那句"拖动下方滑块完成拼图"排在很后面（2026-09-06 踩过：只扫前 600 字，
    /// 结果每次都判成"不是验证"，然后报一个误导人的"找不到表头"）。
    ///
    /// 配套地，特征词必须**足够具体**——不能用"验证"这种单字，页脚有"验证码登录"、
    /// 帮助链接里也有"安全验证"，扫全文就会把正常页面误判成验证页、然后永远等人。
    /// 下面这些是实地截图确认过的原话。
    /// </summary>
    public const string HasChallenge = """
        (function () {
          var b = document.body ? (document.body.innerText || '') : '';
          var marks = ['完成拼图', '拖动下方滑块', '拖动左边滑块', '拖动滑块',
                       'captcha', 'geetest', 'nc_wrapper', 'verifycode',
                       '人机验证', '点击按钮开始验证'];
          for (var i = 0; i < marks.length; i++) {
            if (b.indexOf(marks[i]) >= 0) return 'yes:' + marks[i];
          }
          return 'no';
        })()
        """;
}
