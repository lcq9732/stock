# -*- coding: utf-8 -*-
"""
取数日志分析 —— 从 em_fetch.py 产生的 JSONL 里反推"多快算安全"。

要回答的核心问题是**限流是按域名各算各的，还是按 IP 总量算的**：
  模型 A（各域名独立额度）→ 跨域名并发是安全的，能省一半时间
  模型 B（IP 总额度）      → 并发只会更快撞墙，必须串行

判据：某个域名被限的那一刻，另一个域名此前累计发了多少、之后还能不能继续成功。
如果 A 域名被掐时 B 域名照常工作，就是模型 A；如果几乎同时哑掉，就是模型 B。

用法：
    python em_analyze_log.py E:\\em\\logs\\fetch-20260903.jsonl
"""

import io
import json
import sys
from collections import defaultdict

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def load(path):
    reqs, events = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            (events if "event" in r else reqs).append(r)
    return reqs, events


def pct(vals, p):
    if not vals:
        return 0
    s = sorted(vals)
    i = int(len(s) * p / 100.0)
    return s[min(i, len(s) - 1)]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    path = sys.argv[1]
    reqs, events = load(path)
    if not reqs:
        print("日志里没有请求记录")
        return

    print("=" * 74)
    print("取数日志分析: %s" % path)
    print("共 %d 条请求, %d 条事件" % (len(reqs), len(events)))
    print("=" * 74)

    # ── 各域名总览 ─────────────────────────────────────────────────
    by_host = defaultdict(list)
    for r in reqs:
        by_host[r.get("host", "?")].append(r)

    print("\n【各域名总览】")
    print("%-30s %7s %7s %7s  %9s %9s" %
          ("域名", "请求", "成功", "失败", "耗时中位", "P95"))
    for h, rs in sorted(by_host.items()):
        ok = [r for r in rs if r.get("http") == 200]
        bad = [r for r in rs if r.get("http") != 200]
        ms = [r.get("elapsed_ms", 0) for r in ok]
        print("%-30s %7d %7d %7d  %7dms %7dms" %
              (h, len(rs), len(ok), len(bad), pct(ms, 50), pct(ms, 95)))

    # ── 失败/限流事件 ──────────────────────────────────────────────
    fails = [r for r in reqs if r.get("http") != 200]
    print("\n【失败分布】共 %d 次" % len(fails))
    if fails:
        by_err = defaultdict(int)
        for r in fails:
            e = (r.get("error") or "")[:60]
            by_err[(r.get("host"), e)] += 1
        for (h, e), n in sorted(by_err.items(), key=lambda x: -x[1])[:10]:
            print("   %-28s %4d 次  %s" % (h, n, e))

    # ── 关键：第一次被限的时刻，各域名各发了多少 ─────────────────
    print("\n【限流模型判定】")
    first_fail = {}
    for r in reqs:
        h = r.get("host")
        if r.get("http") != 200 and h not in first_fail:
            first_fail[h] = r
    if not first_fail:
        print("   整轮没有任何失败 —— 当前间隔是安全的，下次可以试着调快。")
    else:
        for h, r in sorted(first_fail.items(), key=lambda x: x[1].get("total_seq", 0)):
            ts = r.get("ts", "")[:19]
            print("\n   %s 首次失败 @ %s" % (h, ts))
            print("      该域名此前累计: %d 次" % (r.get("host_seq", 0) - 1))
            print("      全局此前累计:   %d 次" % (r.get("total_seq", 0) - 1))
            print("      当时间隔设置:   %.1fs" % (r.get("gap_before") or 0))
            # 同一时刻其它域名的状态
            t = r.get("elapsed_total_s", 0)
            for oh, ors in by_host.items():
                if oh == h:
                    continue
                after = [x for x in ors if x.get("elapsed_total_s", 0) > t]
                ok_after = [x for x in after if x.get("http") == 200]
                if after:
                    print("      → %s 在此之后 %d 次请求, %d 次成功 %s"
                          % (oh, len(after), len(ok_after),
                             "【仍正常 → 倾向模型A·独立额度】" if len(ok_after) > len(after) * 0.8
                             else "【也受影响 → 倾向模型B·IP总额度】"))

    # ── 各域名的最长连续成功段 → 安全阈值参考 ────────────────────
    print("\n【各域名最长连续成功段】（这是该间隔下已验证能扛住的量）")
    for h, rs in sorted(by_host.items()):
        rs = sorted(rs, key=lambda x: x.get("host_seq", 0))
        best = cur = 0
        gaps = []
        for r in rs:
            if r.get("http") == 200:
                cur += 1
                if r.get("gap_before"):
                    gaps.append(r["gap_before"])
                best = max(best, cur)
            else:
                cur = 0
        avg_gap = sum(gaps) / len(gaps) if gaps else 0
        print("   %-28s 连续成功 %5d 次 @ 平均间隔 %.1fs" % (h, best, avg_gap))

    # ── 响应时间漂移（限流前兆）────────────────────────────────
    print("\n【响应时间漂移】按该域名请求序号分十段看中位耗时")
    print("   （明显变慢通常是被限的前兆，可以据此提前减速）")
    for h, rs in sorted(by_host.items()):
        ok = sorted([r for r in rs if r.get("http") == 200],
                    key=lambda x: x.get("host_seq", 0))
        if len(ok) < 20:
            continue
        step = max(1, len(ok) // 10)
        cells = []
        for i in range(0, len(ok), step):
            chunk = ok[i:i + step]
            cells.append(pct([c.get("elapsed_ms", 0) for c in chunk], 50))
        print("   %-28s %s" % (h, " ".join("%5d" % c for c in cells[:10])))

    # ── 退避事件 ─────────────────────────────────────────────────
    bo = [e for e in events if e.get("event") == "backoff"]
    if bo:
        print("\n【退避事件】共 %d 次" % len(bo))
        by_h = defaultdict(list)
        for e in bo:
            by_h[e.get("host")].append(e.get("backoff_s", 0))
        for h, v in by_h.items():
            print("   %-28s %3d 次, 最大退避 %.0fs" % (h, len(v), max(v)))

    # ── 结论与建议 ───────────────────────────────────────────────
    print("\n" + "=" * 74)
    print("【建议】")
    for h, rs in sorted(by_host.items()):
        ok = [r for r in rs if r.get("http") == 200]
        rate = len(ok) * 100.0 / len(rs) if rs else 0
        gaps = [r["gap_before"] for r in rs if r.get("gap_before")]
        cur_gap = sum(gaps) / len(gaps) if gaps else 0
        if rate >= 99.5:
            sug = "可以提速：间隔 %.1fs → %.1fs 试试" % (cur_gap, max(1.0, cur_gap * 0.7))
        elif rate >= 97:
            sug = "维持 %.1fs，基本稳定" % cur_gap
        else:
            sug = "太快了：间隔 %.1fs → %.1fs" % (cur_gap, cur_gap * 1.8)
        print("   %-28s 成功率 %5.1f%%  %s" % (h, rate, sug))

    total_min = max((r.get("elapsed_total_s", 0) for r in reqs), default=0) / 60
    print("\n   本轮总耗时 %.1f 分钟，平均 %.2f 请求/秒"
          % (total_min, len(reqs) / max(1.0, total_min * 60)))
    print("=" * 74)


if __name__ == "__main__":
    main()
