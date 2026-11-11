using System.Data.SqlClient;
using System.Globalization;

namespace FintechEtl.FunctionApp.Alson;

internal sealed record AlsonSettings(string Sql, string Blob, string Container, string Prefix, string ExpectedDatabase,
    bool Enabled, bool DryRun, int MaxFiles, DateTime NotBefore, HashSet<string> Masters,
    bool EmailsEnabled, string EmailConnection, string[] EmailRecipients)
{
    internal static AlsonSettings Load()
    {
        string Get(string key, string fallback = null) => Environment.GetEnvironmentVariable(key) ?? fallback;
        bool enabled = bool.TryParse(Get("AlsonEtlEnabled", "false"), out var on) && on;
        bool dry = !bool.TryParse(Get("AlsonEtlDryRun", "true"), out var d) || d;
        int max = int.TryParse(Get("AlsonMaxFilesPerRun", "100"), out var n) && n is > 0 and <= 1000 ? n :
            throw new InvalidOperationException("AlsonMaxFilesPerRun must be 1..1000.");
        var sql = Get("AlsonSqlConnectionString"); var expected = Get("AlsonExpectedDatabase", "FintechStagDB_NewArchitecture");
        var masters = (Get("AlsonAllowedMasterIds", "") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var date = Get("AlsonSourceNotBefore");
        var parsed = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from);
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(sql) || new SqlConnectionStringBuilder(sql).InitialCatalog != expected || expected != "FintechStagDB_NewArchitecture")
                throw new InvalidOperationException("Alson requires its dedicated staging SQL connection and exact expected database.");
            if (masters.Count == 0 || !parsed) throw new InvalidOperationException("Alson needs allowed masters and an explicit source-date lower bound.");
            if (string.IsNullOrWhiteSpace(Get("AlsonBlobConnectionString"))) throw new InvalidOperationException("Alson Blob connection is missing.");
        }
        var container = Get("AlsonBlobContainer", "sftp-data"); var prefix = Get("AlsonBlobPrefix", "Alson/inbox/");
        if (container != "sftp-data" || prefix != "Alson/inbox/") throw new InvalidOperationException("Alson input must be sftp-data/Alson/inbox/.");
        var recipients = Get("AlsonEmailRecipients", "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new System.Net.Mail.MailAddress(x).Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool emails = bool.TryParse(Get("AlsonErrorEmailEnabled", "false"), out var mail) && mail && !dry;
        var acs = Get("ACS_EMAIL_CONNECTION_STRING");
        if (enabled && emails && (string.IsNullOrWhiteSpace(acs) || recipients.Length == 0))
            throw new InvalidOperationException("Alson error emails enabled but ACS or AlsonEmailRecipients missing.");
        return new(sql, Get("AlsonBlobConnectionString"), container, prefix, expected, enabled, dry, max,
            parsed ? from : DateTime.MaxValue, masters, emails, acs, recipients);
    }
}
