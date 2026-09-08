using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteBoardRepository : IBoardRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteBoardRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
    }

    /// <summary>
    /// 用一次抓取的结果整体替换本地板块数据（清空 Board/BoardMember 再写入）。
    ///
    /// <b>空集合是空操作</b>——这一条是有来由的：板块是快照，抓取失败时"库里留着上一次的数据"
    /// 才是对的行为，绝不能因为这一轮拿回来个空列表就把库清空。调用方（FetchBoardsCoreAsync）
    /// 那边还有一层同样意图的保护：某一类板块列表为空时整轮不写库，避免用半批数据覆盖。
    /// </summary>
    public void ReplaceAll(IEnumerable<Board> boards)
    {
        var list = boards.ToList();
        if (list.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Board; DELETE FROM BoardMember;";
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO Board (board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of)
                VALUES ($code, $type, $name, $count, $pct, $amount, $lcode, $lname, $asof);
                """;
            var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
            var pType = cmd.CreateParameter(); pType.ParameterName = "$type"; cmd.Parameters.Add(pType);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pCount = cmd.CreateParameter(); pCount.ParameterName = "$count"; cmd.Parameters.Add(pCount);
            var pPct = cmd.CreateParameter(); pPct.ParameterName = "$pct"; cmd.Parameters.Add(pPct);
            var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
            var pLCode = cmd.CreateParameter(); pLCode.ParameterName = "$lcode"; cmd.Parameters.Add(pLCode);
            var pLName = cmd.CreateParameter(); pLName.ParameterName = "$lname"; cmd.Parameters.Add(pLName);
            var pAsOf = cmd.CreateParameter(); pAsOf.ParameterName = "$asof"; cmd.Parameters.Add(pAsOf);

            foreach (var b in list)
            {
                pCode.Value = b.BoardCode;
                pType.Value = (int)b.Type;
                pName.Value = b.Name;
                pCount.Value = b.MemberCount;
                pPct.Value = b.ChangePct;
                pAmount.Value = b.Amount;
                pLCode.Value = b.LeaderCode;
                pLName.Value = b.LeaderName;
                pAsOf.Value = b.AsOf.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        using (var mcmd = conn.CreateCommand())
        {
            mcmd.Transaction = tx;
            mcmd.CommandText = "INSERT OR IGNORE INTO BoardMember (board_code, stock_code) VALUES ($bcode, $scode);";
            var pB = mcmd.CreateParameter(); pB.ParameterName = "$bcode"; mcmd.Parameters.Add(pB);
            var pS = mcmd.CreateParameter(); pS.ParameterName = "$scode"; mcmd.Parameters.Add(pS);
            foreach (var b in list)
                foreach (var s in b.MemberCodes)
                {
                    pB.Value = b.BoardCode;
                    pS.Value = s;
                    mcmd.ExecuteNonQuery();
                }
        }

        tx.Commit();
    }

    public List<Board> QueryBoards(BoardType? type = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of
            FROM Board
            WHERE ($type IS NULL OR board_type = $type)
            ORDER BY change_pct DESC;
            """;
        cmd.Parameters.AddWithValue("$type", type.HasValue ? (int)type.Value : (object)DBNull.Value);

        var result = new List<Board>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Board
            {
                BoardCode = reader.GetString(0),
                Type = (BoardType)reader.GetInt32(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MemberCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                ChangePct = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                Amount = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                LeaderCode = reader.IsDBNull(6) ? "" : reader.GetString(6),
                LeaderName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                AsOf = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(8), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    /// <summary>
    /// 只更新板块列表，不动 BoardMember（2026-09-03）。
    /// 成分股改成逐板块抓取后，列表刷新和成分股抓取是两条独立节奏——列表 10 个请求，
    /// 成分股 2500 个请求且常常跨多轮，用 ReplaceAll 会把前几轮抓到的成分股全清掉。
    /// </summary>
    /// <summary>
    /// 板块列表这一类的页级断点（2026-09-04）：下次从第几页接着抓。没有就是 null（该开新一轮了）。
    /// </summary>
    public (DateTime RunStartedAt, int NextPage, int Total, int FetchedCount)? GetListState(BoardType type)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_started_at, next_page, total, fetched_count
            FROM BoardListFetchState WHERE board_type = $t;
            """;
        cmd.Parameters.AddWithValue("$t", (int)type);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (!DateTime.TryParseExact(r.GetString(0), TimeFormat, CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var started)) return null;
        return (started, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
    }

    public void SaveListState(BoardType type, DateTime runStartedAt, int nextPage, int total, int fetchedCount)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BoardListFetchState (board_type, run_started_at, next_page, total, fetched_count, updated_at)
            VALUES ($t, $s, $p, $tot, $c, $u)
            ON CONFLICT(board_type) DO UPDATE SET
                run_started_at = excluded.run_started_at,
                next_page      = excluded.next_page,
                total          = excluded.total,
                fetched_count  = excluded.fetched_count,
                updated_at     = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$t", (int)type);
        cmd.Parameters.AddWithValue("$s", runStartedAt.ToString(TimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$p", nextPage);
        cmd.Parameters.AddWithValue("$tot", total);
        cmd.Parameters.AddWithValue("$c", fetchedCount);
        cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void ClearListState(BoardType type)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BoardListFetchState WHERE board_type = $t;";
        cmd.Parameters.AddWithValue("$t", (int)type);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把抓到的一页板块写进**暂存区**（2026-09-04）。正表一动不动。
    ///
    /// 页级断点要能续，半截列表就得先存下来；可半截列表绝不能进正表——Board 是快照语义，
    /// "这一轮没出现的板块＝已下架"会连成分股一起删掉，而成分股是逐板块抓的、跨好几轮才攒得齐。
    /// 所以中间隔一道暂存区：抓一页存一页，凑齐了再 <see cref="CommitStaged"/> 整体搬过去。
    /// </summary>
    public void StageBoards(IEnumerable<Board> boards)
    {
        var list = boards.ToList();
        if (list.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO BoardStaging (board_type, board_code, name, change_pct, amount, leader_code, leader_name, as_of)
            VALUES ($type, $code, $name, $pct, $amount, $lcode, $lname, $asof)
            ON CONFLICT(board_type, board_code) DO UPDATE SET
                name       = excluded.name,
                change_pct = excluded.change_pct,
                amount     = excluded.amount,
                leader_code= excluded.leader_code,
                leader_name= excluded.leader_name,
                as_of      = excluded.as_of;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$type", "$code", "$name", "$pct", "$amount", "$lcode", "$lname", "$asof" })
        { var par = cmd.CreateParameter(); par.ParameterName = n; cmd.Parameters.Add(par); p[n] = par; }

        foreach (var b in list)
        {
            p["$type"].Value = (int)b.Type;
            p["$code"].Value = b.BoardCode;
            p["$name"].Value = b.Name;
            p["$pct"].Value = b.ChangePct;
            p["$amount"].Value = b.Amount;
            p["$lcode"].Value = b.LeaderCode;
            p["$lname"].Value = b.LeaderName;
            p["$asof"].Value = b.AsOf.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>暂存区里这一类攒了多少个——跟接口自报的 total 比，判断凑齐没有。</summary>
    public int CountStaged(BoardType type)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM BoardStaging WHERE board_type = $t;";
        cmd.Parameters.AddWithValue("$t", (int)type);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// 把暂存区里这一类**整体搬进正表**（2026-09-04），一个事务里做完：
    /// 清掉这一类里本轮没出现过的板块（连同成分股）→ 写入 → 清空暂存区。
    ///
    /// ⚠ 只在确认凑齐了（<see cref="CountStaged"/> ＝接口自报的 total）之后调。
    ///   半截列表调它，另外那几个板块和它们跨轮攒的成分股就没了。
    /// ⚠ 清理限定在同一个 board_type 内：概念和行业是分开抓、分开凑齐的，
    ///   提交概念时不能把行业当成"没出现过"。
    /// </summary>
    /// <returns>搬过去多少个、清掉多少个僵尸。</returns>
    public (int Committed, int Pruned) CommitStaged(BoardType type)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int committed;
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT COUNT(*) FROM BoardStaging WHERE board_type = $t;";
            c.Parameters.AddWithValue("$t", (int)type);
            committed = Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        if (committed == 0) { tx.Rollback(); return (0, 0); }

        int pruned;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM BoardMember WHERE board_code IN (
                    SELECT board_code FROM Board WHERE board_type = $t
                      AND board_code NOT IN (SELECT board_code FROM BoardStaging WHERE board_type = $t));
                DELETE FROM BoardMemberFetchState WHERE board_code IN (
                    SELECT board_code FROM Board WHERE board_type = $t
                      AND board_code NOT IN (SELECT board_code FROM BoardStaging WHERE board_type = $t));
                DELETE FROM Board WHERE board_type = $t
                  AND board_code NOT IN (SELECT board_code FROM BoardStaging WHERE board_type = $t);
                """;
            del.Parameters.AddWithValue("$t", (int)type);
            pruned = del.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            // member_count 新插入的写 0——它由 ReplaceMembers 按实际抓到的成分股数写，
            // 板块榜给的数字只是参考。已存在的板块走 DO UPDATE，不碰这一列。
            ins.CommandText = """
                INSERT INTO Board (board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of)
                SELECT board_code, board_type, name, 0, change_pct, amount, leader_code, leader_name, as_of
                FROM BoardStaging WHERE board_type = $t
                ON CONFLICT(board_code) DO UPDATE SET
                    board_type = excluded.board_type,
                    name       = excluded.name,
                    change_pct = excluded.change_pct,
                    amount     = excluded.amount,
                    leader_code= excluded.leader_code,
                    leader_name= excluded.leader_name,
                    as_of      = excluded.as_of;
                """;
            ins.Parameters.AddWithValue("$t", (int)type);
            ins.ExecuteNonQuery();
        }

        using (var clr = conn.CreateCommand())
        {
            clr.Transaction = tx;
            clr.CommandText = "DELETE FROM BoardStaging WHERE board_type = $t;";
            clr.Parameters.AddWithValue("$t", (int)type);
            clr.ExecuteNonQuery();
        }

        tx.Commit();
        return (committed, pruned);
    }

    /// <summary>丢掉暂存区里这一类的内容——开新一轮之前调。</summary>
    public void ClearStaged(BoardType type)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BoardStaging WHERE board_type = $t;";
        cmd.Parameters.AddWithValue("$t", (int)type);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 写板块层级树（2026-09-07）。数据来自东财终端本地文件，见 EastMoneyTerminalHierarchyProvider。
    ///
    /// 是**整棵树的快照替换**：先把所有已有的 parent_code/board_level 抹掉，再按这批边重写。
    /// 不这么做的话，行业改版时被摘掉的父子关系会一直赖在库里。
    ///
    /// 三条保护：
    /// 1. edges 为空**直接返回**，不清空——文件读不到时该保留上一次的树，跟 ReplaceAll 的空集合同理；
    /// 2. Board 表里没有的板块代码只计数不报错（终端的板块名单跟我们抓的可能差几个）；
    /// 3. 全程一个事务，中途出错整棵树回到原样，不会留下"清了一半"的状态。
    /// </summary>
    /// <returns>(写进去几个板块, 库里没有的板块代码数, 清掉几行旧关系)。</returns>
    public (int Updated, int Unknown, int Cleared) UpdateHierarchy(IReadOnlyList<BoardHierarchyEdge> edges)
    {
        if (edges.Count == 0) return (0, 0, 0);

        // 边只带子板块的层级，但一级板块**从不作为子出现**，它的层级藏在别人的 ParentLevel 里。
        // 所以得把两头都收进来，否则 23 个一级行业的 board_level 会全是 NULL。
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            levels[e.BoardCode] = e.Level;
            parents[e.BoardCode] = e.ParentCode;
            if (!levels.ContainsKey(e.ParentCode)) levels[e.ParentCode] = e.ParentLevel;
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int cleared;
        using (var clr = conn.CreateCommand())
        {
            clr.Transaction = tx;
            clr.CommandText = """
                UPDATE Board SET parent_code = NULL, board_level = NULL
                WHERE parent_code IS NOT NULL OR board_level IS NOT NULL;
                """;
            cleared = clr.ExecuteNonQuery();
        }

        int updated = 0, unknown = 0;
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE Board SET parent_code = $p, board_level = $l WHERE board_code = $c;";
            var pp = upd.CreateParameter(); pp.ParameterName = "$p"; upd.Parameters.Add(pp);
            var pl = upd.CreateParameter(); pl.ParameterName = "$l"; upd.Parameters.Add(pl);
            var pc = upd.CreateParameter(); pc.ParameterName = "$c"; upd.Parameters.Add(pc);

            foreach (var (code, level) in levels)
            {
                pc.Value = code;
                pl.Value = level;
                // 一级板块没有父，写 NULL 而不是空串——查询时 parent_code IS NULL 才是"树根"的判据
                pp.Value = parents.TryGetValue(code, out var par) ? par : DBNull.Value;

                if (upd.ExecuteNonQuery() > 0) updated++;
                else unknown++;   // 终端认识、我们没抓到的板块，不算错
            }
        }

        tx.Commit();
        return (updated, unknown, cleared);
    }

    public void UpsertBoards(IEnumerable<Board> boards)
    {
        var list = boards.ToList();
        if (list.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 板块列表是快照：这一轮没返回的板块说明已下架，连同它的成分股一起清掉，
        // 否则库里会永远留着一堆查不到、也不会再更新的僵尸板块。
        //
        // ⚠ **删除只在同一个 board_type 内进行**（2026-09-04）。
        //    概念和行业是分两次抓、分两次写的（一类抓完就落库，免得第二类失败把第一类的成果
        //    也带走）。要是删除不按类型限定，写概念板块那一下就会把行业板块**全删光**——
        //    它们当然不在概念这批名单里。跨轮攒了好几天的成分股会跟着一起没，而且不报错。
        //
        //    换句话说：每一类各自是一份完整快照，互不干涉。调用方保证传进来的是**某一类的完整
        //    名单**（不完整的在 EastMoneyBoardFetcher 那边就按"半截列表"拒掉了，见那里的对账）。
        var types = list.Select(b => (int)b.Type).Distinct().ToList();
        if (types.Count != 1)
            throw new ArgumentException(
                $"UpsertBoards 一次只能写一个 board_type 的完整名单，这批混了 {types.Count} 种"
                + "（删除是按类型限定的，混着写会误删）。", nameof(boards));
        var boardType = types[0];

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            var codes = string.Join(",", list.Select(b => "'" + b.BoardCode.Replace("'", "''") + "'"));
            del.CommandText = $"""
                DELETE FROM BoardMember WHERE board_code IN (
                    SELECT board_code FROM Board
                    WHERE board_type = {boardType} AND board_code NOT IN ({codes}));
                DELETE FROM BoardMemberFetchState WHERE board_code IN (
                    SELECT board_code FROM Board
                    WHERE board_type = {boardType} AND board_code NOT IN ({codes}));
                DELETE FROM Board WHERE board_type = {boardType} AND board_code NOT IN ({codes});
                """;
            del.ExecuteNonQuery();
        }

        WriteBoards(conn, tx, list);
        tx.Commit();
    }

    /// <summary>把板块写进 Board 表（纯 upsert，不删任何东西）。调用方负责事务。</summary>
    private static void WriteBoards(SqliteConnection conn, SqliteTransaction tx, List<Board> list)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // member_count 不在这里更新——它由 ReplaceMembers 按实际抓到的成分股数写，
        // 板块榜给的数字只是参考，跟实际拿到的名单可能对不上。
        cmd.CommandText = """
            INSERT INTO Board (board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of)
            VALUES ($code, $type, $name, 0, $pct, $amount, $lcode, $lname, $asof)
            ON CONFLICT(board_code) DO UPDATE SET
                board_type = excluded.board_type,
                name       = excluded.name,
                change_pct = excluded.change_pct,
                amount     = excluded.amount,
                leader_code= excluded.leader_code,
                leader_name= excluded.leader_name,
                as_of      = excluded.as_of;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$code", "$type", "$name", "$pct", "$amount", "$lcode", "$lname", "$asof" })
        {
            var par = cmd.CreateParameter(); par.ParameterName = n; cmd.Parameters.Add(par); p[n] = par;
        }
        foreach (var b in list)
        {
            p["$code"].Value = b.BoardCode;
            p["$type"].Value = (int)b.Type;
            p["$name"].Value = b.Name;
            p["$pct"].Value = b.ChangePct;
            p["$amount"].Value = b.Amount;
            p["$lcode"].Value = b.LeaderCode;
            p["$lname"].Value = b.LeaderName;
            p["$asof"].Value = b.AsOf.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
    }

    public void ReplaceMembers(string boardCode, IReadOnlyList<string> stockCodes)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM BoardMember WHERE board_code = $b;";
            del.Parameters.AddWithValue("$b", boardCode);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO BoardMember (board_code, stock_code) VALUES ($b, $s);";
            var pb = ins.CreateParameter(); pb.ParameterName = "$b"; pb.Value = boardCode; ins.Parameters.Add(pb);
            var ps = ins.CreateParameter(); ps.ParameterName = "$s"; ins.Parameters.Add(ps);
            foreach (var s in stockCodes) { ps.Value = s; ins.ExecuteNonQuery(); }
        }
        using (var st = conn.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = """
                INSERT OR REPLACE INTO BoardMemberFetchState (board_code, fetched_at, member_count, status, message)
                VALUES ($b, $t, $n, $st, NULL);
                UPDATE Board SET member_count = $n WHERE board_code = $b;
                """;
            st.Parameters.AddWithValue("$b", boardCode);
            st.Parameters.AddWithValue("$t", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
            st.Parameters.AddWithValue("$n", stockCodes.Count);
            st.Parameters.AddWithValue("$st", stockCodes.Count > 0 ? "ok" : "empty");
            st.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void MarkMembersFailed(string boardCode, string status, string message)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO BoardMemberFetchState (board_code, fetched_at, member_count, status, message)
            VALUES ($b, $t, NULL, $st, $msg);
            """;
        cmd.Parameters.AddWithValue("$b", boardCode);
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$msg", message.Length > 300 ? message[..300] : message);
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> GetBoardsWithFreshMembers(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 只认 ok：empty 和 failed 下一轮都要重试
        cmd.CommandText = "SELECT board_code FROM BoardMemberFetchState WHERE status = 'ok' AND fetched_at >= $t;";
        cmd.Parameters.AddWithValue("$t", since.ToString(TimeFormat, CultureInfo.InvariantCulture));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public (int Ok, int Failed, int Never) GetMemberFetchProgress(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM BoardMemberFetchState WHERE status='ok' AND fetched_at >= $t),
              (SELECT COUNT(*) FROM BoardMemberFetchState WHERE status <> 'ok' OR fetched_at < $t),
              (SELECT COUNT(*) FROM Board WHERE board_code NOT IN (SELECT board_code FROM BoardMemberFetchState));
            """;
        cmd.Parameters.AddWithValue("$t", since.ToString(TimeFormat, CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : (0, 0, 0);
    }

    public void UpdateQuotes(IEnumerable<(string BoardCode, double ChangePct, double Amount)> quotes)
    {
        var list = quotes.ToList();
        if (list.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Board SET change_pct = $pct, amount = $amt WHERE board_code = $code;";
        var pPct = cmd.CreateParameter(); pPct.ParameterName = "$pct"; cmd.Parameters.Add(pPct);
        var pAmt = cmd.CreateParameter(); pAmt.ParameterName = "$amt"; cmd.Parameters.Add(pAmt);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        foreach (var (code, pct, amt) in list)
        {
            pPct.Value = pct;
            pAmt.Value = amt;
            pCode.Value = code;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<string> QueryMembers(string boardCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT stock_code FROM BoardMember WHERE board_code = $bcode;";
        cmd.Parameters.AddWithValue("$bcode", boardCode);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public Dictionary<string, List<string>> GetConceptBoardsByStock()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bm.stock_code, b.name
            FROM BoardMember bm
            JOIN Board b ON bm.board_code = b.board_code
            WHERE b.board_type = 0;
            """;
        var map = new Dictionary<string, List<string>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (name.Length == 0) continue;
            if (!map.TryGetValue(code, out var list)) { list = new List<string>(); map[code] = list; }
            list.Add(name);
        }
        return map;
    }

    public DateTime? GetLatestAsOf()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(as_of) FROM Board;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, TimeFormat, CultureInfo.InvariantCulture);
    }
}
