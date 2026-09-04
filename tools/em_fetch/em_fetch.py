# -*- coding: utf-8 -*-
"""
东财离线取数脚本 —— 在【能访问东方财富】的机器上运行，把数据落到移动硬盘上的 sqlite 包里，
再把包拷回主环境用 em_merge.py 合并进主库。

为什么要这么绕：主环境访问不了东财，而东财在板块/概念、业绩预告、龙虎榜营业部明细、
资金流分级这几项上是现有数据源（新浪/腾讯/交易所/巨潮）拿不到或质量明显不足的。
详见 doc/eastmoney-offline-fetch.md。

设计要点
--------
1. **只用标准库**（urllib / sqlite3 / json），取数机器上装个 Python 3.8+ 就能跑，不用 pip。
2. **包里存原始字段**：表结构 = 东财返回的字段原样（列名保持大写）。字段映射留给合并阶段做，
   这样映射写错了改 em_merge.py 重跑即可，**不用重新取一夜数据**。
3. **按月切片**：龙虎榜营业部明细全量 131 万行，按 pageSize=500 是 2620 页；东财深分页
   （pageNumber 上千）会拒绝或极慢。所以大表按月切成小片，每片 40 页左右，翻页永远是浅的。
4. **断点续传到"片"级**：每取完一片就落库并记进度表，中途被限流/断网，重跑自动从下一片继续。
   通宵跑最怕的就是挂了要从头来。
5. **全量日志**：每个请求一条 JSONL，跑完用 em_analyze_log.py 反推安全间隔——间隔参数不拍脑袋。

用法
----
    python em_fetch.py --out E:\\em                     # 取全部（首次全量，约 5 小时）
    python em_fetch.py --out E:\\em --packs 1           # 只取 pack1（板块，10 分钟）
    python em_fetch.py --out E:\\em --packs 1,2,3,4     # datacenter 那批
    python em_fetch.py --out E:\\em --packs 5 --flow-scope watch   # 资金流只取关注股票
    python em_fetch.py --out E:\\em --resume            # 断点续传（默认就会续，这个只是显式声明）
"""

import argparse
import json
import os
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, date

# Windows 控制台默认 GBK，中文输出会炸；统一成 UTF-8
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

DC_URL = "https://datacenter-web.eastmoney.com/api/data/v1/get"
FLOW_URL = "https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get"

PAGE_SIZE = 500          # 实测上限，传 1000 也只返回 500
DEFAULT_START = "2016-01-01"

# ── 各域名的默认间隔（秒）──────────────────────────────────────────────
# 实测耐受度：datacenter ≫ push2his > push2。这里给的是保守起点，
# 第一晚跑完看日志再调。push2 已经不用了（板块走 datacenter 的 F10 报表）。
DEFAULT_GAP = {
    "datacenter-web.eastmoney.com": 2.0,
    "push2his.eastmoney.com": 5.0,
}


# ══════════════════════════════════════════════════════════════════════
# 任务定义
# ══════════════════════════════════════════════════════════════════════
# slice: "month" = 按月切片查询（大表必须，避免深分页）
#        "none"  = 一次性翻页取完（小表）
# date_field: 增量和切片依据的日期列；None 表示这张表没有时间维度，只能全量重取
class Task:
    def __init__(self, table, report, date_field, slice_mode="none",
                 sort_field=None, note=""):
        self.table = table
        self.report = report
        self.date_field = date_field
        self.slice_mode = slice_mode
        # 分页要稳定必须排序，否则翻页会漏/重
        self.sort_field = sort_field or date_field
        self.note = note


