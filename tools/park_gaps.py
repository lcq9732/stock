# -*- coding: utf-8 -*-
"""把待补名单（manifest.MissingBars）按类型拆开，好分别处理。

两类记录混在同一份名单里，但补法和复查方式完全不同：
  · 缺行（Reason=gap）——7000+ 段 / 100 万个交易日，绝大多数是十年的停牌日，
    补一轮要几小时、而且要走满两轮才能重新沉淀进「确认没有」白名单；
  · 值错（intraday / inconsistent / ohlc）——几千段，约 40 分钟。
【重新拉取失败】是先补缺行、后修值问题的，不拆的话中途停就轮不到值问题。

用法（在仓库根目录跑）：
    python tools/park_gaps.py show      看当前构成，不改任何东西
    python tools/park_gaps.py split     拆：manifest 只留值错段，缺行段存进 parked 文件
    python tools/park_gaps.py merge     合：把 parked 的缺行段并回 manifest
    python tools/park_gaps.py sample    抽样：只留每类一条值错段做试跑，其余暂存
    python tools/park_gaps.py restore   把 sample 暂存的值错段并回来

⚠ 只在**程序空闲**时跑。体检或【重新拉取失败】正在跑的时候 manifest 会被它们改写，
   这时候拆会互相覆盖。每次写 manifest 前都自动带时间戳备份。
"""
import json
import os
import shutil
import sys
from collections import Counter
from datetime import datetime

