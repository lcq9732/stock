# -*- coding: utf-8 -*-
"""把待补名单里的「缺行」段暂存开，只留「值错」段——好让【重新拉取失败】先只修值问题。

为什么要拆：体检报出的缺行有 7000+ 段 / 100 万个交易日，绝大多数是十年的停牌日，补一轮要
几小时、而且要走满两轮才能重新沉淀进「确认没有」白名单。值问题只有几千段、约 40 分钟。
【重新拉取失败】是先补缺行、后修值问题的，不拆的话中途停就轮不到值问题。

用法（在仓库根目录跑）：
    python park_gaps.py show      看当前构成，不改任何东西
    python park_gaps.py split     拆：manifest 只留值错段，缺行段存进 parked 文件
    python park_gaps.py merge     合：把 parked 的缺行段并回 manifest（值错段保持现状）

⚠ 只在**程序空闲**时跑。体检或【重新拉取失败】正在跑的时候 manifest 会被它们改写，
   这时候拆会互相覆盖。
"""
import json
import os
import shutil
import sys
from collections import Counter
from datetime import datetime

MANIFEST = os.path.join("publish", "data", "local", "manifest.json")
PARKED = os.path.join("publish", "data", "local", "missing-gaps-parked.json")
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


def backup(path):
    dst = f"{path}.bak-park-{datetime.now():%Y%m%d-%H%M%S}"
    shutil.copy(path, dst)
    return dst


def describe(rows, title):
    if not rows:
        print(f"  {title}：0 段")
        return
    by_reason = Counter(reason(r) for r in rows)
    days = sum(r.get("Days", 0) for r in rows)
    print(f"  {title}：{len(rows)} 段 / {days} 个交易日")
    for k, n in sorted(by_reason.items()):
        sub = [r for r in rows if reason(r) == k]
        by_gran = Counter(r.get("Granularity", "?") for r in sub)
        tries = Counter(r.get("Tries", 0) for r in sub)
        print(f"     {k:<14} {n:>6} 段 / {sum(x.get('Days', 0) for x in sub):>8} 天"
              f"   口径 {dict(by_gran)}   Tries {dict(sorted(tries.items()))}")


def cmd_show():
    m = load(MANIFEST)
    rows = m.get("MissingBars", [])
    gaps = [r for r in rows if reason(r) == GAP]
    vals = [r for r in rows if reason(r) != GAP]
    print(f"manifest: {MANIFEST}")
    describe(gaps, "缺行（gap）")
    describe(vals, "值错")
    if os.path.exists(PARKED):
        parked = load(PARKED).get("MissingBars", [])
        print(f"\nparked: {PARKED}")
        describe(parked, "已暂存的缺行")
    else:
        print(f"\nparked: 还没有（{PARKED}）")


def cmd_split():
    m = load(MANIFEST)
    rows = m.get("MissingBars", [])
    gaps = [r for r in rows if reason(r) == GAP]
    vals = [r for r in rows if reason(r) != GAP]
    if not gaps:
        print("名单里没有缺行段，不用拆。")
        return

    if os.path.exists(PARKED):
        old = load(PARKED).get("MissingBars", [])
        if old:
            print(f"⚠ {PARKED} 里已经有 {len(old)} 段暂存的缺行。")
            print("  先 merge 回去再拆，否则那批会被这次覆盖掉。中止。")
            sys.exit(1)

    print("拆分前：")
    describe(gaps, "缺行（要暂存）")
    describe(vals, "值错（留在 manifest）")

    b = backup(MANIFEST)
    save(PARKED, {"MissingBars": gaps,
                  "ParkedAt": datetime.now().isoformat(timespec="seconds"),
                  "Note": "【全库数据体检】报出的缺行段，暂存以便先只修值问题。用 park_gaps.py merge 并回去。"})
    m["MissingBars"] = vals
    save(MANIFEST, m)
    print(f"\n已拆分。manifest 备份：{b}")
    print(f"缺行段已暂存到：{PARKED}")
    print("现在跑【重新拉取失败】只会修值问题。")


def cmd_merge():
    if not os.path.exists(PARKED):
        print(f"没有 {PARKED}，没什么可合并的。")
        return
    parked = load(PARKED).get("MissingBars", [])
    if not parked:
        print("parked 文件里是空的。")
        return

    m = load(MANIFEST)
    rows = m.get("MissingBars", [])
    # 按 (Code, Granularity, Reason) 去重：manifest 里已经有的那条优先（它的 Tries 更新）
    have = {(r.get("Code"), r.get("Granularity"), reason(r)) for r in rows}
    add = [r for r in parked if (r.get("Code"), r.get("Granularity"), reason(r)) not in have]
    skipped = len(parked) - len(add)

    print("合并前：")
    describe(rows, "manifest 现有")
    describe(add, "要并回来的缺行")
    if skipped:
        print(f"  （{skipped} 段跳过：manifest 里已有同 code+口径+原因的记录，保留那边的 Tries）")

    b = backup(MANIFEST)
    m["MissingBars"] = sorted(rows + add,
                              key=lambda r: (r.get("Code", ""), r.get("Granularity", ""), reason(r)))
    save(MANIFEST, m)
    os.remove(PARKED)
    print(f"\n已合并。manifest 备份：{b}")
    print(f"parked 文件已删除。现在名单里共 {len(m['MissingBars'])} 段。")


if __name__ == "__main__":
    if not os.path.exists(MANIFEST):
        print(f"找不到 {MANIFEST}——请在仓库根目录运行。")
        sys.exit(1)
    cmd = sys.argv[1] if len(sys.argv) > 1 else "show"
    {"show": cmd_show, "split": cmd_split, "merge": cmd_merge}.get(cmd, cmd_show)()