PACKS = {
    1: ("pack1_boards", [
        # 个股→板块归属映射（约 9.4 万行）。板块成分股的反向索引，
        # 一次拿全市场，完全绕开限流最凶的 push2。
        Task("EmStockBoard", "RPT_F10_CORETHEME_BOARDTYPE", None, "none",
             sort_field="SECURITY_CODE", note="个股板块归属·快照全量"),
    ]),
    2: ("pack2_earnings", [
        Task("EmEarningsForecast", "RPT_PUBLIC_OP_NEWPREDICT", "NOTICE_DATE", "year",
             note="业绩预告"),
        Task("EmEarningsExpress", "RPT_FCI_PERFORMANCEE", "UPDATE_DATE", "year",
             note="业绩快报"),
    ]),
    3: ("pack3_lhb", [
        Task("EmLhbSeatBuy", "RPT_BILLBOARD_DAILYDETAILSBUY", "TRADE_DATE", "month",
             note="龙虎榜买方营业部·约131万行"),
        Task("EmLhbSeatSell", "RPT_BILLBOARD_DAILYDETAILSSELL", "TRADE_DATE", "month",
             note="龙虎榜卖方营业部·约133万行"),
        Task("EmLhbDetail", "RPT_DAILYBILLBOARD_DETAILSNEW", "TRADE_DATE", "month",
             note="龙虎榜每日明细·约27万行"),
    ]),
    4: ("pack4_misc", [
        Task("EmBlockTrade", "RPT_DATA_BLOCKTRADE", "TRADE_DATE", "month",
             note="大宗交易·约68万行"),
        Task("EmOrgSurvey", "RPT_ORG_SURVEYNEW", "NOTICE_DATE", "month",
             note="机构调研·约28万行"),
        Task("EmHolderChange", "RPT_SHARE_HOLDER_INCREASE", "NOTICE_DATE", "year",
             note="股东增减持·约15万行"),
        # 解禁表含【未来】的解禁计划（样例里有 2035 年的），所以不能按"取到今天为止"做增量，
        # 每次都要全量重取。好在只有 3 万行、63 页。
        Task("EmShareLift", "RPT_LIFT_STAGE", None, "none",
             sort_field="FREE_DATE", note="限售解禁·含未来计划·全量重取"),
    ]),
    # pack5 是资金流，走 push2his 按股票取，逻辑不同，单独处理
}

FLOW_PACK = "pack5_moneyflow"

# 资金流 klines 的字段顺序（fields2 指定的 f51..f65）
FLOW_COLUMNS = [
    "TRADE_DATE",            # f51
    "MAIN_NET",              # f52 主力净额
    "SMALL_NET",             # f53 小单净额
    "MID_NET",               # f54 中单净额
    "BIG_NET",               # f55 大单净额
    "SUPER_NET",             # f56 超大单净额
    "MAIN_RATIO",            # f57 主力净占比
    "SMALL_RATIO",           # f58
    "MID_RATIO",             # f59
    "BIG_RATIO",             # f60
    "SUPER_RATIO",           # f61
    "CLOSE_PRICE",           # f62
    "CHANGE_RATE",           # f63
    "EXTRA1",                # f64
    "EXTRA2",                # f65
]


# ══════════════════════════════════════════════════════════════════════
# 日志
# ══════════════════════════════════════════════════════════════════════
class Logger:
    """每个请求一条 JSONL。跑完用 em_analyze_log.py 反推安全间隔。

    刻意把 gap_before / elapsed_ms / 域名累计次数都记下来——限流发生时，
    这几个数放在一起才能判断是"某域名自己的额度用完"还是"IP 总额度"。
    """

    def __init__(self, path):
        self.path = path
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.fh = open(path, "a", encoding="utf-8")
        self.host_count = {}
        self.total_count = 0
        self.start = time.time()

    def req(self, host, **kw):
        self.host_count[host] = self.host_count.get(host, 0) + 1
        self.total_count += 1
        rec = {
            "ts": datetime.now().isoformat(timespec="milliseconds"),
            "elapsed_total_s": round(time.time() - self.start, 1),
            "host": host,
            "host_seq": self.host_count[host],     # 该域名累计第几次请求
            "total_seq": self.total_count,         # 全局累计第几次
        }
        rec.update(kw)
        self.fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
        self.fh.flush()   # 通宵跑，随时可能被掐，不缓冲
        return rec

    def event(self, kind, **kw):
        rec = {"ts": datetime.now().isoformat(timespec="milliseconds"),
               "event": kind}
        rec.update(kw)
        self.fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
        self.fh.flush()

    def close(self):
        try:
            self.fh.close()
        except Exception:
            pass


