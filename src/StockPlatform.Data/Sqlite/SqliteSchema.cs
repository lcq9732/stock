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

            -- 板块成分股的抓取进度（2026-09-03）。
            --
            -- 为什么需要单独记：成分股必须逐个板块去 push2 取（见 EastMoneyBoardFetcher 的类注释——
            -- 实测 datacenter 的 F10 报表会系统性漏股，液冷服务器 170 只里漏了 4 只，包括美的集团、
            -- 拓普集团这种链上有实际业务的大票），1031 个板块 ≈ 2500 个请求，而 push2 限流极敏感，
            -- **一轮跑不完是常态**。没有进度表就只能每次从头再来，永远抓不完。
            --
            -- status: ok=成功  empty=接口返回空(可能是已下架板块)  failed=取不到
            -- 只有 ok 会在下一轮被跳过；failed/empty 下轮继续重试。
            CREATE TABLE IF NOT EXISTS BoardMemberFetchState (
                board_code TEXT PRIMARY KEY,
                fetched_at TEXT,
                member_count INTEGER,
                status TEXT,
                message TEXT
            );

            -- 板块列表的**页级断点**（2026-09-04）。
            --
            -- 为什么需要它：push2 限流下，一轮往往抓到第 5 页就被拒。原来的做法是"这一类整轮作废、
            -- 下轮从第 1 页重来"——于是每轮都白烧 5 页配额、再在同一个地方被拒，**永远到不了第 6 页**。
            -- 记下"下次从第几页接着抓"，配额就不会浪费在已经拿到的那几页上。
            --
            -- run_started_at 是这一轮的开始时刻，同一轮抓到的板块 as_of 都写它。
            -- 等某一类凑齐了（条数＝接口自报的 total），才按它把"这一轮没出现过的板块"当已下架清掉——
            -- 清理和写入分开，半截列表就不会误删另外几个板块及其成分股。
            -- 板块列表的**暂存区**（2026-09-04）。抓到的每一页先落这儿，等这一类凑齐了
            -- （条数＝接口自报的 total），才在一个事务里整体搬进 Board 表。
            --
            -- 为什么要中间这一道：页级断点要能续，半截列表就得先存下来；可半截列表又绝不能进正表——
            -- Board 是快照语义，"这一轮没出现的板块＝已下架"会连成分股一起删掉，
            -- 而成分股是逐板块抓的、跨好几轮才攒得齐。
            -- 先前想过"写正表但推迟清理"，问题是正表会短暂地半新半旧（旧的 224 个新浪板块
            -- 和新抓的 500 个东财板块并存），那期间分析页查出来的东西是不一致的。
            -- 走暂存区就没这回事：正表要么是旧的完整快照、要么是新的完整快照。
            CREATE TABLE IF NOT EXISTS BoardStaging (
                board_type INTEGER NOT NULL,
                board_code TEXT NOT NULL,
                name TEXT,
                change_pct REAL,
                amount REAL,
                leader_code TEXT,
                leader_name TEXT,
                as_of TEXT,
                PRIMARY KEY (board_type, board_code)
            );

            CREATE TABLE IF NOT EXISTS BoardListFetchState (
                board_type INTEGER PRIMARY KEY,   -- 0=概念 1=行业
                run_started_at TEXT NOT NULL,     -- 这一轮的开始时刻（＝本轮板块的 as_of）
                next_page INTEGER NOT NULL,       -- 下次从第几页接着抓
                total INTEGER NOT NULL,           -- 接口自报的总数，用来判断凑齐没有
                fetched_count INTEGER NOT NULL,   -- 本轮已经落库多少个
                updated_at TEXT
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

            -- 财务数据的抓取状态（2026-08-27新增）。单独一张表而不是塞进 FinancialReport：
            -- GetLatestSnapshotByCode 是全取 metric_key 的，混一个假科目进去会进财务快照。
            -- keys_version 见 FinancialKeys.Version——增量判断要靠它识别"数据是旧版代码抓的、
            -- 科目不全"，只看 report_date 是不够的。
            CREATE TABLE IF NOT EXISTS FinancialFetchState (
                code TEXT PRIMARY KEY,      -- 6位股票代码
                keys_version INTEGER,       -- 抓这份数据时的 FinancialKeys.Version
                report_date TEXT,           -- 抓到的最新报告期（跟 FinancialReport 里的 MAX 一致，冗余但省一次聚合）
                fetched_at TEXT
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

            -- 定期报告预约披露日（2026-09-01）——交易所要求上市公司预约本期定期报告的披露日期。
            -- 用途：主动仓的"财报日"列、以及选股时避开"马上要出财报"的票（跨财报持仓是回测参数里
            -- 没有的事件风险）。数据源是巨潮（深沪京全覆盖），见 CninfoPrebookProvider。
            --
            -- 为什么四个日期都存、而不是只存一个"下次财报日"：
            --   ① 预约日**会改**，最多能改三次——实测沪市 2000 条样本里 12% 改过；
            --   ② 改的方向**不是只会延后**：提前 55%、延后 44%，最多提前 44 天、最多延后 62 天。
            --      提前那半边更危险（你按原日期盯，财报已经出了还不知道），所以不能只在临近时复查。
            --   ③ 变更轨迹本身有信息量：反复推迟披露往往不是好信号。
            --
            -- actual_date 为空 = 这期还没披露 = 需要每天复查；有值 = 这期结束了，
            -- 下一期的预约日要等交易所发布新一期预约表才会出现（见 RunFetchEarningsScheduleAsync）。
            CREATE TABLE IF NOT EXISTS EarningsSchedule (
                code TEXT NOT NULL,             -- 6位股票代码
                report_period TEXT NOT NULL,    -- 报告期，如 2026-06-30
                appoint_date TEXT,              -- 首次预约披露日
                change1 TEXT,                   -- 一次变更后的日期
                change2 TEXT,                   -- 二次变更
                change3 TEXT,                   -- 三次变更
                actual_date TEXT,               -- 实际披露日（空=还没披露）
                fetched_at TEXT,
                PRIMARY KEY (code, report_period)
            );

            -- 银行监管指标（2026-08-29）——不良率/拨备覆盖率/核心一级资本充足率/客户集中度等。
            -- 这些**不在三张报表里**，只在财报正文"会计数据和财务指标摘要"那两三页，所以单独
            -- 一张表、单独的抓取路径（下载 PDF + 解析），见 BankRegulatoryFetcher。
            --
            -- 为什么不并进 FinancialReport：① 数据源不同（PDF vs 新浪三表接口），抓取状态和重试
            -- 逻辑要独立，混进 FinancialFetchState 会互相干扰；② 那张表存的是金额（元），这里全是
            -- 比率（%），混存极易出单位 bug，而且比率的"同比"是百分点差不是百分比变化；
            -- ③ 只有 42 家银行有，塞进 5000+ 只股票的表里是噪音。
            --
            -- basis：口径。核心一级资本充足率同时披露"高级法"和"权重法"两个数（招行 2026H1 分别
            -- 是 14.07% 和 11.84%），只有六大行+招行等少数获批高级法。**横向比较必须用权重法**，
            -- 否则行业分位会被算歪。其它指标 basis 为空串（不能用 NULL，它是主键的一部分）。
            CREATE TABLE IF NOT EXISTS BankRegulatoryMetric (
                code TEXT NOT NULL,
                report_date TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                basis TEXT NOT NULL DEFAULT '',  -- '' | 'weighted'(权重法) | 'advanced'(高级法)
                value REAL,
                standard_value TEXT,             -- 监管标准值，报表自带（"≥25" / "≤10"）
                source_page INTEGER,             -- 取自 PDF 第几页，便于人工回查
                fetched_at TEXT,
                PRIMARY KEY (code, report_date, metric_key, basis)
            );

            -- 每份财报 PDF 的解析状态（2026-08-29）。**解析失败必须留痕**：某家银行改了版式、
            -- 或者是没有文本层的扫描件时，如果静默跳过，界面上"无数据"就分不清是【没抓】还是
            -- 【抓失败】——而这跟"这一项本来就取不到"是完全不同的三件事。
            CREATE TABLE IF NOT EXISTS BankReportFetchState (
                code TEXT NOT NULL,
                report_date TEXT NOT NULL,
                status TEXT,                     -- ok | no_pdf | no_text | no_match | error
                metric_count INTEGER,            -- 成功解析出几个指标
                message TEXT,                    -- 失败原因
                pdf_url TEXT,                    -- 来源 URL，PDF 缓存被清理后可重新下载
                pdf_path TEXT,                   -- 本地缓存路径（相对 data/reports）
                fetched_at TEXT,
                PRIMARY KEY (code, report_date)
            );

            -- 配股（2026-09-01新增）——A股第四类除权事件，前三类（现金分红/送股/转增）在 Dividend 表。
            -- 为什么不并进 Dividend：① 主键都是 (code, announce_date)，同一天既有分红方案又有配股方案
            -- 时会撞车；② 字段语义完全不同（配股要的是"配几股 + 每股掏多少钱"，分红是"送几股 + 收多少钱"）。
            -- 数据来源跟分红是**同一次请求**：新浪 vISSUE_ShareBonus 页里 sharebonus_1 是分红、
            -- sharebonus_2 是配股，一次 HTTP 拿两张表。
            -- 用途：day_adj 复权序列。漏掉配股会让除权日凭空多一根阴线——中信证券 2022-01 那次
            -- 10配1.5@14.43 理论跳空 −5.72%，10配3 的量级能到 −15%，且集中在银行/券商。
            CREATE TABLE IF NOT EXISTS RightsIssue (
                code TEXT NOT NULL,             -- 6位股票代码
                announce_date TEXT NOT NULL,    -- 公告日期（方案标识）
                shares_per_10 REAL,             -- 每10股配X股（10配3 = 3.0）
                price REAL,                     -- 配股价格（元/股）
                ex_date TEXT,                   -- 除权日（可空=未实施）。⚠ 缴款期常停牌，此日是复牌日，
                                                --   不等于"登记日+1"，见 RightsIssueRow.ExDate 注释
                record_date TEXT,               -- 股权登记日（可空）
                fetched_at TEXT,
                PRIMARY KEY (code, announce_date)
            );

            -- 全库体检确认过"数据源确实没有"的日线（2026-09-02）。
            --
            -- 为什么需要它：按股票体检时，停牌那几天在数据上跟"漏抓"长得一模一样——都是交易日历里有、
            -- 这只票没有。区分不了就只能每次体检都报一遍、每次都去抓一遍，永远收敛不了。
            -- 所以：体检报出来的缺口先去**抓一次**，连着两轮都拿不到才写进这张表，往后体检跳过它。
            -- 这是"抓过、确认拿不到"的结论，不是体检直接下的判断。
            --
            -- granularity 要记：前复权/后复权/不复权三条线各自独立，某只票可能只在其中一条缺。
            -- tries/confirmed_at 留着是为了有回头路——数据源当时抽风、后来补上了的话，
            -- 【全库数据体检】的「彻底体检」模式会忽略这张表重查一遍。
            CREATE TABLE IF NOT EXISTS MissingBarConfirmed (
                code TEXT NOT NULL,
                granularity TEXT NOT NULL,
                period_start TEXT NOT NULL,
                tries INTEGER NOT NULL DEFAULT 0,   -- 确认之前抓过几轮
                confirmed_at TEXT,                  -- 什么时候确认的
                PRIMARY KEY (code, granularity, period_start)
            );

            -- 业绩预告（2026-09-03，东财 RPT_PUBLIC_OP_NEWPREDICT）。本地此前完全没有这份数据。
            --
            -- 值钱在三点：① 比正式财报早一个月以上（Q3预告10月中 vs 财报10月底；年报预告1月底 vs 年报4月）；
            -- ② 强制披露规则正好对准剧变——净利变动超50%、扭亏、首亏都必须预告，"业绩剧变的公司"全在这张表里；
            -- ③ change_reason 是公司自述的变动原因，能从中提"涨价/供不应求/产能满负荷"这类词，
            --    是法定披露文件里公司自己写的，比研报转述硬。
            --
            -- 主键带 notice_date：同一报告期公司会修正预告，每次修正都是一行，要都留着（能看出修正方向）。
            -- is_latest 标记该报告期的最新一次。
            CREATE TABLE IF NOT EXISTS EarningsForecast (
                code TEXT NOT NULL,
                name TEXT,
                report_date TEXT NOT NULL,          -- 报告期 yyyy-MM-dd
                notice_date TEXT NOT NULL,          -- 公告日，增量按它切片
                predict_finance_code TEXT NOT NULL, -- 004=归母净利润
                predict_finance TEXT,
                amount_lower REAL,                  -- 预测区间（元）
                amount_upper REAL,
                amplitude_lower REAL,               -- 同比增幅区间（%）
                amplitude_upper REAL,
                predict_type TEXT,                  -- 预增/预减/扭亏/首亏/续亏…
                content TEXT,
                change_reason TEXT,                 -- 公司自述的变动原因
                preyear_same_period REAL,
                is_latest INTEGER,
                fetched_at TEXT,
                PRIMARY KEY (code, report_date, notice_date, predict_finance_code)
            );

            -- 业绩快报（2026-09-03，东财 RPT_FCI_PERFORMANCEE）。介于预告和正式财报之间：
            -- 比预告详细（确切数字而非区间，带营收/ROE/每股净资产），比财报早。非强制披露，覆盖面不如预告。
            CREATE TABLE IF NOT EXISTS EarningsExpress (
                code TEXT NOT NULL,
                name TEXT,
                report_date TEXT NOT NULL,
                notice_date TEXT,
                update_date TEXT,                   -- 增量按它切片（快报会修正，notice_date 有时为空）
                eps REAL,
                revenue REAL,
                revenue_yoy REAL,                   -- %
                np_parent REAL,
                np_yoy REAL,                        -- %
                bvps REAL,
                roe REAL,                           -- 加权平均ROE %
                revenue_qoq REAL,
                np_qoq REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, report_date)
            );

            -- 龙虎榜营业部席位明细（2026-09-03，东财 RPT_BILLBOARD_DAILYDETAILSBUY/SELL）。
            --
            -- 跟现有的 Lhb 表是**不同粒度**，不是替换：Lhb（新浪）只有"某天某股上榜了、原因、成交额"，
            -- 没有买卖前五营业部名单——而龙虎榜的全部价值恰恰在于看**是谁在买**（机构专用席位/知名游资/
            -- 深股通）。17 万行数据缺了这块等于只留了个壳。
            --
            -- seat_code 是营业部代码，有它才能跨时间追踪同一席位、自建游资库、算某个席位的历史胜率。
            --
            -- 主键为什么长成这样（trade_date, code, is_buy, explanation, seq）：
            --   · explanation —— 同一股同一天可能因多个原因分别上榜（"振幅30%"和"换手率20%"各一张榜）；
            --   · seq —— 机构席位是**匿名**的，seat_code 一律 "0"、名称一律"机构专用"，但同一张榜里
            --     可能有 2~3 个不同机构；"深股通投资者/机构投资者/自然人/中小投资者"这类类别统计也
            --     共用同一个营业部代码。不带 seq 的话这些行互相覆盖——实测一天 415 行会丢 31 行(7.5%)，
            --     全量 264 万行就是丢十几万。seq 按净额降序算（不是按接口返回顺序），保证重抓幂等。
            CREATE TABLE IF NOT EXISTS LhbSeat (
                trade_date TEXT NOT NULL,
                code TEXT NOT NULL,
                name TEXT,
                is_buy INTEGER NOT NULL,        -- 1=买方榜 0=卖方榜
                seat_code TEXT NOT NULL,        -- 营业部代码
                seat_name TEXT,
                buy REAL, sell REAL, net REAL,  -- 该席位当日买入/卖出/净额（元）
                explanation TEXT NOT NULL,      -- 上榜原因，进主键（同股同日可能多张榜）
                rise_prob_3day REAL,            -- 该营业部近期上榜后3日上涨概率（%），东财算好的
                times_3day INTEGER,
                trade_id TEXT,
                seq INTEGER NOT NULL,           -- 同一张榜里按净额降序的位次，进主键
                close_price REAL,
                change_rate REAL,
                fetched_at TEXT,
                PRIMARY KEY (trade_date, code, is_buy, explanation, seq)
            );

            -- 分档资金流（2026-09-03，东财 push2his）。
            --
            -- 跟 NetInflow 是**同一件事的不同精度**、不是新东西：那张表 1077 万行但每行只有一个
            -- main_net_inflow（主力净额合计）；这张把它拆成超大单/大单/中单/小单各自的净额+净占比。
            -- 判断资金性质要看结构不看合计——同样"主力净流入1亿"，超大单进、小单出（机构建仓）
            -- 跟大单进、超大单出（游资接力）含义完全相反，合计数把这个信息抹平了。
            --
            -- ⚠ 接口只给最近约 120 个交易日（lmt=0 也突破不了），历史深度只能靠定期抓取滚动累积。
            CREATE TABLE IF NOT EXISTS NetInflowDetail (
                code TEXT NOT NULL,
                trade_date TEXT NOT NULL,
                main_net REAL, super_net REAL, big_net REAL, mid_net REAL, small_net REAL,
                main_ratio REAL, super_ratio REAL, big_ratio REAL, mid_ratio REAL, small_ratio REAL,
                close_price REAL,
                change_rate REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, trade_date)
            );

            -- ─── 市场事件四表（2026-09-03，东财 datacenter）。本地此前全都没有 ───
            --
            -- 主键都带了"同一天可能有多条"的区分列。这是从龙虎榜席位表那次事故来的教训：
            -- 主键少一列，重复行会静默互相覆盖，一天 415 行丢 31 行都不会报错。所以这几张表的
            -- 写入路径都带**批内主键去重自检**（见 SqliteMarketEventRepository.Upsert*），
            -- 一批数据里出现主键重复会直接告警，而不是等到分析结果不对了才去查。

            -- 大宗交易。值钱的是买卖双方营业部和折溢价率：大幅折价通常是股东减持套现，
            -- 溢价接盘可能是产业资本。跟龙虎榜席位是互补的两块筹码信息。
            CREATE TABLE IF NOT EXISTS BlockTrade (
                trade_date TEXT NOT NULL,
                code TEXT NOT NULL,
                daily_rank INTEGER NOT NULL,    -- 当日第几笔，同股同日可多笔
                name TEXT,
                deal_price REAL, deal_volume REAL, deal_amount REAL,
                premium_ratio REAL,             -- 折溢价率(%)，负=折价
                close_price REAL, change_rate REAL, turnover_rate REAL,
                buyer_name TEXT, seller_name TEXT,
                fetched_at TEXT,
                PRIMARY KEY (trade_date, code, daily_rank)
            );

            -- 机构调研。一条记录=一家机构参与一次调研，所以主键要带**参与机构名**。
            -- ⚠ org_name 存的是接口的 RECEIVE_OBJECT（参与调研的机构），**不是** ORG_NAME
            --   ——后者是被调研的上市公司全名、同一次调研每行都一样，拿它做主键实测 95% 撞车。
            -- 用途是看资金关注度迁移（突然被几十家机构集中调研常早于股价异动），属软信号。
            CREATE TABLE IF NOT EXISTS OrgSurvey (
                code TEXT NOT NULL,
                notice_date TEXT NOT NULL,
                org_name TEXT NOT NULL,
                receive_start_date TEXT NOT NULL,   -- 同一机构可能在不同日期各调研一次
                survey_no INTEGER NOT NULL,     -- 接口 NUM：同机构同日参与同一公司的多次调研靠它区分
                name TEXT,
                receive_end_date TEXT,
                object_code TEXT,               -- 参与机构代码，可能为空所以不进主键
                org_type TEXT,
                receive_way TEXT,
                receive_place TEXT,
                investigators TEXT,             -- 机构侧参与人员
                receptionist TEXT,              -- 公司接待人员
                org_total INTEGER,              -- 接口 SUM：本次调研的机构总家数
                fetched_at TEXT,
                PRIMARY KEY (code, notice_date, org_name, receive_start_date, survey_no)
            );

            -- 限售解禁。⚠ **含未来的解禁计划**（实测样例里有 2035 年的），所以它是"日程表"
            -- 而不是"历史事件表"——不能按"抓到今天为止"做增量，每次全量重取（3 万行、63 页）。
            CREATE TABLE IF NOT EXISTS ShareLift (
                code TEXT NOT NULL,
                free_date TEXT NOT NULL,        -- 解禁日，可能在未来
                share_type TEXT NOT NULL,       -- 首发原股东限售股份/定向增发机构配售股份…同日可多类
                name TEXT,
                lift_shares REAL,               -- 解禁股数(万股)
                lift_market_cap REAL,           -- 解禁市值(万元)
                free_ratio REAL,                -- 占流通股比例(%)
                total_ratio REAL,               -- 占总股本比例(%)
                holder_count INTEGER,
                fetched_at TEXT,
                PRIMARY KEY (code, free_date, share_type)
            );

            -- 股东增减持。跟 TopShareholder（季度快照）互补：那张说"季末谁持有多少"，
            -- 这张说"期间谁在买卖、多少、什么价"。重要股东增减持是明确的内部人信号。
            CREATE TABLE IF NOT EXISTS HolderChange (
                code TEXT NOT NULL,
                notice_date TEXT NOT NULL,
                holder_name TEXT NOT NULL,
                end_date TEXT NOT NULL,         -- 变动区间截止日，同一股东可多次公告
                name TEXT,
                direction TEXT,                 -- 增持/减持
                change_shares REAL,             -- 变动股数(万股)，带符号
                change_ratio REAL,
                after_shares REAL, after_ratio REAL,
                start_date TEXT,
                average_price REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, notice_date, holder_name, end_date)
            );

            -- 个股的东财行业归属（2026-09-03）。**三级行业分类**，用来补证监会那套的不足。
            --
            -- 为什么需要：现有 StockIndustry 是证监会分类，33 门类 + 84 大类，但实测
            -- **1867 只（32.5%）大类为空只能退回门类**，而"制造业"一个门类就装了 3596 只（占 62%）。
            -- 拿它做行业中性化等于没中性化——FactorLab 的中性 IC 一直不准，根子就在这。
            -- 东财是三级分类（一级31/二级128/三级337），最大的三级行业也才 627 只。
            --
            -- 数据源是 datacenter 的 RPT_F10_CORETHEME_BOARDTYPE（**不走 push2**，那个要人工过验证）。
            -- 行业归属是"个股属性"场景，正是这张报表的用途；成分股场景才必须用 push2 的官方名单
            -- （见 EastMoneyBoardFetcher 类注释里那个漏股实测）。
            --
            -- 一只股票会有多行（三级各一行），board_level 区分。查最细行业取 MAX(board_level)。
            CREATE TABLE IF NOT EXISTS StockIndustryEm (
                code TEXT NOT NULL,
                board_code TEXT NOT NULL,       -- BKxxxx
                board_name TEXT,
                board_level INTEGER,            -- 1/2/3，越大越细
                fetched_at TEXT,
                PRIMARY KEY (code, board_code)
            );

            -- 个股的东财概念/题材归属（2026-09-03）。跟 BoardMember 是**不同用途**：
            -- 那张是"板块→成分股"的官方名单（判断板块景气度用），这张是"个股→题材"并且带
            -- **入选理由**——公司为什么被归到这个题材（取自互动易回复、公告原文等）。
            --
            -- reason 和 is_precise 合起来能分辨**实质业务 vs 蹭概念**：一个板块里如果一半成分股的
            -- 入选理由是"公司互动易回复称关注该领域"，那这个板块的成分质量就说明了问题。
            -- 这是判断真假风口时唯一能自动化的"含金量"判据。
            CREATE TABLE IF NOT EXISTS StockThemeEm (
                code TEXT NOT NULL,
                board_code TEXT NOT NULL,
                board_name TEXT,
                is_precise INTEGER,             -- 1=精确匹配 0=非精确（边缘关联）
                board_rank INTEGER,             -- 该股在此题材中的排位
                reason TEXT,                    -- 入选理由原文，可空
                fetched_at TEXT,
                PRIMARY KEY (code, board_code)
            );

            -- 反查热点列索引（2026-07-16）：这两条反查用的不是主键最左前缀，无索引会全表扫。
            CREATE INDEX IF NOT EXISTS ix_indexcons_stock ON IndexCons(stock_code);
            CREATE INDEX IF NOT EXISTS ix_etfindexmap_index ON EtfIndexMap(index_code);
            -- 按报告期/公告日横切（"这一期谁预告了""最近一周谁发了预告"）不是主键最左前缀，要单独建。
            CREATE INDEX IF NOT EXISTS ix_forecast_report ON EarningsForecast(report_date);
            CREATE INDEX IF NOT EXISTS ix_forecast_notice ON EarningsForecast(notice_date);
            CREATE INDEX IF NOT EXISTS ix_express_report ON EarningsExpress(report_date);
            -- 按营业部反查（"这个游资最近买了什么""这个席位的历史胜率"）是这张表的主要用法之一，
            -- 但 seat_code 不是主键最左前缀，没索引会扫 264 万行。
            CREATE INDEX IF NOT EXISTS ix_lhbseat_seat ON LhbSeat(seat_code, trade_date);
            CREATE INDEX IF NOT EXISTS ix_lhbseat_code ON LhbSeat(code, trade_date);
            -- 按日横切（"这一天全市场谁被超大单买了"）不是主键最左前缀。
            CREATE INDEX IF NOT EXISTS ix_flowdetail_date ON NetInflowDetail(trade_date);
            -- 市场事件四表：按股票反查是主要用法，但除 ShareLift/HolderChange 外 code 都不是主键最左前缀。
            CREATE INDEX IF NOT EXISTS ix_blocktrade_code ON BlockTrade(code, trade_date);
            CREATE INDEX IF NOT EXISTS ix_orgsurvey_notice ON OrgSurvey(notice_date);
            CREATE INDEX IF NOT EXISTS ix_sharelift_date ON ShareLift(free_date);
            CREATE INDEX IF NOT EXISTS ix_holderchange_notice ON HolderChange(notice_date);
            -- 按行业/题材反查成分（"这个三级行业里有哪些股"）不是主键最左前缀。
            CREATE INDEX IF NOT EXISTS ix_industryem_board ON StockIndustryEm(board_code);
            CREATE INDEX IF NOT EXISTS ix_themeem_board ON StockThemeEm(board_code);
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
        // 2026-08-29：监管指标区分来源——pdf / ocr / ocr_confirmed / manual，取值和含义见
        // Logic.Models.MetricSources。有它才能保证**重解析不会覆盖掉人拍板过的数据**
        // （见 SqliteBankRegulatoryRepository.Upsert 的 ON CONFLICT … WHERE），
        // 也才能把"这个数是 OCR 认的、还没核对"如实告诉看的人。老行为 NULL，按 'pdf' 处理。
        AddColumnIfMissing(conn, "BankRegulatoryMetric", "source", "TEXT");
    }

    /// <summary>
    /// 大表的二级索引（2026-08-29 新增）——**故意不放进 <see cref="EnsureSchema"/>**，由
    /// Fetcher 的【优化数据库】按钮显式触发，见 <see cref="SqliteMaintenance"/>。
    ///
    /// 为什么需要：这几张表的主键分别是 (code, granularity, period_start) / (code, report_date,
    /// metric_key) / (trade_date, code)，**按 code 查走最左前缀很快，按日期/报告期查只能全表扫**。
    /// 7.5GB 库上实测：单只股票日线 54ms，而"按日期取全市场收盘"要 25.2 秒、"按报告期取 42 家
    /// 银行财务"要 6.2 秒（EXPLAIN 都是 SCAN）。所有跨股票的批量比较——因子、选股扫描、板块热度、
    /// 行业分位——吃的都是这个亏。
    ///
    /// 为什么不放进 EnsureSchema：那个方法被 10 处抓取入口调用，而首次在 7.5GB 库上建 Bar 索引
    /// 要数分钟且期间阻塞写入。混进日常抓取路径 = 某天早上"拉取当天"莫名卡住十分钟。做成独立
    /// 按钮，用户自己挑空闲时间跑一次；建成后是持久对象，之后的写入由 SQLite 自动维护。
    ///
    /// 代价：索引本身占空间（Bar 那条约 600MB~1GB）。换来的是上面两个查询降到毫秒级。
    /// </summary>
    public static readonly (string Name, string Table, string Sql)[] BigTableIndexes =
    [
        ("ix_bar_gran_date", "Bar",
            "CREATE INDEX IF NOT EXISTS ix_bar_gran_date ON Bar(granularity, period_start);"),
        ("ix_fr_date_key", "FinancialReport",
            "CREATE INDEX IF NOT EXISTS ix_fr_date_key ON FinancialReport(report_date, metric_key);"),
        ("ix_margin_date", "MarginDetail",
            "CREATE INDEX IF NOT EXISTS ix_margin_date ON MarginDetail(trade_date);"),
    ];

    /// <summary>返回 <see cref="BigTableIndexes"/> 里当前库还没建的那些。</summary>
    public static List<string> GetMissingIndexes(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
            using var r = q.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }
        return BigTableIndexes.Where(i => !existing.Contains(i.Name)).Select(i => i.Name).ToList();
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
