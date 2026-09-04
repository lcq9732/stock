# -*- coding: utf-8 -*-
"""
东财数据包合并 —— 在【主环境】运行，把移动硬盘上的 pack*.sqlite 合并进主库。

跟 em_fetch.py 的分工：取数端只管把东财返回的原始字段原样落盘（列名保持大写），
所有字段映射、类型转换、日期规范化都在这一步做。这样映射写错了改这个脚本重跑即可，
**不用重新取一夜数据**。

三条硬规则
----------
1. **只建新表，一张现有表都不动。** 主库 15GB，NetInflow 有 1077 万行，
   ALTER TABLE 是重操作；而且源不同混一张表，以后分不清哪行来自哪。
2. **幂等**：全部 INSERT OR REPLACE + 组合主键，同一个包合并两次结果一样。
3. **日期统一截成 YYYY-MM-DD**。东财返回的是 "2026-09-02 00:00:00"，
   而主库现有表（MarginDetail.trade_date / Lhb.trade_date 等）用的是 10 位日期，
   不截断以后 join 不上。

用法
----
    python em_merge.py --pack E:\\em\\pack1_boards_20260903.sqlite      # 合并单个包
    python em_merge.py --dir E:\\em                                     # 合并目录下所有未合并的包
    python em_merge.py --dir E:\\em --dry-run                           # 只看会发生什么
    python em_merge.py --pack ... --db D:\\other\\current.sqlite         # 指定主库
"""

import argparse
import json
import os
import sqlite3
import sys
import time
from datetime import datetime

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

DEFAULT_DB = r"C:\Chingli\Git\stock\publish\data\local\current.sqlite"
BATCH = 5000


# ══════════════════════════════════════════════════════════════════════
# 目标表定义
# ══════════════════════════════════════════════════════════════════════
# src   : pack 库里的表名
# ddl   : 主库建表语句
# cols  : [(目标列, 源字段, 类型)] 类型 t=文本 d=日期(截10位) n=数值 i=整数
class Target:
    def __init__(self, name, src, pk, cols, indexes=(), note=""):
        self.name = name
        self.src = src
        self.pk = pk
        self.cols = cols
        self.indexes = indexes
        self.note = note

    def ddl(self):
        types = {"t": "TEXT", "d": "TEXT", "n": "REAL", "i": "INTEGER"}
        body = ", ".join("%s %s" % (c[0], types[c[2]]) for c in self.cols)
        return "CREATE TABLE IF NOT EXISTS %s (%s, fetched_at TEXT, PRIMARY KEY(%s))" % (
            self.name, body, ", ".join(self.pk))


