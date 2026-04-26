using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Shared "download a binary + verify before use" primitive. Centralizes the patterns that
/// <see cref="AutoUpdaterService"/>, <see cref="ViVeToolService"/>, and
/// <see cref="BenchmarkService"/> each re-implemented: host allowlist enforcement, size
/// guardrails, explicit redirect control, `.part` staging, SHA-256 sidecar verification,
/// atomic promotion into the final staged path. Callers supply policy via <see cref="DownloadPolicy"/>.
/// </summary>
public static class VerifiedDownloader
{
    public enum IntegritySignal
    {
        /// <summary>No content-level verification available — size + host match only.</summary>
        None,
        /// <summary>SHA-256 sidecar fetched and matched.</summary>
        Sha256Sidecar,
        /// <summary>Authenticode signature validated via <c>signtool verify /pa</c>.</summary>
        Authenticode
    }

    public sealed class DownloadPolicy
    {
        /// <summary>HTTPS-only allowlist of acceptable hosts. Every redirect hop is re-checked.</summary>
        public required IReadOnlyCollection<string> AllowedHosts { get; init; }

        /// <summary>Minimum acceptable downloaded-file size (bytes). Guards against 404-page payloads.</summary>
        public long MinBytes { get; init; } = 1_024;

        /// <summary>Maximum acceptable downloaded-file size (bytes). Guards against supply-chain bombs.</summary>
        public long MaxBytes { get; init; } = 262_144_000;

        /// <summary>Maximum redirect hops to follow before giving up.</summary>
        public int MaxRedirects { get; init; } = 6;

        /// <summary>
        /// If true, integrity MUST be verified (SHA-256 sidecar or Authenticode). If neither
        /// is available, the download is rejected. Use for anything that will be executed.
        /// If false, a missing sidecar is reported via <see cref="DownloadResult.Signal"/> but
        /// doesn't fail the download (used for archive downloads where the archive's own
        /// content check substitutes, e.g. zip-slip defense + filesize cap).
        /// </summary>
        public bool RequireIntegrity { get; init; } = true;

        /// <summary>When true, attempt Authenticode as a fallback if no sidecar is found.</summary>
        public bool AllowAuthenticodeFallback { get; init; } = true;
    }

    public sealed class DownloadResult
    {
        public bool Success { get; init; }
        public string? Path { get; init; }
        public IntegritySignal Signal { get; init; }
        public string Summary { get; init; } = string.Empty;
        public Exception? Error { get; init; }
    }

    /// <summary>
    /// Downloads <paramref name="initialUri"/> to <paramref name="destinationPath"/>, writing
    /// through a `.part` sibling and atomically moving into place only after every policy
    /// check passes. Returns a descriptive result rather than throwing for expected failures
    /// (host refused, size out of range, sidecar mismatch).
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        HttpClient client,
        Uri initialUri,
        string destinationPath,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (initialUri is null) throw new ArgumentNullException(nameof(initialUri));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("destinationPath required", nameof(destinationPath));
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        if (initialUri.Scheme != Uri.UriSchemeHttps)
            return Failure("Download URL must use https://");
        if (!IsAllowedHost(initialUri.Host, policy.AllowedHosts))
            return Failure($"Download host '{initialUri.Host}' is not in the allowlist.");

        var partPath = destinationPath + ".part";
        TryDelete(partPath);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Follow redirects manually so every hop is re-checked against the allowlist. A
            // release that somehow points at an off-GitHub CDN after redirect is refused,
            // not silently followed by HttpClient's built-in redirect handling.
            var finalUri = await DownloadFollowingAllowlistedRedirectsAsync(
                client, initialUri, partPath, policy, cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(partPath);
            if (info.Length < policy.MinBytes || info.Length > policy.MaxBytes)
            {
                TryDelete(partPath);
                return Failure($"Downloaded file size {info.Length} bytes is outside expected range ({policy.MinBytes}..{policy.MaxBytes}).");
            }

            // Integrity verification. Sidecar is preferred (fast, does not require the binary
            // to be Authenticode-signed); Authenticode is a last-resort fallback; neither ==
            // weak trust, which we accept only when policy explicitly allows it.
            var signal = IntegritySignal.None;
            var sidecarHash = await TryFetchSidecarHashAsync(client, finalUri, policy.AllowedHosts, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(sidecarHash))
            {
                var actual = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, sidecarHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    return Failure($"SHA-256 mismatch — aborting. Expected {sidecarHash}, got {actual}.");
                }
                signal = IntegritySignal.Sha256Sidecar;
            }
            else if (policy.AllowAuthenticodeFallback && VerifyAuthenticode(partPath))
            {
                signal = IntegritySignal.Authenticode;
            }
            else if (policy.RequireIntegrity)
            {
                TryDelete(partPath);
                return Failure(
                    "Integrity check failed: no SHA-256 sidecar and no Authenticode signature. Aborting.");
            }

