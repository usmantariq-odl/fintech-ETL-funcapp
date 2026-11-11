using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FintechEtl.FunctionApp.Alson;

internal sealed record AlsonProbe(string Code, decimal Fuel, decimal Water, decimal Temperature);
internal sealed record AlsonFrame(string Command, string Master, string RecordId, DateTime At, byte[] Hash,
    string Raw, byte[] Bytes, string Dispenser = null, int? Nozzle = null, string Product = null,
    decimal? Volume = null, decimal? Amount = null, decimal? Price = null, decimal? Totalizer = null,
    IReadOnlyList<AlsonProbe> Probes = null);
internal sealed class AlsonPendingException(string message) : Exception(message);

internal static class AlsonParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static AlsonFrame Parse(byte[] envelope)
    {
        using var json = JsonDocument.Parse(envelope);
        var e = json.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("Expected JSON envelope object.");
        string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var encoded = Text("rawBase64"); var supplied = Text("rawFrame");
        if (encoded == null && supplied == null) throw new FormatException("Missing raw frame.");
        var bytes = encoded == null ? Utf8.GetBytes(supplied) : Convert.FromBase64String(encoded);
        var raw = Utf8.GetString(bytes); var hash = SHA256.HashData(bytes);
        if (Text("sha256") is string digest && !Convert.ToHexString(hash).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Frame checksum mismatch.");
        if (supplied != null && supplied != raw) throw new FormatException("rawFrame and exact bytes disagree.");
        var frame = raw.Trim();
        if (!frame.StartsWith('#') || !frame.EndsWith('|') || frame.Count(c => c == '|') != 1)
            throw new FormatException("Frame must have one #...| boundary with no appended modem commands.");
        var content = frame[1..^1]; var parts = content.Split(','); var cmd = parts[0];
        if (cmd is not ("00" or "20")) return new(cmd, "", "", DateTime.MinValue, hash, raw, bytes);
        string Id(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value != value.Trim())
                throw new FormatException("Invalid or whitespace-padded identifier.");
            return value;
        }
        decimal Number(string value)
        {
            if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var n)) throw new FormatException("Invalid decimal field.");
            return n;
        }
        void RecordId(string id)
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw new FormatException("Record ID does not fit existing integer column.");
        }
        DateTime At(string date, string time)
        {
            if (!DateTime.TryParseExact(date + " " + time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at) || at.Year < 1753) throw new FormatException("Invalid source date/time.");
            return DateTime.SpecifyKind(at, DateTimeKind.Unspecified);
        }
        if (cmd == "00")
        {
            if (parts.Length != 13) throw new FormatException("Sale requires 13 fields.");
            RecordId(parts[1]);
            if (!int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var nozzle) || nozzle <= 0)
                throw new FormatException("Invalid nozzle.");
            var volume = Number(parts[6]); var price = Number(parts[7]); var amount = Number(parts[8]); var total = Number(parts[9]);
            if (volume < 0 || price < 0 || amount < 0 || total < 0) throw new FormatException("Negative sale measurement.");
            return new(cmd, Id(parts[2]), Id(parts[1]), At(parts[11], parts[10]), hash, raw, bytes,
                Id(parts[3]), nozzle, Id(parts[5]), volume, amount, price, total);
        }
        var groups = content.Split('~');
        var header = groups[0].Split(','); var probes = new List<AlsonProbe>();
        if (groups.Length == 1)
        {
            if (header.Length != 9) throw new FormatException("Flat ATG requires 9 fields.");
            probes.Add(new(Id(header[5]), Number(header[6]), Number(header[7]), Number(header[8])));
        }
        else
        {
            if (header.Length != 5) throw new FormatException("ATG header requires 5 fields.");
            for (int i = 1; i < groups.Length; i++)
            {
                if (i == groups.Length - 1 && groups[i] is "" or "0" or "1") continue;
                var f = groups[i].Split(',');
                if (f.Length != 4) throw new FormatException("Probe requires code,fuel,water,temperature.");
                if (f[0].Equals("FFFFF", StringComparison.OrdinalIgnoreCase)) continue;
                probes.Add(new(Id(f[0]), Number(f[1]), Number(f[2]), Number(f[3])));
            }
        }
        if (probes.Count == 0 || probes.Any(p => p.Code.Equals("FFFFF", StringComparison.OrdinalIgnoreCase)) ||
            probes.Select(p => p.Code).Distinct(StringComparer.Ordinal).Count() != probes.Count)
            throw new FormatException("Missing, unused-only or repeated probes.");
        if (probes.Any(p => p.Fuel < 0 || p.Water < 0)) throw new FormatException("Negative tank reading.");
        RecordId(header[1]);
        return new(cmd, Id(header[2]), Id(header[1]), At(header[4], header[3]), hash, raw, bytes, Probes: probes);
    }
}
