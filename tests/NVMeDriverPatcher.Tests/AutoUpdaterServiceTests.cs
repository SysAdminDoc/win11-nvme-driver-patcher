using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// AutoUpdaterService.StageUpdateAsync host-allowlist + filename guards — both run pre-network
// so these tests exercise rejection paths without any I/O. The happy path is covered
// manually since it requires a live GitHub release.
public sealed class AutoUpdaterServiceTests
{
    [Fact]
    public async Task NonHttpsUrl_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "http://github.com/foo/bar/releases/download/asset.exe",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("https", result.Summary);
    }

    [Fact]
    public async Task UrlOnUnknownHost_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "https://evil.example.com/foo.exe",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("not in the allowlist", result.Summary);
    }

    [Fact]
    public async Task AssetNameWithPathTraversal_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "https://github.com/foo/bar/releases/download/asset.exe",
            "..\\evil.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("invalid", result.Summary);
    }

    [Fact]
    public async Task MalformedUri_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "not a url at all",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]                                                            // too short
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ", null)] // non-hex char
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]      // normalized to lower
    public void ExtractSha256_AcceptsBareHash(string? input, string? expected)
    {
        Assert.Equal(expected, AutoUpdaterService.ExtractSha256(input!));
    }

    [Fact]
    public void ExtractSha256_AcceptsSha256SumFormat()
    {
        // sha256sum output is `<hash>  <filename>`; we take the first whitespace-delimited token.
        const string body = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  NVMeDriverPatcher.exe";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AutoUpdaterService.ExtractSha256(body));
    }

    [Fact]
    public void ExtractSha256_IgnoresLinesAfterFirst()
    {
        // A .sha256 file may include a trailing newline or a second entry; we only care
        // about the leading hash so the parser stays tolerant of formatting variations.
        const string body =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  file.exe\n" +
            "abc123  unrelated.exe\n";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AutoUpdaterService.ExtractSha256(body));
    }

    [Fact]
    public void BuildRestartCommand_EscapesSingleQuotesInPaths()
    {
        // PowerShell single-quoted strings treat '' as a literal apostrophe. The restart
        // command builder must double up any apostrophe in the staged or current exe path
        // so a directory like `C:\Users\O'Brien\…` doesn't break the command line.
        var cmd = AutoUpdaterService.BuildRestartCommand(
            @"C:\Users\O'Brien\staged.exe",
            @"C:\Program Files\App's\current.exe");

        Assert.Contains(@"'C:\Users\O''Brien\staged.exe'", cmd);
        Assert.Contains(@"'C:\Program Files\App''s\current.exe'", cmd);
        Assert.Contains("-LiteralPath", cmd);
        Assert.DoesNotContain("\"", cmd); // Never emit double quotes — they can re-break inside the single-quoted context.
    }

    [Fact]
    public async Task SidecarLookup_RejectsSidecarsOnUntrustedHost()
    {
        // The sidecar fetch reuses the same host allowlist as the asset download — a release
        // that somehow points at an off-GitHub host must NOT be silently "integrity-verified"
        // against an attacker-supplied sha256 file.
        var evil = new Uri("https://evil.example.com/asset.exe");
        var result = await AutoUpdaterService.TryFetchSidecarHashAsync(evil, CancellationToken.None);
        Assert.Null(result);
    }
}
