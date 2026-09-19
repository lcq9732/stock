using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>公司档案的本地存取（2026-09-08）。见 <see cref="ICompanyProfileRepository"/>。</summary>
public class SqliteCompanyProfileRepository : ICompanyProfileRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteCompanyProfileRepository(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath}";

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

    public int Upsert(IEnumerable<(CompanyProfile Profile, CompanyNarrative Narrative)> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;      // 空集合是空操作，别把档案清了

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 两张表在**同一个事务**里写：一张写了另一张漏就会留下半拉记录
        using (var p = conn.CreateCommand())
        using (var n = conn.CreateCommand())
        {
            p.Transaction = tx;
            n.Transaction = tx;
            p.CommandText = """
                INSERT INTO CompanyProfile
                    (code, full_name, abbr, name_en, org_form, found_date, listing_date, listing_state,
                     reg_capital_wan, reg_capital, currency, province, city, district,
                     reg_address, address, postcode, industry_csrc, emp_num,
                     legal_person, actual_holder, final_holder, holder_name, holder_ratio,
                     chairman, president, secretary, publish_person,
                     secretary_tel, org_tel, org_fax, org_email, org_web,
                     reg_num, law_firm, accountfirm, cpa, ah_change, main_business,
                     org_code_em, report_date, fetched_at)
                VALUES ($code,$full,$abbr,$en,$form,$found,$listing,$lstate,
                        $capw,$cap,$cur,$prov,$city,$dist,
                        $regaddr,$addr,$post,$ind,$emp,
                        $legal,$actual,$final,$hname,$hratio,
                        $chair,$pres,$sec,$pub,
                        $sectel,$tel,$fax,$email,$web,
                        $regnum,$law,$acct,$cpa,$ah,$main,
                        $orgcode,$rdate,$at)
                ON CONFLICT(code) DO UPDATE SET
                    full_name=excluded.full_name, abbr=excluded.abbr, name_en=excluded.name_en,
                    org_form=excluded.org_form, found_date=excluded.found_date,
                    listing_date=excluded.listing_date, listing_state=excluded.listing_state,
                    reg_capital_wan=excluded.reg_capital_wan, reg_capital=excluded.reg_capital,
                    currency=excluded.currency, province=excluded.province, city=excluded.city,
                    district=excluded.district, reg_address=excluded.reg_address,
                    address=excluded.address, postcode=excluded.postcode,
                    industry_csrc=excluded.industry_csrc, emp_num=excluded.emp_num,
                    legal_person=excluded.legal_person, actual_holder=excluded.actual_holder,
                    final_holder=excluded.final_holder, holder_name=excluded.holder_name,
                    holder_ratio=excluded.holder_ratio, chairman=excluded.chairman,
                    president=excluded.president, secretary=excluded.secretary,
                    publish_person=excluded.publish_person, secretary_tel=excluded.secretary_tel,
                    org_tel=excluded.org_tel, org_fax=excluded.org_fax, org_email=excluded.org_email,
                    org_web=excluded.org_web, reg_num=excluded.reg_num, law_firm=excluded.law_firm,
                    accountfirm=excluded.accountfirm, cpa=excluded.cpa, ah_change=excluded.ah_change,
                    main_business=excluded.main_business, org_code_em=excluded.org_code_em,
                    report_date=excluded.report_date, fetched_at=excluded.fetched_at;
                """;
            n.CommandText = """
                INSERT INTO CompanyNarrative
                    (code, org_profile, org_evolution, business_scope, business_review, fetched_at)
                VALUES ($code,$prof,$evo,$scope,$review,$at)
                ON CONFLICT(code) DO UPDATE SET
                    org_profile=excluded.org_profile, org_evolution=excluded.org_evolution,
                    business_scope=excluded.business_scope, business_review=excluded.business_review,
                    fetched_at=excluded.fetched_at;
                """;

            SqliteParameter Add(SqliteCommand c, string name)
            { var q = c.CreateParameter(); q.ParameterName = name; c.Parameters.Add(q); return q; }

            var pp = new[] { "$code","$full","$abbr","$en","$form","$found","$listing","$lstate",
                             "$capw","$cap","$cur","$prov","$city","$dist","$regaddr","$addr","$post",
                             "$ind","$emp","$legal","$actual","$final","$hname","$hratio","$chair",
                             "$pres","$sec","$pub","$sectel","$tel","$fax","$email","$web","$regnum",
                             "$law","$acct","$cpa","$ah","$main","$orgcode","$rdate","$at" }
                     .ToDictionary(x => x, x => Add(p, x));
            var np = new[] { "$code","$prof","$evo","$scope","$review","$at" }
                     .ToDictionary(x => x, x => Add(n, x));

            foreach (var (x, y) in list)
            {
                var at = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                object O(string? s) => (object?)s ?? DBNull.Value;
                object N(double? d) => (object?)d ?? DBNull.Value;

                pp["$code"].Value = x.Code;      pp["$full"].Value = O(x.FullName);
                pp["$abbr"].Value = O(x.Abbr);   pp["$en"].Value = O(x.NameEn);
                pp["$form"].Value = O(x.OrgForm); pp["$found"].Value = O(x.FoundDate);
                pp["$listing"].Value = O(x.ListingDate); pp["$lstate"].Value = O(x.ListingState);
                pp["$capw"].Value = N(x.RegCapitalWan);  pp["$cap"].Value = N(x.RegCapital);
                pp["$cur"].Value = O(x.Currency); pp["$prov"].Value = O(x.Province);
                pp["$city"].Value = O(x.City);   pp["$dist"].Value = O(x.District);
                pp["$regaddr"].Value = O(x.RegAddress); pp["$addr"].Value = O(x.Address);
                pp["$post"].Value = O(x.Postcode); pp["$ind"].Value = O(x.IndustryCsrc);
                pp["$emp"].Value = (object?)x.EmpNum ?? DBNull.Value;
                pp["$legal"].Value = O(x.LegalPerson); pp["$actual"].Value = O(x.ActualHolder);
                pp["$final"].Value = O(x.FinalHolder); pp["$hname"].Value = O(x.HolderName);
                pp["$hratio"].Value = N(x.HolderRatio); pp["$chair"].Value = O(x.Chairman);
                pp["$pres"].Value = O(x.President); pp["$sec"].Value = O(x.Secretary);
                pp["$pub"].Value = O(x.PublishPerson); pp["$sectel"].Value = O(x.SecretaryTel);
                pp["$tel"].Value = O(x.OrgTel); pp["$fax"].Value = O(x.OrgFax);
                pp["$email"].Value = O(x.OrgEmail); pp["$web"].Value = O(x.OrgWeb);
                pp["$regnum"].Value = O(x.RegNum); pp["$law"].Value = O(x.LawFirm);
                pp["$acct"].Value = O(x.AccountFirm); pp["$cpa"].Value = O(x.Cpa);
                pp["$ah"].Value = O(x.AhChange); pp["$main"].Value = O(x.MainBusiness);
                pp["$orgcode"].Value = O(x.OrgCodeEm); pp["$rdate"].Value = O(x.ReportDate);
                pp["$at"].Value = at;
                p.ExecuteNonQuery();

                np["$code"].Value = y.Code;      np["$prof"].Value = O(y.OrgProfile);
                np["$evo"].Value = O(y.OrgEvolution); np["$scope"].Value = O(y.BusinessScope);
                np["$review"].Value = O(y.BusinessReview);
                np["$at"].Value = y.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                n.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return list.Count;
    }

    public List<(string Code, string FullName, string? Abbr)> GetAllNames()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 只取三列：这张表会被匹配步骤全表读，别把长文本捎上（长文本本来就在另一张表）。
        // abbr 是证券简称（"宁德时代"），2026-09-15 加进来给简称精确档用。
        cmd.CommandText = "SELECT code, full_name, abbr FROM CompanyProfile WHERE full_name IS NOT NULL AND full_name <> '';";
        var list = new List<(string, string, string?)>(6000);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    public List<(string Code, string Name)> GetDelistedCodes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // ⚠ 只认 '2'。9（待上市/暂缓）和 10（换代码吸收合并）不是退市，见接口注释。
        cmd.CommandText = """
            SELECT code, COALESCE(NULLIF(abbr, ''), full_name, code)
            FROM CompanyProfile WHERE listing_state = '2';
            """;
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public (int Profiles, int Narratives) GetCounts()
    {
        using var conn = Open();
        int N(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            return Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        return (N("SELECT COUNT(*) FROM CompanyProfile"), N("SELECT COUNT(*) FROM CompanyNarrative"));
    }
}