# ══════════════════════════════════════════════════════════════════════
# HTTP（带限流退避）
# ══════════════════════════════════════════════════════════════════════
class Fetcher:
    def __init__(self, log, gap=None, max_retry=6):
        self.log = log
        self.gap = dict(DEFAULT_GAP)
        if gap:
            for h in self.gap:
                self.gap[h] = gap
        self.max_retry = max_retry
        self.last_call = {}       # host -> 上次请求的时刻
        self.backoff = {}         # host -> 当前额外退避秒数

    def _wait(self, host):
        """同域名两次请求之间至少隔 gap + 当前退避量。"""
        need = self.gap.get(host, 2.0) + self.backoff.get(host, 0.0)
        last = self.last_call.get(host)
        if last is not None:
            delta = time.time() - last
            if delta < need:
                time.sleep(need - delta)
        self.last_call[host] = time.time()
        return need

    def get(self, url, referer):
        host = urllib.parse.urlparse(url).hostname
        for attempt in range(self.max_retry):
            gap_before = self._wait(host)
            req = urllib.request.Request(
                url, headers={"User-Agent": UA, "Referer": referer,
                              "Accept": "*/*"})
            t0 = time.time()
            try:
                with urllib.request.urlopen(req, timeout=30) as r:
                    body = r.read().decode("utf-8", "replace")
                    status = r.status
                ms = int((time.time() - t0) * 1000)
                self.log.req(host, url=url[:200], http=status, elapsed_ms=ms,
                             bytes=len(body), gap_before=round(gap_before, 2),
                             retry=attempt)
                # 请求成功：退避量逐步回落（一次减一半，不要立刻清零）
                if self.backoff.get(host):
                    self.backoff[host] = round(self.backoff[host] / 2, 1)
                    if self.backoff[host] < 0.5:
                        self.backoff[host] = 0.0
                return body
            except Exception as e:
                ms = int((time.time() - t0) * 1000)
                self.log.req(host, url=url[:200], http=0, elapsed_ms=ms,
                             bytes=0, gap_before=round(gap_before, 2),
                             retry=attempt, error=str(e)[:160])
                # 指数退避：2→4→8→16→32→64，上限 120
                cur = self.backoff.get(host, 0.0)
                self.backoff[host] = min(120.0, (cur * 2) if cur else 2.0)
                self.log.event("backoff", host=host,
                               backoff_s=self.backoff[host], attempt=attempt)
                print("    ! %s 请求失败(%d/%d) 退避 %.0fs — %s"
                      % (host, attempt + 1, self.max_retry,
                         self.backoff[host], str(e)[:80]))
                if attempt == self.max_retry - 1:
                    return None
        return None

    def get_json(self, url, referer):
        body = self.get(url, referer)
        if body is None:
            return None
        try:
            return json.loads(body)
        except Exception as e:
            self.log.event("parse_error", url=url[:200], err=str(e)[:120],
                           head=body[:200])
            return None


