using System.Security.Cryptography;
using System.Text.Json;

namespace NVMeDriverPatcher.Services;

/// <summary>A release's signed update manifest, parsed only after its signature checked out.</summary>
public sealed record UpdateManifest(
    Version Version,
    Version? MinimumVersion,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, string> Assets)
{
    /// <summary>The lowercase SHA-256 the manifest pins for <paramref name="assetName"/>, or null.</summary>
    public string? Sha256For(string assetName) =>
        Assets.TryGetValue(assetName, out var hash) ? hash : null;
}

public sealed record UpdateManifestCheck(bool Success, string Summary, UpdateManifest? Manifest = null);

/// <summary>
/// Checks the <c>update-manifest.json</c> each release carries: version, the oldest install that
/// may take it, an expiry, and the SHA-256 of every asset, signed with an offline ECDSA P-256 key
/// (IEEE P1363 signature over the file's exact bytes, base64 in <c>update-manifest.json.sig</c>).
/// The public halves ship in the app, so replacing an exe and its <c>.sha256</c> sidecar on the
/// release isn't enough to get a swapped binary staged. Two keys are trusted at once so the
/// signing key can rotate: releases sign with the first, and the second takes over at rotation.
/// </summary>
public static class UpdateManifestService
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.json.sig";
    public const string ProductName = "NVMeDriverPatcher";
    internal const int MaxManifestBytes = 64 * 1024;
    internal const int MaxSignatureBytes = 1024;

    // Validate-ReleaseAssets.ps1 reads the keys between these markers; keep one quoted base64
    // SubjectPublicKeyInfo per line.
    // update-manifest-keys:start
    internal static readonly IReadOnlyList<string> TrustedPublicKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE47D+S4o05vVCn7emK4XIarINIWpq1NiHSFdOu5+eOnV7xTDnPH2085+qaxdMi6Ep3DTdr9eG1t5hGj9ckQ45bg==",
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcXP9AZflNPUHtlZe+qW5VH9EYa71OUOYz5Fsmx4qdIVWAJpuA2XaGSFkyQ+Nx03OsCR97+LkA4VhOQZNu0YLdQ==",
    ];
    // update-manifest-keys:end

    public static UpdateManifestCheck Verify(
        byte[] manifestBytes,
        string? signatureText,
        Version currentVersion,
        DateTimeOffset nowUtc) =>
        Verify(manifestBytes, signatureText, TrustedPublicKeys, currentVersion, nowUtc);

    internal static UpdateManifestCheck Verify(
        byte[] manifestBytes,
        string? signatureText,
        IReadOnlyList<string> trustedKeys,
        Version currentVersion,
        DateTimeOffset nowUtc)
    {
        if (manifestBytes is null || manifestBytes.Length == 0 || manifestBytes.Length > MaxManifestBytes)
            return Fail("The release's update manifest is missing or too large.");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String((signatureText ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            return Fail("The update manifest's signature file isn't valid base64.");
        }
        if (signature.Length != 64)
            return Fail("The update manifest's signature isn't a P-256 signature.");

        if (!trustedKeys.Any(key => VerifyWith(key, manifestBytes, signature)))
            return Fail("The update manifest's signature doesn't match a key this version trusts.");

        UpdateManifest manifest;
        try
        {
            manifest = Parse(manifestBytes);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail($"The signed update manifest couldn't be read: {ex.Message}");
        }

        if (manifest.ExpiresUtc <= nowUtc)
            return Fail($"The update manifest expired on {manifest.ExpiresUtc:yyyy-MM-dd}. Download the release from its GitHub page instead.");
        if (manifest.Version <= currentVersion)
            return Fail($"The update manifest names {manifest.Version}, which isn't newer than the installed {currentVersion}.");
        if (manifest.MinimumVersion is not null && currentVersion < manifest.MinimumVersion)
            return Fail($"{manifest.Version} needs {manifest.MinimumVersion} or later installed first. Download it from the release page.");

        return new UpdateManifestCheck(true, $"Signed update manifest for {manifest.Version} verified.", manifest);
    }

    private static UpdateManifest Parse(byte[] manifestBytes)
    {
        using var doc = JsonDocument.Parse(manifestBytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("the root isn't an object.");
        if (root.GetProperty("schema").GetInt32() != 1)
            throw new FormatException("unknown schema.");
        if (!string.Equals(root.GetProperty("product").GetString(), ProductName, StringComparison.Ordinal))
            throw new FormatException("it names another product.");

        if (!UpdateService.TryParseComparableVersion(root.GetProperty("version").GetString(), out var version))
            throw new FormatException("version isn't a version.");

        Version? minimum = null;
        if (root.TryGetProperty("minimumVersion", out var minimumElement) && minimumElement.ValueKind == JsonValueKind.String)
        {
            if (!UpdateService.TryParseComparableVersion(minimumElement.GetString(), out var parsedMinimum))
                throw new FormatException("minimumVersion isn't a version.");
            minimum = parsedMinimum;
        }

        if (!DateTimeOffset.TryParse(
                root.GetProperty("expiresUtc").GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var expires))
            throw new FormatException("expiresUtc isn't a date.");

        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in root.GetProperty("assets").EnumerateObject())
        {
            var hash = VerifiedDownloader.ExtractSha256(asset.Value.GetString());
            if (hash is null || asset.Value.GetString()!.Trim().Length != 64)
                throw new FormatException($"the hash for {asset.Name} isn't a SHA-256.");
            assets[asset.Name] = hash;
        }
        if (assets.Count == 0)
            throw new FormatException("it lists no assets.");

        return new UpdateManifest(version, minimum, expires, assets);
    }

    private static bool VerifyWith(string publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.KeySize == 256 && ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static UpdateManifestCheck Fail(string summary) => new(false, summary);
}