MANIFEST = os.path.join("publish", "data", "local", "manifest.json")
GAPS_PARKED = os.path.join("publish", "data", "local", "missing-gaps-parked.json")
VALUES_PARKED = os.path.join("publish", "data", "local", "missing-values-parked.json")
GAP = "gap"


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def save(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def reason(r):
    """老记录没有 Reason 字段 / 空串，一律当缺行——跟 MissingBarRange.EffectiveReason 一致。"""
    return r.get("Reason") or GAP


def key_of(r):
    return (r.get("Code"), r.get("Granularity"), reason(r))


def backup(path):
    dst = path + ".bak-park-" + datetime.now().strftime("%Y%m%d-%H%M%S")
    shutil.copy(path, dst)
    return dst


def describe(rows, title):
    if not rows:
        print("  %s：0 段" % title)
        return
    days = sum(r.get("Days", 0) for r in rows)
    print("  %s：%d 段 / %d 个交易日" % (title, len(rows), days))
    for k, n in sorted(Counter(reason(r) for r in rows).items()):
        sub = [r for r in rows if reason(r) == k]
        by_gran = dict(Counter(r.get("Granularity", "?") for r in sub))
        tries = dict(sorted(Counter(r.get("Tries", 0) for r in sub).items()))
        print("     %-14s %6d 段 / %8d 天   口径 %s   Tries %s"
              % (k, n, sum(x.get("Days", 0) for x in sub), by_gran, tries))


def parked_rows(path):
    if not os.path.exists(path):
        return []
    return load(path).get("MissingBars", [])


def cmd_show():
    rows = load(MANIFEST).get("MissingBars", [])
    print("manifest: %s" % MANIFEST)
    describe([r for r in rows if reason(r) == GAP], "缺行（gap）")
    describe([r for r in rows if reason(r) != GAP], "值错")
    for path, label in ((GAPS_PARKED, "已暂存的缺行"), (VALUES_PARKED, "已暂存的值错段")):
        rowsp = parked_rows(path)
        if rowsp:
            print()
            print("parked: %s" % path)
            describe(rowsp, label)


def _park(rows_to_park, rows_to_keep, path, note, label):
    """把一部分记录挪进 parked 文件，manifest 只留另一部分。"""
    if parked_rows(path):
        print("⚠ %s 里已经有暂存记录，先合并回去再拆，否则那批会被这次覆盖掉。中止。" % path)
        sys.exit(1)
    b = backup(MANIFEST)
    save(path, {"MissingBars": rows_to_park,
                "ParkedAt": datetime.now().isoformat(timespec="seconds"),
                "Note": note})
    m = load(MANIFEST)
    m["MissingBars"] = rows_to_keep
    save(MANIFEST, m)
    print()
    print("已拆分。manifest 备份：%s" % b)
    print("%s 已暂存到：%s" % (label, path))


def _unpark(path, label):
    """把 parked 文件里的记录并回 manifest（按 code+口径+原因去重，manifest 里已有的优先）。"""
    parked = parked_rows(path)
    if not parked:
        print("没有 %s（或里面是空的），没什么可合并的。" % path)
        return
    m = load(MANIFEST)
    rows = m.get("MissingBars", [])
    have = {key_of(r) for r in rows}
    add = [r for r in parked if key_of(r) not in have]
    skipped = len(parked) - len(add)

    print("合并前：")
    describe(rows, "manifest 现有")
    describe(add, "要并回来的" + label)
    if skipped:
        print("  （%d 段跳过：manifest 里已有同 code+口径+原因的记录，保留那边的 Tries）" % skipped)

    b = backup(MANIFEST)
    m["MissingBars"] = sorted(rows + add, key=lambda r: (r.get("Code", ""),
                                                         r.get("Granularity", ""),
                                                         reason(r)))
    save(MANIFEST, m)
    os.remove(path)
    print()
    print("已合并。manifest 备份：%s" % b)
    print("parked 文件已删除。现在名单里共 %d 段。" % len(m["MissingBars"]))


def cmd_split():
    rows = load(MANIFEST).get("MissingBars", [])
    gaps = [r for r in rows if reason(r) == GAP]
    vals = [r for r in rows if reason(r) != GAP]
    if not gaps:
        print("名单里没有缺行段，不用拆。")
        return
    print("拆分前：")
    describe(gaps, "缺行（要暂存）")
    describe(vals, "值错（留在 manifest）")
    _park(gaps, vals, GAPS_PARKED,
          "【全库数据体检】报出的缺行段，暂存以便先只修值问题。用 park_gaps.py merge 并回去。",
          "缺行段")
    print("现在跑【重新拉取失败】只会修值问题。")


def cmd_merge():
    _unpark(GAPS_PARKED, "缺行")


def cmd_sample():
    """只留每类一条值错段做试跑——首次跑新的修复路径时用，几分钟就能看出对不对。"""
    rows = load(MANIFEST).get("MissingBars", [])
    vals = [r for r in rows if reason(r) != GAP]
    if not vals:
        print("名单里没有值错段，没什么可试的。")
        return

    picked = []
    for k in sorted({reason(r) for r in vals}):
        same = [r for r in vals if reason(r) == k]
        # 优先挑手上有确切现状、好验证的（002650 的 2026-09-01 是盘中固化的标准样本）
        pref = [r for r in same if r.get("Code") in ("002650", "000013")]
        picked.append((pref or same)[0])
    picked_ids = {id(r) for r in picked}
    rest = [r for r in vals if id(r) not in picked_ids]

    print("挑出来试跑的：")
    for r in picked:
        print("   %-8s %-8s %-14s %s~%s %d天 Tries=%s"
              % (r.get("Code"), r.get("Granularity"), reason(r),
                 r.get("From", "")[:10], r.get("To", "")[:10], r.get("Days", 0), r.get("Tries")))
    describe(rest, "暂存起来的其余值错段")

    keep = [r for r in rows if reason(r) == GAP] + picked
    _park(rest, keep, VALUES_PARKED,
          "试跑期间暂存的值错段。用 park_gaps.py restore 并回去。", "其余值错段")
    print("现在跑【重新拉取失败】只会动这几段，几分钟就完。")


def cmd_restore():
    _unpark(VALUES_PARKED, "值错段")


if __name__ == "__main__":
    if not os.path.exists(MANIFEST):
        print("找不到 %s——请在仓库根目录运行。" % MANIFEST)
        sys.exit(1)
    cmd = sys.argv[1] if len(sys.argv) > 1 else "show"
    {"show": cmd_show, "split": cmd_split, "merge": cmd_merge,
     "sample": cmd_sample, "restore": cmd_restore}.get(cmd, cmd_show)()