# ══════════════════════════════════════════════════════════════════════
# sqlite 包
# ══════════════════════════════════════════════════════════════════════
class Pack:
    """一个 pack = 一个 sqlite 文件。表结构按东财返回的字段动态建，全部 TEXT，
    原样存——类型转换留给合并阶段，取数阶段不做任何加工，减少出错面。"""

    def __init__(self, path):
        self.path = path
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.con = sqlite3.connect(path)
        self.con.execute("PRAGMA journal_mode=WAL")
        self.con.execute("PRAGMA synchronous=NORMAL")
        self.con.execute("""
            CREATE TABLE IF NOT EXISTS _progress(
                task TEXT, slice TEXT, pages_done INTEGER, total_pages INTEGER,
                rows INTEGER, done INTEGER, updated_at TEXT,
                PRIMARY KEY(task, slice))""")
        self.con.execute("""
            CREATE TABLE IF NOT EXISTS _meta(
                k TEXT PRIMARY KEY, v TEXT)""")
        self.con.commit()
        self._cols = {}

    def set_meta(self, k, v):
        self.con.execute("INSERT OR REPLACE INTO _meta VALUES(?,?)", (k, str(v)))
        self.con.commit()

    def ensure_table(self, table, sample_row):
        """按第一行的 key 建表。东财偶尔会在不同页返回多出来的字段，
        insert 时按表已有列取交集，多出来的丢掉（合并阶段用不到的字段本来也不要）。"""
        if table in self._cols:
            return self._cols[table]
        cur = self.con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (table,))
        if not cur.fetchone():
            cols = list(sample_row.keys())
            ddl = ", ".join('"%s" TEXT' % c for c in cols)
            self.con.execute('CREATE TABLE "%s" (%s, _fetched_at TEXT)' % (table, ddl))
            self.con.commit()
        cur = self.con.execute('PRAGMA table_info("%s")' % table)
        cols = [r[1] for r in cur.fetchall() if r[1] != "_fetched_at"]
        self._cols[table] = cols
        return cols

    def insert(self, table, rows):
        if not rows:
            return 0
        cols = self.ensure_table(table, rows[0])
        now = datetime.now().isoformat(timespec="seconds")
        ph = ",".join("?" * (len(cols) + 1))
        sql = 'INSERT INTO "%s" (%s,_fetched_at) VALUES(%s)' % (
            table, ",".join('"%s"' % c for c in cols), ph)
        data = []
        for r in rows:
            data.append([_s(r.get(c)) for c in cols] + [now])
        self.con.executemany(sql, data)
        self.con.commit()
        return len(data)

    def get_progress(self, task, slc):
        cur = self.con.execute(
            "SELECT pages_done,total_pages,rows,done FROM _progress WHERE task=? AND slice=?",
            (task, slc))
        return cur.fetchone()

    def set_progress(self, task, slc, pages_done, total_pages, rows, done):
        self.con.execute(
            "INSERT OR REPLACE INTO _progress VALUES(?,?,?,?,?,?,?)",
            (task, slc, pages_done, total_pages, rows, 1 if done else 0,
             datetime.now().isoformat(timespec="seconds")))
        self.con.commit()

    def clear_slice(self, table, task, slc, date_field, lo, hi):
        """重跑某一片之前先删掉这片已有的行，避免断点续传时产生重复。
        没有 date_field 的表（快照类）直接清空整表。"""
        cur = self.con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (table,))
        if not cur.fetchone():
            return
        if date_field and lo and hi:
            self.con.execute(
                'DELETE FROM "%s" WHERE substr("%s",1,10)>=? AND substr("%s",1,10)<=?'
                % (table, date_field, date_field), (lo, hi))
        else:
            self.con.execute('DELETE FROM "%s"' % table)
        self.con.commit()

    def summary(self):
        out = []
        cur = self.con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE '\\_%' ESCAPE '\\'")
        for (t,) in cur.fetchall():
            n = self.con.execute('SELECT COUNT(*) FROM "%s"' % t).fetchone()[0]
            out.append((t, n))
        return out

    def close(self):
        try:
            self.con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            self.con.close()
        except Exception:
            pass


def _s(v):
    """统一转成字符串存。None 保持 None，dict/list 转 JSON。"""
    if v is None:
        return None
    if isinstance(v, (dict, list)):
        return json.dumps(v, ensure_ascii=False)
    return str(v)


