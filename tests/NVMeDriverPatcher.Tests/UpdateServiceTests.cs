using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v4.3.1", "4.3.1")]
    [InlineData("4.3.1-beta1", "4.3.1")]
    [InlineData("4.3.1+abc123", "4.3.1")]
    [InlineData(" v4.3.1-rc2+sha ", "4.3.1")]
    public void NormalizeVersionString_StripsPrefixAndSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeVersionString(raw));
    }

    [Fact]
    public void SanitizeReleaseUrl_AllowsHttpsGitHubReleaseLinks()
    {
        const string url = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/tag/v4.3.1";

        Assert.Equal(url, UpdateService.SanitizeReleaseUrl(url));
    }

    [Theory]
    [InlineData("http://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/tag/v4.3.1")]
    [InlineData("file:///C:/temp/payload.url")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("https://example.com/releases/tag/v4.3.1")]
    [InlineData("https://github.com/other/project/releases/tag/v4.3.1")]
    [InlineData("https://user:token@github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/tag/v4.3.1")]
    [InlineData("not a url")]
    public void SanitizeReleaseUrl_RejectsUnexpectedSchemesAndHosts(string url)
    {
        Assert.Equal(string.Empty, UpdateService.SanitizeReleaseUrl(url));
    }
}
