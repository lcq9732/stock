using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>DDL for the shared SQLite schema (see doc/data-platform-design.md section 4). Idempotent.</summary>
public static class SqliteSchema
{
    public static void EnsureSchema(SqliteConnection conn)
    {
        // WAL（2026-08-21）：分析程序不再拿"拷过来/下载来的副本"，而是直接读 Fetcher 正在写的
        // 那个库（见 AnalyzerPaths 类注释）。默认的 delete 模式下写事务要加排他锁、读方直接吃
        // SQLITE_BUSY，早上"一边抓一边看盘"必然报 database is locked；WAL 下读写互不阻塞。
        //
        // journal_mode 是**写进数据库文件头的持久属性**，设一次就永久生效，所以这里每次调用实际
        // 只有第一次起作用。但切换需要独占访问：若此刻另一个程序正开着这个库，这条 PRAGMA 不报错、
        // 只是返回旧模式，下次无人占用时再切——所以升级后第一次请单独开 Fetcher 跑一遍。
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }

        // 迁移：IndexWeight 2026-07-16 改为按 as_of_date 版本化（PK 加 as_of_date 保留历史各期权重）。
        // 旧表(PK不含 as_of_date)先删掉、由下面 CREATE 重建——权重数据可随时重拉，改造前基本为空。
        DropTableIfPkMismatch(conn, "IndexWeight", "as_of_date");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Bar (
                code TEXT NOT NULL,
                granularity TEXT NOT NULL,
                period_start TEXT NOT NULL,
                open REAL, close REAL, high REAL, low REAL,
                volume REAL, amount REAL, turnover REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, granularity, period_start)
            );

            CREATE TABLE IF NOT EXISTS FundamentalMetric (
                code TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                as_of_date TEXT NOT NULL,
                value REAL,
                source TEXT,
                fetched_at TEXT,
                PRIMARY KEY (code, metric_key, as_of_date)
            );

            CREATE TABLE IF NOT EXISTS NetInflow (
                code TEXT NOT NULL,
                period_start TEXT NOT NULL,
                main_net_inflow REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, period_start)
            );

            CREATE TABLE IF NOT EXISTS StockMeta (
                code TEXT PRIMARY KEY,
                name TEXT,
                exchange TEXT,
                list_date TEXT,
                last_updated TEXT
            );

            CREATE TABLE IF NOT EXISTS OrderWinAnnouncement (
                code TEXT NOT NULL,
                name TEXT,
                title TEXT NOT NULL,
                publish_date TEXT NOT NULL,
                keyword TEXT,
                art_code TEXT,
                pdf_url TEXT,
                content TEXT,
                total_amount_yuan REAL,
                source TEXT,
                fetched_at TEXT,
                PRIMARY KEY (code, title, publish_date)
            );

            CREATE TABLE IF NOT EXISTS Board (
                board_code TEXT PRIMARY KEY,
                board_type INTEGER NOT NULL,   -- 0=概念/题材, 1=行业
                name TEXT,
                member_count INTEGER,
                change_pct REAL,
                amount REAL,
                leader_code TEXT,
                leader_name TEXT,
                as_of TEXT
            );

            CREATE TABLE IF NOT EXISTS BoardMember (
                board_code TEXT NOT NULL,
                stock_code TEXT NOT NULL,
                PRIMARY KEY (board_code, stock_code)
            );

            CREATE TABLE IF NOT EXISTS IndexCons (
                index_code TEXT NOT NULL,   -- 6位指数代码
                stock_code TEXT NOT NULL,   -- 6位成分股（去前缀）
                in_date TEXT,               -- 纳入日期（新浪成分表第3列，仅供单向时点过滤）
                fetched_at TEXT,
                PRIMARY KEY (index_code, stock_code)
            );

            CREATE TABLE IF NOT EXISTS IndexWeight (
                index_code TEXT NOT NULL,
                stock_code TEXT NOT NULL,
                weight REAL,
                as_of_date TEXT NOT NULL,   -- 权重基准日（版本化：不同基准日各留一版，供回测按时点取）
                fetched_at TEXT,
                PRIMARY KEY (index_code, stock_code, as_of_date)
            );

            CREATE TABLE IF NOT EXISTS Lhb (
                trade_date TEXT NOT NULL,
                stock_code TEXT NOT NULL,
                stock_name TEXT,
                close_price REAL, deviation REAL, volume REAL, amount REAL,
                reason TEXT NOT NULL,       -- 上榜指标（同股同日可多条）
                fetched_at TEXT,
                PRIMARY KEY (trade_date, stock_code, reason)
            );

            CREATE TABLE IF NOT EXISTS EtfIndexMap (
                etf_code TEXT NOT NULL,     -- 带前缀8位（sh510300）
                index_code TEXT,           -- 匹配到的6位指数代码，可空
                match_type TEXT,           -- exact/contains/unmatched
                PRIMARY KEY (etf_code)
            );

            CREATE TABLE IF NOT EXISTS ShareholderCount (
                code TEXT NOT NULL,         -- 6位股票代码
                report_date TEXT NOT NULL,  -- 报告期
                holder_num INTEGER,         -- 股东户数
                avg_shares REAL,            -- 户均持股数
                fetched_at TEXT,
                PRIMARY KEY (code, report_date)
            );

            CREATE TABLE IF NOT EXISTS TopShareholder (
                code TEXT NOT NULL,         -- 6位股票代码
                report_date TEXT NOT NULL,
                kind TEXT NOT NULL,         -- total=十大股东, float=十大流通股东
                rank INTEGER NOT NULL,      -- 名次
                holder_name TEXT,
                shares REAL,                -- 持股数量
                ratio REAL,                 -- 占比(%)
                share_type TEXT,            -- 股本性质
                change_direction TEXT,      -- 相对上期的增减方向：增/减，无标记为NULL
                fetched_at TEXT,
                PRIMARY KEY (code, report_date, kind, rank)
            );

            CREATE TABLE IF NOT EXISTS DelistedStock (
                code TEXT PRIMARY KEY,      -- 6位A股代码
                name TEXT,                  -- 终止上市时的简称（如"乐视退"）
                exchange TEXT,              -- sse=上交所, szse=深交所
                list_date TEXT,
                delist_date TEXT,           -- 终止上市日（上交所转板/合并的行缺失为NULL）
                fetched_at TEXT,
                -- 已经尝试过补"最后几天"K线的时间（见 FetchOrchestrator.CatchUpDelistedTailsAsync）。
                -- 必须有这个标记：停牌后才退市的股票（K线止于停牌日、早于终止日）永远满足"本地最后一根
                -- 早于终止日"，没有标记就会每天徒劳重抓一次。成功尝试过即置位，之后永久跳过。
                tail_fetched_at TEXT
            );

            CREATE TABLE IF NOT EXISTS FinancialReport (
                code TEXT NOT NULL,         -- 6位股票代码
                report_date TEXT NOT NULL,  -- 报告期（季度末），不是公告日（数据源没有公告日）
                metric_key TEXT NOT NULL,   -- 规范化科目键，见 FinancialKeys
                value REAL,                 -- 单位：元；利润表/现金流为年内累计口径
                fetched_at TEXT,
                PRIMARY KEY (code, report_date, metric_key)
            );

            CREATE TABLE IF NOT EXISTS StockIndustry (
                code TEXT PRIMARY KEY,      -- 6位股票代码
                class_code TEXT,            -- 证监会门类代码 A~S（两所官网，覆盖沪深全部）
                class_name TEXT,            -- 门类名称，如"制造业"（太粗，仅作兜底）
                major_name TEXT,            -- 证监会大类名称，如"汽车制造业"（新浪，约覆盖58%）
                fetched_at TEXT
            );

            CREATE TABLE IF NOT EXISTS MarginDetail (
                trade_date TEXT NOT NULL,   -- 交易日
                code TEXT NOT NULL,         -- 6位标的代码
                name TEXT,
                margin_balance REAL,        -- 融资余额(元)
                margin_buy REAL,            -- 融资买入额(元)
                short_balance REAL,         -- 融券余额(元)
                short_volume REAL,          -- 融券余量(股/份)
                fetched_at TEXT,
                PRIMARY KEY (trade_date, code)
            );

            -- 分红送配（2026-07-31新增，新浪 vISSUE_ShareBonus 分红派息页，非东财）——每只股票历年每个
            -- 分红方案一行。数据库里原本没有任何分红明细：前复权已把分红效果揉进价格、反而看不出"哪天除权、
            -- 每股派多少"，且减法式前复权对高分红老股会算出负价（见 project_qfq_hfq），所以分红必须单独抓。
            -- 用途：股息率因子、除权除息日核对。金额均为"每10股"口径（数据源如此）。
            CREATE TABLE IF NOT EXISTS Dividend (
                code TEXT NOT NULL,             -- 6位股票代码
                announce_date TEXT NOT NULL,    -- 公告日期（方案标识，同股同日唯一）
                bonus_shares REAL,              -- 送股（每10股送X股）
                transfer_shares REAL,           -- 转增（每10股转增X股）
                dividend_yuan REAL,             -- 派息（税前，每10股派X元）→ 每股股息=X/10
                progress TEXT,                  -- 进度：实施/预案/董事会通过/不分配 等
                record_date TEXT,               -- 股权登记日（可空：方案未实施/进行中，源给 "--"）
                ex_date TEXT,                   -- 除权除息日（可空，同上）
                fetched_at TEXT,
                PRIMARY KEY (code, announce_date)
            );

            -- 反查热点列索引（2026-07-16）：这两条反查用的不是主键最左前缀，无索引会全表扫。
            CREATE INDEX IF NOT EXISTS ix_indexcons_stock ON IndexCons(stock_code);
            CREATE INDEX IF NOT EXISTS ix_etfindexmap_index ON EtfIndexMap(index_code);
            """;
        cmd.ExecuteNonQuery();

        // 老数据库文件（2026-07-09之前建的）已经有Bar/NetInflow表，上面CREATE TABLE IF NOT
        // EXISTS对已存在的表是空操作，不会补上新列——用ALTER TABLE显式迁移。加列前先检查是否已经
        // 存在（EnsureSchema要保持幂等可重复调用，且ALTER TABLE ADD COLUMN对已有同名列会直接报错）。
        AddColumnIfMissing(conn, "Bar", "fetched_at", "TEXT");
        AddColumnIfMissing(conn, "NetInflow", "fetched_at", "TEXT");
        // 2026-07-15：StockMeta 从"只装个股"扩成"装所有标的"——加 type 列区分 stock/index/etf/board。
        // 老行没有这一列，读出来是 NULL，一律按 'stock' 处理（见 SqliteStockMetaUpsert）。个股列表/各选股
        // 扫描仍只认个股，指数/ETF/板块只是为了能在"查询"页搜到、看行情，不进选股。
        AddColumnIfMissing(conn, "StockMeta", "type", "TEXT");

        // 2026-07-14 起不再存储涨跌幅——它是收盘价的派生值，全部改成读取时现算（存一份反而多一层
        // "每条写入路径都得同步更新"的负担，之前 pct_chg 常年为0正是这个坑，见 doc §9.5 的原则）。
        // 老库里已有的 pct_chg 列在这里删掉，保持"代码里没有、库里也没有"一致；DROP COLUMN 会重写
        // 整张表，856MB 的库首次执行需要几十秒，之后列已不在、此调用变成空操作。
        DropColumnIfExists(conn, "Bar", "pct_chg");

        // 2026-07-16：指数成分加"纳入日期"列（老库已有 IndexCons 表的补列）。
        AddColumnIfMissing(conn, "IndexCons", "in_date", "TEXT");
        // 2026-07-29：退市股"最后几天K线"的一次性补齐标记（见 DelistedStock 表注释）。
        AddColumnIfMissing(conn, "DelistedStock", "tail_fetched_at", "TEXT");
        // 2026-08-13：十大股东加"增减方向"列。新浪页面在持股数后面挂了个涨跌箭头（↑增↓减），
        // 以前解析时被当成脏字符导致整格失败、持股数静默变0（14953行受影响，见
        // SinaShareholderProvider.ParseD）；修复时把这个方向本身也存下来——机构调仓方向是有用信息。
        // 老库的历史行这一列为 NULL，等用户重新"拉取股东数据"时按 code 覆盖写入。
        AddColumnIfMissing(conn, "TopShareholder", "change_direction", "TEXT");
    }

    /// <summary>若 <paramref name="table"/> 已存在、但其建表 SQL 的主键里不含 <paramref name="pkColumn"/>，
    /// 就 DROP 掉（由随后的 CREATE 重建成新主键）。用于主键结构变更的迁移——仅对可无损重拉的表使用。</summary>
    private static void DropTableIfPkMismatch(SqliteConnection conn, string table, string pkColumn)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$t;";
        q.Parameters.AddWithValue("$t", table);
        if (q.ExecuteScalar() is not string sql) return;   // 表不存在

        int pkIdx = sql.IndexOf("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
        bool pkHasColumn = pkIdx >= 0 && sql.IndexOf(pkColumn, pkIdx, StringComparison.OrdinalIgnoreCase) >= 0;
        if (pkHasColumn) return;   // 已是新结构

        using var drop = conn.CreateCommand();
        drop.CommandText = $"DROP TABLE {table};";
        drop.ExecuteNonQuery();
    }

    private static void DropColumnIfExists(SqliteConnection conn, string table, string column)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        checkCmd.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(checkCmd.ExecuteScalar()) == 0) return;

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        alterCmd.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string columnType)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        checkCmd.Parameters.AddWithValue("$column", column);
        var exists = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;
        if (exists) return;

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType};";
        alterCmd.ExecuteNonQuery();
    }
}