# ══════════════════════════════════════════════════════════════════════
# 切片
# ══════════════════════════════════════════════════════════════════════
def month_slices(start, end):
    """[(片名, 起, 止), ...] 按自然月切。"""
    y, m = int(start[:4]), int(start[5:7])
    ey, em = int(end[:4]), int(end[5:7])
    out = []
    while (y, m) <= (ey, em):
        if m == 12:
            ny, nm = y + 1, 1
        else:
            ny, nm = y, m + 1
        lo = "%04d-%02d-01" % (y, m)
        # 下个月1号往前一天
        hi_d = date(ny, nm, 1).toordinal() - 1
        hi = date.fromordinal(hi_d).isoformat()
        out.append(("%04d-%02d" % (y, m), max(lo, start), min(hi, end)))
        y, m = ny, nm
    return out


def year_slices(start, end):
    out = []
    for y in range(int(start[:4]), int(end[:4]) + 1):
        lo, hi = "%d-01-01" % y, "%d-12-31" % y
        out.append((str(y), max(lo, start), min(hi, end)))
    return out


# ══════════════════════════════════════════════════════════════════════
# datacenter 取数
# ══════════════════════════════════════════════════════════════════════
def dc_url(report, page, sort_field, filt=None):
    # 参数组合刻意跟探测脚本保持一致（不带 source/client）——那是实测跑通过的组合，
    # 多加参数意味着多一个没验证过的变量，通宵跑不值得冒这个险。
    qs = {
        "reportName": report,
        "columns": "ALL",
        "pageNumber": str(page),
        "pageSize": str(PAGE_SIZE),
    }
    if sort_field:
        qs["sortColumns"] = sort_field
        qs["sortTypes"] = "-1"
    if filt:
        qs["filter"] = filt
    # 括号/引号/比较符是 filter 语法的一部分，不能被转义
    return DC_URL + "?" + urllib.parse.urlencode(qs, safe="()'%<>=,")


def run_task(fetcher, pack, task, start, end, log):
    """取一个 report。大表按片跑，每片跑完落库+记进度，可断点续传。"""
    if task.slice_mode == "month":
        slices = month_slices(start, end)
    elif task.slice_mode == "year":
        slices = year_slices(start, end)
    else:
        slices = [("ALL", None, None)]

    print("\n  ── %s  (%s)  %d 片" % (task.table, task.note, len(slices)))
    total_rows = 0
    for si, (name, lo, hi) in enumerate(slices, 1):
        prog = pack.get_progress(task.table, name)
        if prog and prog[3]:            # done
            total_rows += prog[2] or 0
            continue

        # 这一片之前可能取了一半，先清掉重来（比记录页级偏移简单且更可靠）
        pack.clear_slice(task.table, task.table, name, task.date_field, lo, hi)

        filt = None
        if task.date_field and lo and hi:
            filt = "(%s>='%s')(%s<='%s')" % (task.date_field, lo, task.date_field, hi)

        page = 1
        pages = None
        rows_in_slice = 0
        while True:
            url = dc_url(task.report, page, task.sort_field, filt)
            j = fetcher.get_json(url, "https://data.eastmoney.com/")
            if j is None:
                print("    × %s 第%d页 取不到，跳过该片（重跑时会自动重来）" % (name, page))
                log.event("slice_failed", task=task.table, slice=name, page=page)
                break
            res = j.get("result")
            if not res or not res.get("data"):
                # 空片是正常的（某月没数据），标记完成
                break
            data = res["data"]
            pages = res.get("pages") or 1
            n = pack.insert(task.table, data)
            rows_in_slice += n
            pack.set_progress(task.table, name, page, pages, rows_in_slice, False)
            if page >= pages or len(data) < PAGE_SIZE:
                break
            page += 1

        pack.set_progress(task.table, name, page, pages or 1, rows_in_slice, True)
        total_rows += rows_in_slice
        pct = si * 100.0 / len(slices)
        print("    [%5.1f%%] %-8s %6d 行 (%s页)  累计 %d"
              % (pct, name, rows_in_slice, pages, total_rows))
    print("  ── %s 完成，共 %d 行" % (task.table, total_rows))
    return total_rows


