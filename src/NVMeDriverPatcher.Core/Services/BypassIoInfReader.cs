namespace NVMeDriverPatcher.Services;

public enum BypassIoInfDeclaration
{
    /// <summary>The INF couldn't be found, read, or doesn't install this service.</summary>
    Unknown,
    /// <summary>The service's Parameters key sets StorageSupportedFeatures with the BypassIO bit.</summary>
    Declared,
    /// <summary>The INF installs the service but never sets that value, so BypassIO is blocked.</summary>
    NotDeclared
}

// Microsoft documents the opt-in for storage drivers as a "Parameters" key under the service with
// a DWORD StorageSupportedFeatures whose bit 0 (STORAGE_SUPPORTED_FEATURES_BYPASS_IO) is set,
// written from the INF. This reads that declaration from the INF text of the bound driver, so the
// verdict doesn't depend on fsutil's localized prose.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/bypassio
internal static class BypassIoInfReader
{
    internal const string ValueName = "StorageSupportedFeatures";

    public static BypassIoInfDeclaration Evaluate(string? infText, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(infText) || string.IsNullOrWhiteSpace(serviceName))
            return BypassIoInfDeclaration.Unknown;

        var sections = ParseSections(infText, out var strings);
        var installSections = new List<string>();
        foreach (var lines in sections.Values)
        {
            foreach (var line in lines)
            {
                if (!TrySplitKey(line, out var key, out var value) ||
                    !key.Equals("AddService", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fields = SplitFields(value);
                if (fields.Count < 3) continue;
                if (Resolve(fields[0], strings).Equals(serviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    installSections.Add(Resolve(fields[2], strings));
            }
        }
        if (installSections.Count == 0) return BypassIoInfDeclaration.Unknown;

        foreach (var install in installSections)
        {
            foreach (var installLine in LinesOf(sections, install))
            {
                if (!TrySplitKey(installLine, out var key, out var value) ||
                    !key.Equals("AddReg", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var regSection in SplitFields(value))
                {
                    foreach (var regLine in LinesOf(sections, Resolve(regSection, strings)))
                    {
                        var f = SplitFields(regLine);
                        if (f.Count < 5) continue;
                        if (!Resolve(f[0], strings).Equals("HKR", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Resolve(f[1], strings).Equals("Parameters", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Resolve(f[2], strings).Equals(ValueName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (TryParseNumber(Resolve(f[4], strings), out var number) && (number & 1) == 1)
                            return BypassIoInfDeclaration.Declared;
                    }
                }
            }
        }
        return BypassIoInfDeclaration.NotDeclared;
    }

    // A section may be platform-decorated (Name.NT, Name.NTamd64), so a reference to "Name" also
    // covers "Name.<decoration>".
    private static IEnumerable<string> LinesOf(Dictionary<string, List<string>> sections, string name)
    {
        var wanted = name.Trim();
        foreach (var (sectionName, lines) in sections)
        {
            if (sectionName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                sectionName.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines) yield return line;
            }
        }
    }

    private static Dictionary<string, List<string>> ParseSections(string text, out Dictionary<string, string> strings)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var pending = string.Empty;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (pending.Length > 0)
            {
                line = pending + line;
                pending = string.Empty;
            }
            if (line.EndsWith('\\'))
            {
                pending = line[..^1].TrimEnd() + " ";
                continue;
            }
            if (line.Length == 0) continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                current = line[1..^1].Trim();
                if (!sections.ContainsKey(current)) sections[current] = [];
                continue;
            }
            if (current is null) continue;

            sections[current].Add(line);
            if (current.Equals("Strings", StringComparison.OrdinalIgnoreCase) &&
                TrySplitKey(line, out var key, out var value))
                strings[key] = Unquote(value);
        }
        return sections;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static bool TrySplitKey(string line, out string key, out string value)
    {
        var index = line.IndexOf('=');
        if (index <= 0)
        {
            key = value = string.Empty;
            return false;
        }
        key = line[..index].Trim();
        value = line[(index + 1)..].Trim();
        return true;
    }

    private static List<string> SplitFields(string text)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; current.Append(ch); }
            else if (ch == ',' && !inQuotes) { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static string Resolve(string field, Dictionary<string, string> strings)
    {
        var value = Unquote(field);
        if (value.Length > 2 && value[0] == '%' && value[^1] == '%' &&
            strings.TryGetValue(value[1..^1], out var resolved))
            return resolved;
        return value;
    }

    private static bool TryParseNumber(string text, out long number)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out number);
    }
}