TARGETS = [
    Target("EmStockBoard", "EmStockBoard",
           ["code", "board_code"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("board_code", "NEW_BOARD_CODE", "t"),
            ("board_code_raw", "BOARD_CODE", "t"),
            ("board_name", "BOARD_NAME", "t"),
            ("board_type", "BOARD_TYPE", "t"),
            ("board_level", "BOARD_LEVEL", "t"),
            ("board_rank", "BOARD_RANK", "i"),
            ("reason", "SELECTED_BOARD_REASON", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emsb_board ON EmStockBoard(board_code)",
                    "CREATE INDEX IF NOT EXISTS ix_emsb_name ON EmStockBoard(board_name)"],
           note="个股板块归属"),

    Target("EmEarningsForecast", "EmEarningsForecast",
           ["code", "report_date", "notice_date", "predict_finance_code"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("report_date", "REPORT_DATE", "d"),
            ("notice_date", "NOTICE_DATE", "d"),
            ("predict_finance_code", "PREDICT_FINANCE_CODE", "t"),
            ("predict_finance", "PREDICT_FINANCE", "t"),
            ("amt_lower", "PREDICT_AMT_LOWER", "n"),
            ("amt_upper", "PREDICT_AMT_UPPER", "n"),
            ("amp_lower", "ADD_AMP_LOWER", "n"),
            ("amp_upper", "ADD_AMP_UPPER", "n"),
            ("predict_type", "PREDICT_TYPE", "t"),
            ("content", "PREDICT_CONTENT", "t"),
            # 公司自述的业绩变动原因——可以从里面提"涨价/供不应求/产能满载"这类词，
            # 这是法定披露文件里公司自己写的，比研报硬。
            ("reason", "CHANGE_REASON_EXPLAIN", "t"),
            ("preyear_same_period", "PREYEAR_SAME_PERIOD", "n"),
            ("is_latest", "IS_LATEST", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emef_rd ON EmEarningsForecast(report_date)",
                    "CREATE INDEX IF NOT EXISTS ix_emef_nd ON EmEarningsForecast(notice_date)"],
           note="业绩预告"),

    Target("EmEarningsExpress", "EmEarningsExpress",
           ["code", "report_date"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("report_date", "REPORT_DATE", "d"),
            ("notice_date", "NOTICE_DATE", "d"),
            ("update_date", "UPDATE_DATE", "d"),
            ("eps", "BASIC_EPS", "n"),
            ("revenue", "TOTAL_OPERATE_INCOME", "n"),
            ("revenue_yoy", "YSTZ", "n"),
            ("np_parent", "PARENT_NETPROFIT", "n"),
            ("np_yoy", "JLRTBZCL", "n"),
            ("bvps", "PARENT_BVPS", "n"),
            ("roe", "WEIGHTAVG_ROE", "n"),
            ("revenue_qoq", "DJDYSHZ", "n"),
            ("np_qoq", "DJDJLHZ", "n")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emee_rd ON EmEarningsExpress(report_date)"],
           note="业绩快报"),

    Target("EmLhbSeat", "EmLhbSeatBuy",
           ["trade_date", "code", "side", "operatedept_code", "trade_id"],
           [("trade_date", "TRADE_DATE", "d"),
            ("code", "SECURITY_CODE", "t"),
            ("side", None, "t"),                 # 由合并阶段填 'B'/'S'
            ("operatedept_code", "OPERATEDEPT_CODE", "t"),
            ("operatedept_name", "OPERATEDEPT_NAME", "t"),
            ("buy", "BUY", "n"),
            ("sell", "SELL", "n"),
            ("net", "NET", "n"),
            ("explanation", "EXPLANATION", "t"),
            ("change_rate", "CHANGE_RATE", "n"),
            ("close_price", "CLOSE_PRICE", "n"),
            ("accum_amount", "ACCUM_AMOUNT", "n"),
            # 该营业部近期3日胜率——有营业部代码+胜率就能自己建游资库
            ("rise_prob_3day", "RISE_PROBABILITY_3DAY", "n"),
            ("sale_times_3day", "TOTAL_BUYER_SALESTIMES_3DAY", "n"),
            ("buy_ratio", "TOTAL_BUYRIO", "n"),
            ("sell_ratio", "TOTAL_SELLRIO", "n"),
            ("change_type", "CHANGE_TYPE", "t"),
            ("trade_id", "TRADE_ID", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emls_code ON EmLhbSeat(code, trade_date)",
                    "CREATE INDEX IF NOT EXISTS ix_emls_dept ON EmLhbSeat(operatedept_code)"],
           note="龙虎榜买方席位"),

    # 卖方进同一张表，side='S'
    Target("EmLhbSeat", "EmLhbSeatSell", ["trade_date", "code", "side",
                                          "operatedept_code", "trade_id"],
           [], note="龙虎榜卖方席位"),

    Target("EmLhbDetail", "EmLhbDetail",
           ["trade_date", "code", "trade_id"],
           [("trade_date", "TRADE_DATE", "d"),
            ("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("close_price", "CLOSE_PRICE", "n"),
            ("change_rate", "CHANGE_RATE", "n"),
            ("turnover_rate", "TURNOVERRATE", "n"),
            ("billboard_buy", "BILLBOARD_BUY_AMT", "n"),
            ("billboard_sell", "BILLBOARD_SELL_AMT", "n"),
            ("billboard_net", "BILLBOARD_NET_AMT", "n"),
            ("billboard_deal", "BILLBOARD_DEAL_AMT", "n"),
            ("deal_amount_ratio", "DEAL_AMOUNT_RATIO", "n"),
            ("deal_net_ratio", "DEAL_NET_RATIO", "n"),
            ("accum_amount", "ACCUM_AMOUNT", "n"),
            ("free_market_cap", "FREE_MARKET_CAP", "n"),
            ("explain", "EXPLAIN", "t"),
            ("explanation", "EXPLANATION", "t"),
            ("change_type", "CHANGE_TYPE", "t"),
            ("d1_chg", "D1_CLOSE_ADJCHRATE", "n"),
            ("d2_chg", "D2_CLOSE_ADJCHRATE", "n"),
            ("d5_chg", "D5_CLOSE_ADJCHRATE", "n"),
            ("d10_chg", "D10_CLOSE_ADJCHRATE", "n"),
            ("trade_id", "TRADE_ID", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emld_code ON EmLhbDetail(code, trade_date)"],
           note="龙虎榜每日明细"),

    Target("EmNetInflowDetail", "EmNetInflowDetail",
           ["code", "trade_date"],
           [("code", "SECURITY_CODE", "t"),
            ("trade_date", "TRADE_DATE", "d"),
            ("main_net", "MAIN_NET", "n"),
            ("super_net", "SUPER_NET", "n"),
            ("big_net", "BIG_NET", "n"),
            ("mid_net", "MID_NET", "n"),
            ("small_net", "SMALL_NET", "n"),
            ("main_ratio", "MAIN_RATIO", "n"),
            ("super_ratio", "SUPER_RATIO", "n"),
            ("big_ratio", "BIG_RATIO", "n"),
            ("mid_ratio", "MID_RATIO", "n"),
            ("small_ratio", "SMALL_RATIO", "n"),
            ("close_price", "CLOSE_PRICE", "n"),
            ("change_rate", "CHANGE_RATE", "n")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emnid_date ON EmNetInflowDetail(trade_date)"],
           note="资金流分级"),

    Target("EmBlockTrade", "EmBlockTrade",
           ["trade_date", "code", "daily_rank"],
           [("trade_date", "TRADE_DATE", "d"),
            ("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("daily_rank", "DAILY_RANK", "i"),
            ("deal_price", "DEAL_PRICE", "n"),
            ("deal_volume", "DEAL_VOLUME", "n"),
            ("deal_amt", "DEAL_AMT", "n"),
            ("premium_ratio", "PREMIUM_RATIO", "n"),
            ("close_price", "CLOSE_PRICE", "n"),
            ("turnover_rate", "TURNOVER_RATE", "n"),
            ("change_rate", "CHANGE_RATE", "n"),
            ("buyer_name", "BUYER_NAME", "t"),
            ("seller_name", "SELLER_NAME", "t"),
            ("buyer_code", "BUYER_CODE", "t"),
            ("seller_code", "SELLER_CODE", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_embt_code ON EmBlockTrade(code, trade_date)"],
           note="大宗交易"),

    Target("EmOrgSurvey", "EmOrgSurvey",
           ["code", "receive_start_date", "receive_object", "receive_way"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("notice_date", "NOTICE_DATE", "d"),
            ("receive_start_date", "RECEIVE_START_DATE", "d"),
            ("receive_end_date", "RECEIVE_END_DATE", "d"),
            ("receive_way", "RECEIVE_WAY", "t"),
            ("receive_place", "RECEIVE_PLACE", "t"),
            ("receive_object_type", "RECEIVE_OBJECT_TYPE", "t"),
            ("receive_object", "RECEIVE_OBJECT", "t"),
            ("investigators", "INVESTIGATORS", "t"),
            ("receptionist", "RECEPTIONIST", "t"),
            ("num", "NUM", "n"),
            ("org_type", "ORG_TYPE", "t"),
            ("org_name", "ORG_NAME", "t")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emos_code ON EmOrgSurvey(code, receive_start_date)"],
           note="机构调研"),

    Target("EmShareLift", "EmShareLift",
           ["code", "free_date", "free_shares_type"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("free_date", "FREE_DATE", "d"),
            ("free_shares_type", "FREE_SHARES_TYPE", "t"),
            ("current_free_shares", "CURRENT_FREE_SHARES", "n"),
            ("lift_market_cap", "LIFT_MARKET_CAP", "n"),
            ("free_shares", "FREE_SHARES", "n"),
            ("non_free_shares", "NON_FREE_SHARES", "n"),
            ("free_ratio", "FREE_RATIO", "n"),
            ("total_ratio", "TOTAL_RATIO", "n"),
            ("batch_holder_num", "BATCH_HOLDER_NUM", "n"),
            ("b20_chg", "B20_ADJCHRATE", "n"),
            ("a20_chg", "A20_ADJCHRATE", "n")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emsl_date ON EmShareLift(free_date)"],
           note="限售解禁（含未来计划）"),

    Target("EmHolderChange", "EmHolderChange",
           ["code", "notice_date", "holder_name", "end_date"],
           [("code", "SECURITY_CODE", "t"),
            ("name", "SECURITY_NAME_ABBR", "t"),
            ("notice_date", "NOTICE_DATE", "d"),
            ("start_date", "START_DATE", "d"),
            ("end_date", "END_DATE", "d"),
            ("holder_name", "HOLDER_NAME", "t"),
            ("direction", "DIRECTION", "t"),
            ("change_num", "CHANGE_NUM", "n"),
            ("change_num_symbol", "CHANGE_NUM_SYMBOL", "n"),
            ("change_rate", "CHANGE_RATE", "n"),
            ("after_holder_num", "AFTER_HOLDER_NUM", "n"),
            ("after_change_rate", "AFTER_CHANGE_RATE", "n"),
            ("hold_ratio", "HOLD_RATIO", "n"),
            ("free_shares", "FREE_SHARES", "n"),
            ("trade_avg_price", "TRADE_AVERAGE_PRICE", "n")],
           indexes=["CREATE INDEX IF NOT EXISTS ix_emhc_code ON EmHolderChange(code, notice_date)"],
           note="股东增减持"),
]

# 卖方席位复用买方的列定义，只是 side 不同
_buy = next(t for t in TARGETS if t.src == "EmLhbSeatBuy")
for t in TARGETS:
    if t.src == "EmLhbSeatSell":
        t.cols = _buy.cols
        t.indexes = ()          # 索引买方那条已经建了

SIDE_OF = {"EmLhbSeatBuy": "B", "EmLhbSeatSell": "S"}

# pack 里的表 → 状态文件里的 key（供 merged_to 水位线用）
STATE_KEY = {
    "EmStockBoard": "EmStockBoard",
    "EmEarningsForecast": "EmEarningsForecast",
    "EmEarningsExpress": "EmEarningsExpress",
    "EmLhbSeatBuy": "EmLhbSeatBuy",
    "EmLhbSeatSell": "EmLhbSeatSell",
    "EmLhbDetail": "EmLhbDetail",
    "EmNetInflowDetail": "EmNetInflowDetail",
    "EmBlockTrade": "EmBlockTrade",
    "EmOrgSurvey": "EmOrgSurvey",
    "EmShareLift": "EmShareLift",
    "EmHolderChange": "EmHolderChange",
}


# ══════════════════════════════════════════════════════════════════════
def conv(val, kind):
    if val is None or val == "" or val == "None":
        return None
    if kind == "d":
        return str(val)[:10]           # "2026-09-02 00:00:00" → "2026-09-02"
    if kind in ("n", "i"):
        try:
            f = float(val)
            return int(f) if kind == "i" else f
        except (TypeError, ValueError):
            return None
    return str(val)


def ensure_schema(db, target):
    db.execute(target.ddl())
    for ix in target.indexes:
        db.execute(ix)
    db.commit()


def merge_table(db, pack_con, target, dry=False):
    """把 pack 里的一张表合进主库。整表一个事务，出错回滚。"""
    cur = pack_con.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (target.src,))
    if not cur.fetchone():
        return None

    total_src = pack_con.execute('SELECT COUNT(*) FROM "%s"' % target.src).fetchone()[0]
    if total_src == 0:
        return (target.name, target.src, 0, 0, 0)

    if dry:
        before = 0
        try:
            before = db.execute("SELECT COUNT(*) FROM %s" % target.name).fetchone()[0]
        except sqlite3.OperationalError:
            pass
        return (target.name, target.src, total_src, before, None)

    ensure_schema(db, target)
    before = db.execute("SELECT COUNT(*) FROM %s" % target.name).fetchone()[0]

    # 源表实际有哪些列（东财偶尔加字段/某些包缺字段，取交集）
    src_cols = set(r[1] for r in pack_con.execute('PRAGMA table_info("%s")' % target.src))
    side = SIDE_OF.get(target.src)

    dst_names = [c[0] for c in target.cols]
    sql = "INSERT OR REPLACE INTO %s (%s, fetched_at) VALUES(%s)" % (
        target.name, ", ".join(dst_names), ",".join("?" * (len(dst_names) + 1)))

    sel_cols = [c[1] for c in target.cols if c[1] and c[1] in src_cols]
    cur = pack_con.execute('SELECT %s, _fetched_at FROM "%s"' % (
        ", ".join('"%s"' % c for c in sel_cols), target.src))

    n = 0
    batch = []
    db.execute("BEGIN")
    try:
        while True:
            rows = cur.fetchmany(BATCH)
            if not rows:
                break
            for r in rows:
                d = dict(zip(sel_cols, r[:-1]))
                fetched_at = r[-1]
                out = []
                for dst, srcf, kind in target.cols:
                    if srcf is None:            # side 这种由合并阶段生成的列
                        out.append(side)
                    else:
                        out.append(conv(d.get(srcf), kind))
                out.append(fetched_at)
                batch.append(out)
            db.executemany(sql, batch)
            n += len(batch)
            batch = []
        db.execute("COMMIT")
    except Exception:
        db.execute("ROLLBACK")
        raise

    after = db.execute("SELECT COUNT(*) FROM %s" % target.name).fetchone()[0]
    return (target.name, target.src, n, before, after)


def rebuild_board_list(db):
    """从 EmStockBoard 派生板块清单，省得单独存一份、还可能对不上。"""
    db.execute("""CREATE TABLE IF NOT EXISTS EmBoard(
                    board_code TEXT PRIMARY KEY, board_name TEXT,
                    board_type TEXT, member_count INTEGER, updated_at TEXT)""")
    db.execute("DELETE FROM EmBoard")
    db.execute("""INSERT INTO EmBoard
                  SELECT board_code, MAX(board_name), MAX(board_type),
                         COUNT(DISTINCT code), ?
                  FROM EmStockBoard WHERE board_code IS NOT NULL
                  GROUP BY board_code""",
               (datetime.now().isoformat(timespec="seconds"),))
    db.commit()
    return db.execute("SELECT COUNT(*) FROM EmBoard").fetchone()[0]


def merge_log_init(db):
    # 主键必须带 src_table：龙虎榜买方/卖方是两张源表但合进同一张 EmLhbSeat，
    # 只用 (pack_file, table_name) 的话第二条会覆盖第一条，日志里的行数就少一半。
    db.execute("""CREATE TABLE IF NOT EXISTS EmMergeLog(
                    pack_file TEXT, src_table TEXT, table_name TEXT, rows INTEGER,
                    before_rows INTEGER, after_rows INTEGER,
                    merged_at TEXT, PRIMARY KEY(pack_file, src_table))""")
    db.commit()


def already_merged(db, pack_file):
    cur = db.execute("SELECT src_table, rows, merged_at FROM EmMergeLog WHERE pack_file=?",
                     (os.path.basename(pack_file),))
    return cur.fetchall()


# ══════════════════════════════════════════════════════════════════════
def merge_pack(db, pack_path, state, dry=False, force=False):
    name = os.path.basename(pack_path)
    print("\n" + "=" * 68)
    print("包: %s  (%.1f MB)" % (name, os.path.getsize(pack_path) / 1e6))

    prev = already_merged(db, pack_path)
    if prev and not force and not dry:
        print("  已合并过 (%s)，跳过。要重合并加 --force" % prev[0][2])
        for t, r, at in prev:
            print("     %s: %d 行 @ %s" % (t, r, at))
        return []

    pc = sqlite3.connect("file:%s?mode=ro" % pack_path.replace("\\", "/"), uri=True)
    results = []
    try:
        meta = {}
        try:
            meta = dict(pc.execute("SELECT k,v FROM _meta").fetchall())
        except Exception:
            pass
        if meta:
            print("  meta: " + ", ".join("%s=%s" % (k, v) for k, v in sorted(meta.items())[:6]))

        for target in TARGETS:
            r = merge_table(db, pc, target, dry=dry)
            if r is None:
                continue
            tname, src, rows, before, after = r
            results.append(r)
            if dry:
                print("  [预览] %-22s ← %-20s %8d 行 (主库现有 %d)"
                      % (tname, src, rows, before))
            else:
                added = (after - before) if after is not None else 0
                print("  %-22s ← %-20s 写入 %8d 行，净增 %8d（%d → %d）"
                      % (tname, src, rows, added, before, after))
                db.execute("INSERT OR REPLACE INTO EmMergeLog VALUES(?,?,?,?,?,?,?)",
                           (name, src, tname, rows, before, after,
                            datetime.now().isoformat(timespec="seconds")))
                db.commit()
                # 水位线：这张表已经合并到哪天
                key = STATE_KEY.get(src)
                if key:
                    end = meta.get("%s_end" % src) or meta.get("end")
                    if end:
                        state.setdefault(key, {})["merged_to"] = end
                        state[key]["merged_at"] = datetime.now().isoformat(timespec="seconds")
                        state[key]["merged_from"] = name
    finally:
        pc.close()
    return results


def main():
    ap = argparse.ArgumentParser(description="东财数据包合并进主库")
    ap.add_argument("--pack", help="单个包文件")
    ap.add_argument("--dir", help="包目录（合并目录下所有 pack*.sqlite）")
    ap.add_argument("--db", default=DEFAULT_DB, help="主库路径")
    ap.add_argument("--dry-run", action="store_true", help="只预览不写入")
    ap.add_argument("--force", action="store_true", help="重新合并已合并过的包")
    args = ap.parse_args()

    if not args.pack and not args.dir:
        ap.error("--pack 或 --dir 至少给一个")

    packs = []
    if args.pack:
        packs = [args.pack]
    else:
        packs = sorted(os.path.join(args.dir, f) for f in os.listdir(args.dir)
                       if f.startswith("pack") and f.endswith(".sqlite"))
    if not packs:
        print("没找到任何 pack*.sqlite")
        return

    if not os.path.exists(args.db):
        print("!! 主库不存在: %s" % args.db)
        sys.exit(1)

    print("=" * 68)
    print("主库: %s  (%.1f GB)" % (args.db, os.path.getsize(args.db) / 1e9))
    print("待合并 %d 个包%s" % (len(packs), "  [预览模式]" if args.dry_run else ""))
    print("=" * 68)

    # 主库可能正被 Fetcher/Analyzer 打开，给足超时并明确提示
    db = sqlite3.connect(args.db, timeout=60)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=NORMAL")
    if not args.dry_run:
        merge_log_init(db)

    state_dir = args.dir or os.path.dirname(packs[0])
    state_path = os.path.join(state_dir, "em_state.json")
    state = {}
    if os.path.exists(state_path):
        try:
            state = json.load(open(state_path, encoding="utf-8"))
        except Exception:
            state = {}

    t0 = time.time()
    touched_boards = False
    try:
        for p in packs:
            res = merge_pack(db, p, state, dry=args.dry_run, force=args.force)
            if any(r[0] == "EmStockBoard" for r in res):
                touched_boards = True
    except sqlite3.OperationalError as e:
        if "locked" in str(e).lower():
            print("\n!! 主库被占用——先关掉 Fetcher / Analyzer 再重跑")
        raise

    if touched_boards and not args.dry_run:
        n = rebuild_board_list(db)
        print("\n  重建 EmBoard 板块清单: %d 个板块" % n)

    if not args.dry_run:
        with open(state_path, "w", encoding="utf-8") as f:
            json.dump(state, f, ensure_ascii=False, indent=1)
        print("\n  状态已写回: %s" % state_path)
        print("  （下次取数会从各表的 merged_to 继续，不会重复取也不会丢）")

    db.close()
    print("\n" + "=" * 68)
    print("完成，用时 %.1f 秒" % (time.time() - t0))
    print("=" * 68)


if __name__ == "__main__":
    main()