# ══════════════════════════════════════════════════════════════════════
# 资金流（push2his，按股票）
# ══════════════════════════════════════════════════════════════════════
def secid(code):
    """东财的 secid 前缀：沪市 1、深市/北交 0。

    坑在 9 开头要分两种：900xxx 是沪 B 股（1.），而 920xxx 是北交所 2023 年起启用的
    新代码段（0.）。按"9 开头=沪市"一刀切会让所有北交所股票取不到资金流。
    4/8 开头是北交所老代码段，同样归 0。"""
    if code.startswith("920") or code[0] in "48":
        return "0." + code
    if code[0] == "6" or code.startswith("900"):
        return "1." + code
    return "0." + code


def run_moneyflow(fetcher, pack, codes, log):
    print("\n  ── EmNetInflowDetail  资金流分级 (%d 只，接口只给最近约120个交易日)" % len(codes))
    done = set()
    cur = pack.con.execute(
        "SELECT slice FROM _progress WHERE task='EmNetInflowDetail' AND done=1")
    done = set(r[0] for r in cur.fetchall())
    if done:
        print("    断点续传：已完成 %d 只，跳过" % len(done))

    total = 0
    t0 = time.time()
    for i, code in enumerate(codes, 1):
        if code in done:
            continue
        url = (FLOW_URL + "?lmt=0&klt=101&secid=" + secid(code) +
               "&fields1=f1,f2,f3,f7"
               "&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61,f62,f63,f64,f65")
        j = fetcher.get_json(url, "https://quote.eastmoney.com/")
        if j is None:
            log.event("flow_failed", code=code)
            continue
        d = j.get("data")
        if not d or not d.get("klines"):
            pack.set_progress("EmNetInflowDetail", code, 1, 1, 0, True)
            continue
        rows = []
        for line in d["klines"]:
            parts = line.split(",")
            r = {"SECURITY_CODE": code}
            for ci, cname in enumerate(FLOW_COLUMNS):
                r[cname] = parts[ci] if ci < len(parts) else None
            rows.append(r)
        n = pack.insert("EmNetInflowDetail", rows)
        pack.set_progress("EmNetInflowDetail", code, 1, 1, n, True)
        total += n
        if i % 50 == 0 or i == len(codes):
            el = time.time() - t0
            speed = i / el if el > 0 else 0
            eta = (len(codes) - i) / speed / 60 if speed > 0 else 0
            print("    [%5.1f%%] %d/%d 只  %d 行  已用 %.0f 分  预计还需 %.0f 分"
                  % (i * 100.0 / len(codes), i, len(codes), total, el / 60, eta))
    print("  ── 资金流完成，共 %d 行" % total)
    return total


# watch 模式关注的板块关键词。资金流接口只能按股票逐只取（5553 只要 7.7 小时），
# 而它只有 120 天历史、价值相对最低，所以默认只覆盖当前在看的这些产业链。
WATCH_KEYWORDS = [
    "存储", "芯片", "半导体", "算力", "液冷", "数据中心", "封装", "CPO",
    "光模块", "服务器", "光刻", "铜缆", "人工智能", "机器人", "固态电池",
    "创新药", "核电", "军工", "稀土", "PCB",
]


