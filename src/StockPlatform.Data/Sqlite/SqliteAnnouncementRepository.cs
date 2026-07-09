using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteAnnouncementRepository : IAnnouncementRepository
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteAnnouncementRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<OrderWinAnnouncement> items)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO OrderWinAnnouncement
                (code, name, title, publish_date, keyword, art_code, pdf_url, content, total_amount_yuan, source, fetched_at)
            VALUES
                ($code, $name, $title, $publish_date, $keyword, $art_code, $pdf_url, $content, $total_amount_yuan, $source, $fetched_at);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pTitle = cmd.CreateParameter(); pTitle.ParameterName = "$title"; cmd.Parameters.Add(pTitle);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$publish_date"; cmd.Parameters.Add(pDate);
        var pKeyword = cmd.CreateParameter(); pKeyword.ParameterName = "$keyword"; cmd.Parameters.Add(pKeyword);
        var pArtCode = cmd.CreateParameter(); pArtCode.ParameterName = "$art_code"; cmd.Parameters.Add(pArtCode);
        var pPdfUrl = cmd.CreateParameter(); pPdfUrl.ParameterName = "$pdf_url"; cmd.Parameters.Add(pPdfUrl);
        var pContent = cmd.CreateParameter(); pContent.ParameterName = "$content"; cmd.Parameters.Add(pContent);
        var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$total_amount_yuan"; cmd.Parameters.Add(pAmount);
        var pSource = cmd.CreateParameter(); pSource.ParameterName = "$source"; cmd.Parameters.Add(pSource);
        var pFetchedAt = cmd.CreateParameter(); pFetchedAt.ParameterName = "$fetched_at"; cmd.Parameters.Add(pFetchedAt);

        foreach (var item in items)
        {
            pCode.Value = item.Code;
            pName.Value = (object?)item.Name ?? DBNull.Value;
            pTitle.Value = item.Title;
            pDate.Value = item.PublishDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pKeyword.Value = (object?)item.Keyword ?? DBNull.Value;
            pArtCode.Value = (object?)item.ArtCode ?? DBNull.Value;
            pPdfUrl.Value = (object?)item.PdfUrl ?? DBNull.Value;
            pContent.Value = (object?)item.Content ?? DBNull.Value;
            pAmount.Value = (object?)item.TotalAmountYuan ?? DBNull.Value;
            pSource.Value = (object?)item.Source ?? DBNull.Value;
            pFetchedAt.Value = item.FetchedAt.ToString(DateFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<OrderWinAnnouncement> Query(string? code = null, DateTime? start = null, DateTime? end = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, name, title, publish_date, keyword, art_code, pdf_url, content, total_amount_yuan, source, fetched_at
            FROM OrderWinAnnouncement
            WHERE ($code IS NULL OR code = $code)
              AND ($start IS NULL OR publish_date >= $start)
              AND ($end IS NULL OR publish_date <= $end)
            ORDER BY publish_date DESC;
            """;
        cmd.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", (object?)start?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)end?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);

        var result = new List<OrderWinAnnouncement>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new OrderWinAnnouncement
            {
                Code = reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Title = reader.GetString(2),
                PublishDate = DateTime.ParseExact(reader.GetString(3), DateFormat, CultureInfo.InvariantCulture),
                Keyword = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ArtCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                PdfUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                Content = reader.IsDBNull(7) ? null : reader.GetString(7),
                TotalAmountYuan = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                Source = reader.IsDBNull(9) ? "" : reader.GetString(9),
                FetchedAt = DateTime.ParseExact(reader.GetString(10), DateFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }
}