            // Atomic promote. File.Move(overwrite:true) is atomic on NTFS so the destination
            // never appears to an observer in a half-written state.
            File.Move(partPath, destinationPath, overwrite: true);

            return new DownloadResult
            {
                Success = true,
                Path = destinationPath,
                Signal = signal,
                Summary = signal switch
                {
                    IntegritySignal.Sha256Sidecar => "Downloaded and verified via SHA-256 sidecar.",
                    IntegritySignal.Authenticode => "Downloaded and verified via Authenticode signature.",
                    _ => "Downloaded (weak integrity: size + host allowlist only)."
                }
            };
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            return new DownloadResult
            {
                Success = false,
                Summary = $"Download failed: {ex.GetType().Name}: {ex.Message}",
                Error = ex
            };
        }
    }

    // --- Integrity primitives (also used directly by AutoUpdaterService's public API). ---

    /// <summary>
    /// Fetches <c><paramref name="assetUri"/>.sha256</c> and returns the parsed hex digest,
    /// or <c>null</c> if the sidecar is missing, on a disallowed host, or malformed.
    /// </summary>
    public static async Task<string?> TryFetchSidecarHashAsync(
        HttpClient client,
        Uri assetUri,
        IReadOnlyCollection<string> allowedHosts,
        CancellationToken cancellationToken)
    {
        var sidecarUri = new UriBuilder(assetUri) { Path = assetUri.AbsolutePath + ".sha256" }.Uri;
        if (!IsAllowedHost(sidecarUri.Host, allowedHosts)) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, sidecarUri);
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ExtractSha256(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a sidecar body (either a bare 64-char hex hash, or `sha256sum` format
    /// "<hash>  <filename>") and returns the lowercase hex digest. Returns <c>null</c> for
    /// any malformed input.
    /// </summary>
    public static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var firstLine = text.Split('\n').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine)) return null;
        var token = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(token) || token.Length != 64) return null;
        foreach (var c in token)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        return token.ToLowerInvariant();
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    public static bool VerifyAuthenticode(string path)
    {
        try
        {
            var signtool = ResolveSigntool();
            if (string.IsNullOrEmpty(signtool)) return false;

            var psi = new ProcessStartInfo(signtool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("verify");
            psi.ArgumentList.Add("/pa");
            psi.ArgumentList.Add("/q");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { stderrTask.GetAwaiter().GetResult(); } catch { }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // --- Internals ---

    private static async Task<Uri> DownloadFollowingAllowlistedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        string destPath,
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (int hops = 0; hops < policy.MaxRedirects; hops++)
        {
            if (!IsAllowedHost(current.Host, policy.AllowedHosts))
                throw new InvalidOperationException($"Refusing redirect to untrusted host '{current.Host}'.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Redirect response has no Location header.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength &&
                (contentLength < policy.MinBytes || contentLength > policy.MaxBytes))
            {
                throw new InvalidOperationException(
                    $"Content-Length {contentLength} bytes is outside the allowed range ({policy.MinBytes}..{policy.MaxBytes}).");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > policy.MaxBytes)
                    throw new InvalidOperationException($"Download exceeded maximum size ({policy.MaxBytes} bytes).");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }
        throw new InvalidOperationException("Download followed too many redirects.");
    }

    private static bool IsAllowedHost(string host, IReadOnlyCollection<string> allowlist)
    {
        foreach (var candidate in allowlist)
        {
            if (string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? ResolveSigntool()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe",
            @"C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe",
            @"C:\Program Files\Windows Kits\10\bin\x64\signtool.exe"
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }

        try
        {
            var binRoot = @"C:\Program Files (x86)\Windows Kits\10\bin";
            if (Directory.Exists(binRoot))
            {
                var versioned = Directory.GetDirectories(binRoot)
                    .Select(d => Path.Combine(d, "x64", "signtool.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (versioned is not null) return versioned;
            }
        }
        catch { }

        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static DownloadResult Failure(string summary) =>
        new() { Success = false, Summary = summary };
}