def load_codes(scope, out_dir, pack1_path):
    """资金流要取哪些股票。

    优先级：codes.txt（手工名单，最高） > pack1 的板块归属表 > 空
      all   —— 板块归属表里出现过的全部股票（≈全市场）
      watch —— 只要 BOARD_NAME 命中 WATCH_KEYWORDS 的股票，省下大半时间
    """
    # 手工名单优先——想精确控制范围时最直接
    txt = os.path.join(out_dir, "codes.txt")
    if os.path.exists(txt):
        with open(txt, encoding="utf-8") as f:
            codes = [l.strip() for l in f if l.strip() and not l.startswith("#")]
        if codes:
            print("  股票名单来自 codes.txt：%d 只" % len(set(codes)))
            return sorted(set(codes))

    if not os.path.exists(pack1_path):
        return []

    con = sqlite3.connect(pack1_path)
    try:
        if scope == "watch":
            where = " OR ".join(["BOARD_NAME LIKE ?"] * len(WATCH_KEYWORDS))
            cur = con.execute(
                "SELECT DISTINCT SECURITY_CODE FROM EmStockBoard WHERE " + where,
                ["%" + k + "%" for k in WATCH_KEYWORDS])
        else:
            cur = con.execute("SELECT DISTINCT SECURITY_CODE FROM EmStockBoard")
        codes = [r[0] for r in cur.fetchall() if r[0] and len(r[0]) == 6]
    except Exception as e:
        print("  !! 读 pack1 失败：%s" % e)
        codes = []
    finally:
        con.close()

    if scope == "watch":
        print("  watch 模式：命中 %d 个关键词板块的股票共 %d 只"
              % (len(WATCH_KEYWORDS), len(set(codes))))
    return sorted(set(codes))


