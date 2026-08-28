"""抓完财务数据后跑一次，确认 52 个科目真的进库了。

用法:  python tools/check-financial-keys.py [股票代码]
"""
import sqlite3, os, re, sys, io

CODE = sys.argv[1] if len(sys.argv) > 1 else '603501'
BS = chr(92)
DB = os.path.join('publish', 'data', 'local', 'current.sqlite')
SRC = os.path.join('src', 'StockPlatform.Logic', 'Models', 'FinancialValue.cs')

# 从代码里读出应该有哪些 key（不手抄，代码是唯一事实来源）
body = io.open(SRC, encoding='utf-8').read().split('public static class FinancialKeys', 1)[1]
expected = re.findall(r'public const string (\w+) = "([^"]+)";', body.split('\n}', 1)[0])
print('代码里定义了 %d 个科目' % len(expected))

uri = 'file:' + os.path.abspath(DB).replace(BS, '/') + '?mode=ro'
con = sqlite3.connect(uri, uri=True, timeout=15)
cur = con.cursor()

# 全市场层面：每个 key 覆盖多少只票
cur.execute("SELECT metric_key, COUNT(DISTINCT code) FROM FinancialReport GROUP BY metric_key")
coverage = dict(cur.fetchall())
cur.execute("SELECT COUNT(DISTINCT code) FROM FinancialReport")
total_codes = cur.fetchone()[0]
cur.execute("SELECT MAX(report_date) FROM FinancialReport")
latest_period = cur.fetchone()[0]

print('库里有财务数据的股票 %d 只，最新报告期 %s' % (total_codes, latest_period))
print()

# 单只票：最新一期每个 key 的值
cur.execute("SELECT MAX(report_date) FROM FinancialReport WHERE code=?", (CODE,))
row = cur.fetchone()
latest = row[0] if row else None
vals = {}
if latest:
    cur.execute("SELECT metric_key, value FROM FinancialReport WHERE code=? AND report_date=?",
                (CODE, latest))
    vals = dict(cur.fetchall())
con.close()

print('%-24s %-20s %9s   %s 的 %s' % ('常量名', 'key', '覆盖股票数', CODE, latest or '(无)'))
print('-' * 84)
missing_db, missing_code = [], []
for name, key in expected:
    n = coverage.get(key, 0)
    if n == 0:
        missing_db.append((name, key))
    v = vals.get(key)
    if v is None and latest:
        missing_code.append((name, key))
    if key == 'eps_basic':
        shown = '%.4f 元/股' % v if v is not None else '—'
    elif key == 'share_capital':
        shown = '%.4f 亿股' % (v / 1e8) if v is not None else '—'
    else:
        shown = '%+.2f 亿' % (v / 1e8) if v is not None else '—'
    mark = '' if n > 0 else '   <<< 全市场一条都没有'
    print('%-24s %-20s %9d   %s%s' % (name, key, n, shown, mark))

print()
if missing_db:
    print('X 有 %d 个科目在库里一条都没有 —— 说明跑的还是旧版 Fetcher，或抓取没完成：' % len(missing_db))
    for name, key in missing_db:
        print('     %s (%s)' % (name, key))
    sys.exit(2)
print('OK 全部 %d 个科目都已入库' % len(expected))
if missing_code:
    print('注：%s 最新一期缺 %d 个科目（个别公司确实没有这些行，属正常）' % (CODE, len(missing_code)))
