using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NVMeDriverPatcher.Tests;

// Validates the bundled safety-data JSON (windows_build_rules.json, compat.json) and the
// telemetry payload contract against their packaged JSON Schemas. Uses a small built-in
// validator (no external dependency, matching the repo's hand-rolled schema-check style) that
// supports the subset of JSON Schema the schemas use: type, required, properties, items, enum,
// additionalProperties:false, and minimum/maximum.
public sealed class SafetyDataSchemaTests
{
    [Fact]
    public void WindowsBuildRules_ConformsToSchema()
    {
        var errors = MiniJsonSchema.Validate(
            DataFile("src", "NVMeDriverPatcher.Core", "windows_build_rules.json"),
            SchemaFile("windows_build_rules.schema.json"));
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void CompatData_ConformsToSchema()
    {
        var errors = MiniJsonSchema.Validate(
            DataFile("src", "NVMeDriverPatcher.Core", "compat.json"),
            SchemaFile("compat.schema.json"));
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void TelemetryPayloadSample_ConformsToSchema()
    {
        // A representative client payload (matches CompatReport's camelCase contract).
        const string sample = """
        {
          "schemaVersion": 1,
          "submittedAt": "2026-06-14T00:00:00.0000000Z",
          "anonId": "00000000-0000-0000-0000-000000000000",
          "appVersion": "5.0.0",
          "osBuild": "26100.3775",
          "cpu": "Intel64 Family 6 Model 154, GenuineIntel",
          "controllers": [ { "model": "WD_BLACK SN850X", "firmware": "620331WD", "migrated": true } ],
          "profile": "Safe",
          "verification": "Confirmed",
          "watchdog": "Healthy",
          "watchdogEvents": 0,
          "reliabilityDelta": null,
          "benchmarkDeltaPercent": 23.5
        }
        """;
        var errors = MiniJsonSchema.Validate(sample, SchemaFile("telemetry_payload.schema.json"));
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // --- Negative cases: intentionally malformed fixtures must fail validation ---

    [Fact]
    public void BuildRules_UnknownExpectedPath_Fails()
    {
        const string bad = """
        { "schemaVersion": 1, "rules": [ { "id": "x", "expectedPath": "make-it-up", "summary": "s" } ] }
        """;
        var errors = MiniJsonSchema.Validate(bad, SchemaFile("windows_build_rules.schema.json"));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void BuildRules_MissingRequiredField_Fails()
    {
        const string bad = """{ "schemaVersion": 1, "rules": [ { "id": "x", "summary": "s" } ] }""";
        var errors = MiniJsonSchema.Validate(bad, SchemaFile("windows_build_rules.schema.json"));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Compat_UnknownLevel_Fails()
    {
        const string bad = """
        { "schemaVersion": 1, "entries": [ { "controller": "c", "firmware": "*", "level": "Catastrophic" } ] }
        """;
        var errors = MiniJsonSchema.Validate(bad, SchemaFile("compat.schema.json"));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Compat_AdditionalProperty_Fails()
    {
        const string bad = """
        { "schemaVersion": 1, "entries": [ { "controller": "c", "firmware": "*", "level": "Good", "rootkit": true } ] }
        """;
        var errors = MiniJsonSchema.Validate(bad, SchemaFile("compat.schema.json"));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Telemetry_WrongType_Fails()
    {
        const string bad = """
        { "schemaVersion": 1, "anonId": "id", "controllers": [ { "model": "m", "firmware": "f", "migrated": "yes" } ] }
        """;
        var errors = MiniJsonSchema.Validate(bad, SchemaFile("telemetry_payload.schema.json"));
        Assert.NotEmpty(errors);
    }

    private static string DataFile(params string[] parts) => File.ReadAllText(RepoPath(parts));
    private static string SchemaFile(string name) => File.ReadAllText(RepoPath(new[] { "packaging", "schemas", name }));

    private static string RepoPath(string[] parts, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(new[] { repoRoot }.Concat(parts).ToArray());
    }
}

// Minimal recursive JSON Schema validator — supports the subset used by this repo's schemas.
internal static class MiniJsonSchema
{
    public static List<string> Validate(string dataJson, string schemaJson)
    {
        var errors = new List<string>();
        using var data = JsonDocument.Parse(dataJson);
        using var schema = JsonDocument.Parse(schemaJson);
        ValidateNode(data.RootElement, schema.RootElement, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement data, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("type", out var typeEl))
        {
            if (!MatchesAnyType(data, typeEl))
            {
                errors.Add($"{path}: expected type {TypeText(typeEl)} but got {data.ValueKind}");
                return; // further checks assume the type matched
            }
        }

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var ok = enumEl.EnumerateArray().Any(allowed => JsonEquals(allowed, data));
            if (!ok) errors.Add($"{path}: value '{Raw(data)}' is not in the allowed set");
        }

        if (data.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var min) && data.GetDouble() < min.GetDouble())
                errors.Add($"{path}: {data.GetDouble()} is below minimum {min.GetDouble()}");
            if (schema.TryGetProperty("maximum", out var max) && data.GetDouble() > max.GetDouble())
                errors.Add($"{path}: {data.GetDouble()} is above maximum {max.GetDouble()}");
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            JsonElement props = default;
            var hasProps = schema.TryGetProperty("properties", out props);

            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    var name = r.GetString()!;
                    if (!data.TryGetProperty(name, out _))
                        errors.Add($"{path}: missing required property '{name}'");
                }
            }

            var additionalAllowed = !(schema.TryGetProperty("additionalProperties", out var ap)
                                      && ap.ValueKind == JsonValueKind.False);

            foreach (var member in data.EnumerateObject())
            {
                if (hasProps && props.TryGetProperty(member.Name, out var propSchema))
                    ValidateNode(member.Value, propSchema, $"{path}.{member.Name}", errors);
                else if (!additionalAllowed)
                    errors.Add($"{path}: unexpected property '{member.Name}'");
            }
        }

        if (data.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            int i = 0;
            foreach (var el in data.EnumerateArray())
                ValidateNode(el, items, $"{path}[{i++}]", errors);
        }
    }

    private static bool MatchesAnyType(JsonElement data, JsonElement typeEl)
    {
        if (typeEl.ValueKind == JsonValueKind.String)
            return MatchesType(data, typeEl.GetString()!);
        if (typeEl.ValueKind == JsonValueKind.Array)
            return typeEl.EnumerateArray().Any(t => MatchesType(data, t.GetString()!));
        return true;
    }

    private static bool MatchesType(JsonElement data, string type) => type switch
    {
        "object" => data.ValueKind == JsonValueKind.Object,
        "array" => data.ValueKind == JsonValueKind.Array,
        "string" => data.ValueKind == JsonValueKind.String,
        "boolean" => data.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => data.ValueKind == JsonValueKind.Number,
        "integer" => data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out _),
        "null" => data.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string TypeText(JsonElement typeEl) =>
        typeEl.ValueKind == JsonValueKind.Array
            ? string.Join("|", typeEl.EnumerateArray().Select(t => t.GetString()))
            : typeEl.GetString() ?? "?";

    private static string Raw(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText();

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            _ => a.GetRawText() == b.GetRawText(),
        };
    }
}