# ══════════════════════════════════════════════════════════════════════
# 状态文件（移动硬盘上，取数端和合并端靠它对接）
# ══════════════════════════════════════════════════════════════════════
def load_state(out_dir):
    p = os.path.join(out_dir, "em_state.json")
    if os.path.exists(p):
        try:
            with open(p, encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {}


def save_state(out_dir, st):
    p = os.path.join(out_dir, "em_state.json")
    with open(p, "w", encoding="utf-8") as f:
        json.dump(st, f, ensure_ascii=False, indent=1)


# ══════════════════════════════════════════════════════════════════════
def main():
    ap = argparse.ArgumentParser(description="东财离线取数")
    ap.add_argument("--out", required=True, help="输出目录（移动硬盘），如 E:\\em")
    ap.add_argument("--packs", default="1,2,3,4,5",
                    help="要取的包，逗号分隔。1板块 2业绩 3龙虎榜 4大宗/调研/解禁/增减持 5资金流")
    ap.add_argument("--start", default=None,
                    help="起始日期，默认取 em_state.json 里的已合并水位线，都没有则 %s" % DEFAULT_START)
    ap.add_argument("--end", default=None, help="截止日期，默认今天")
    ap.add_argument("--gap", type=float, default=None,
                    help="所有域名统一用这个间隔（秒）。不填用各域名默认值")
    ap.add_argument("--flow-scope", default="watch", choices=["watch", "all"],
                    help="资金流范围：watch=板块成分股(约1小时) all=全部(约7.7小时)")
    ap.add_argument("--resume", action="store_true", help="显式声明续传（默认就会续）")
    args = ap.parse_args()

    out_dir = args.out
    os.makedirs(out_dir, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d")
    state = load_state(out_dir)

    end = args.end or date.today().isoformat()
    log = Logger(os.path.join(out_dir, "logs", "fetch-%s.jsonl" % stamp))
    fetcher = Fetcher(log, gap=args.gap)

    want = [int(x) for x in args.packs.split(",") if x.strip()]
    print("=" * 68)
    print("东财离线取数   输出=%s   包=%s   截止=%s" % (out_dir, want, end))
    print("间隔: " + ", ".join("%s=%.1fs" % (k.split(".")[0], v)
                               for k, v in fetcher.gap.items()))
    print("日志: %s" % log.path)
    print("=" * 68)
    log.event("run_start", packs=want, end=end, gap=fetcher.gap)

    t_all = time.time()

    # 资金流要从 pack1 的板块归属表里取股票名单。分两晚跑时，pack1 是前一晚生成的，
    # 文件名带的是那天的日期——所以这里找目录下【最新的】 pack1，不能拼当天的 stamp。
    def find_pack1():
        cands = sorted(f for f in os.listdir(out_dir)
                       if f.startswith("pack1_boards_") and f.endswith(".sqlite"))
        return os.path.join(out_dir, cands[-1]) if cands else \
            os.path.join(out_dir, "pack1_boards_%s.sqlite" % stamp)

    for pid in want:
        if pid == 5:
            continue
        if pid not in PACKS:
            print("!! 未知的包 %s，跳过" % pid)
            continue
        pname, tasks = PACKS[pid]
        path = os.path.join(out_dir, "%s_%s.sqlite" % (pname, stamp))
        print("\n" + "=" * 68)
        print("【%s】 → %s" % (pname, os.path.basename(path)))
        pack = Pack(path)
        pack.set_meta("pack", pname)
        pack.set_meta("end", end)
        for task in tasks:
            # 增量起点：优先用"已合并到"的水位线，这样上次取了但没合并成功的会重取，不会丢
            key = task.table
            start = (args.start
                     or (state.get(key, {}) or {}).get("merged_to")
                     or DEFAULT_START)
            if task.date_field is None:
                start = DEFAULT_START      # 快照类全量重取
            pack.set_meta("%s_start" % key, start)
            pack.set_meta("%s_end" % key, end)
            try:
                run_task(fetcher, pack, task, start, end, log)
                state.setdefault(key, {})["fetched_to"] = end
                state[key]["fetched_at"] = datetime.now().isoformat(timespec="seconds")
                state[key]["pack"] = os.path.basename(path)
                save_state(out_dir, state)
            except KeyboardInterrupt:
                print("\n!! 中断，进度已保存，重跑会从断点继续")
                pack.close(); log.close(); sys.exit(1)
            except Exception as e:
                print("!! %s 出错: %s" % (task.table, e))
                log.event("task_error", task=task.table, err=str(e)[:300])
        print("\n  包内容: " + ", ".join("%s=%d" % (t, n) for t, n in pack.summary()))
        pack.close()

    if 5 in want:
        path = os.path.join(out_dir, "%s_%s.sqlite" % (FLOW_PACK, stamp))
        print("\n" + "=" * 68)
        print("【%s】 → %s" % (FLOW_PACK, os.path.basename(path)))
        pack1_path = find_pack1()
        codes = load_codes(args.flow_scope, out_dir, pack1_path)
        if not codes:
            print("!! 拿不到股票名单：先跑 pack1，或在 %s 放一份 codes.txt（每行一个6位代码）"
                  % out_dir)
        else:
            est = len(codes) * fetcher.gap.get("push2his.eastmoney.com", 5.0) / 3600
            print("  共 %d 只，按当前间隔预计 %.1f 小时" % (len(codes), est))
            if len(codes) > 1500:
                print("  想缩短的话：往 %s\\codes.txt 放自定义名单（每行一个6位代码）" % out_dir)
            pack = Pack(path)
            pack.set_meta("pack", FLOW_PACK)
            pack.set_meta("scope", args.flow_scope)
            try:
                run_moneyflow(fetcher, pack, codes, log)
                state.setdefault("EmNetInflowDetail", {})["fetched_to"] = end
                state["EmNetInflowDetail"]["fetched_at"] = datetime.now().isoformat(timespec="seconds")
                state["EmNetInflowDetail"]["pack"] = os.path.basename(path)
                save_state(out_dir, state)
            except KeyboardInterrupt:
                print("\n!! 中断，进度已保存")
                pack.close(); log.close(); sys.exit(1)
            print("\n  包内容: " + ", ".join("%s=%d" % (t, n) for t, n in pack.summary()))
            pack.close()

    mins = (time.time() - t_all) / 60
    log.event("run_end", minutes=round(mins, 1), requests=log.total_count,
              by_host=log.host_count)
    print("\n" + "=" * 68)
    print("全部完成，用时 %.1f 分钟，共 %d 个请求" % (mins, log.total_count))
    for h, n in log.host_count.items():
        print("   %s: %d 次" % (h, n))
    print("日志: %s" % log.path)
    print("把 %s 下的 .sqlite 和 em_state.json 一起拷回主环境，用 em_merge.py 合并" % out_dir)
    print("=" * 68)
    log.close()


if __name__ == "__main__":
    main()
