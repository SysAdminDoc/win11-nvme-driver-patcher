using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class GpoPolicyServiceTests
{
    [Fact]
    public void AnyApplied_FalseForEmptyOverlay_TrueWhenAnyFieldSet()
    {
        Assert.False(new PolicyOverlay().AnyApplied);
        Assert.True(new PolicyOverlay { SkipWarnings = true }.AnyApplied);
        Assert.True(new PolicyOverlay { PatchProfile = PatchProfile.Full }.AnyApplied);
        Assert.True(new PolicyOverlay { WatchdogWindowHours = 24 }.AnyApplied);
    }

    /// <summary>
    /// AnyApplied is a hand-kept list. It missed both persistence-guard policies, so a GPO that
    /// pinned only those read as "no policy". Reflection makes the next missed field fail here.
    /// </summary>
    [Fact]
    public void AnyApplied_IsTrueForEveryPolicyFieldOnItsOwn()
    {
        var fields = OverlayFields();
        Assert.Contains(fields, f => f.Name == nameof(PolicyOverlay.PersistenceGuardEnabled));
        Assert.Contains(fields, f => f.Name == nameof(PolicyOverlay.PersistenceGuardMaxReapplies));

        foreach (var field in fields)
        {
            var overlay = new PolicyOverlay();
            field.SetValue(overlay, SampleValue(Nullable.GetUnderlyingType(field.PropertyType)!));
            Assert.True(overlay.AnyApplied, $"{field.Name} is pinned on its own, but AnyApplied says no policy applies");
        }
    }

    [Fact]
    public void EveryPolicyField_IsReadFromItsRegistryValueAndApplied()
    {
        var source = ReadRepoFile("src", "NVMeDriverPatcher.Core", "Services", "GpoPolicyService.cs");

        foreach (var field in OverlayFields())
        {
            Assert.Matches($@"overlay\.{field.Name}\s*=\s*Read\w+\(key,\s*""{field.Name}""", source);
            Assert.Matches($@"overlay\.{field.Name}\s+is\s", source);
        }
    }

    [Fact]
    public void EveryPolicyField_HasAnAdmxPolicyAndLocalizedStrings()
    {
        // A field the service reads but the template doesn't offer can't be set from Group Policy.
        var admx = XDocument.Parse(ReadRepoFile("packaging", "admx", "NVMeDriverPatcher.admx"));
        var stringIds = XDocument.Parse(ReadRepoFile("packaging", "admx", "en-US", "NVMeDriverPatcher.adml"))
            .Descendants().Where(e => e.Name.LocalName == "string")
            .Select(e => (string?)e.Attribute("id"))
            .ToHashSet(StringComparer.Ordinal);
        var policies = admx.Descendants().Where(e => e.Name.LocalName == "policy").ToList();
        Assert.NotEmpty(policies);

        foreach (var field in OverlayFields())
        {
            // Toggles carry valueName on the policy; numeric ones carry it on an <elements> child.
            var policy = policies.SingleOrDefault(p => p.DescendantsAndSelf()
                .Any(e => (string?)e.Attribute("valueName") == field.Name));
            Assert.True(policy is not null, $"{field.Name} has no ADMX policy");
            foreach (var attribute in new[] { "displayName", "explainText" })
            {
                var id = Regex.Match((string?)policy!.Attribute(attribute) ?? "", @"^\$\(string\.(\w+)\)$").Groups[1].Value;
                Assert.True(stringIds.Contains(id), $"{field.Name}'s {attribute} has no ADML string");
            }
        }
    }

    private static List<PropertyInfo> OverlayFields() =>
        typeof(PolicyOverlay).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && Nullable.GetUnderlyingType(p.PropertyType) is not null)
            .ToList();

    // Deliberately the "off"/zero value: a policy pinning a feature off is still a policy.
    private static object SampleValue(Type type) =>
        type == typeof(bool) ? false
        : type == typeof(int) ? 0
        : type.IsEnum ? Enum.GetValues(type).GetValue(0)!
        : throw new NotSupportedException($"Add a sample value for policy type {type.Name}.");

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NVMeDriverPatcher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"expected repo file missing: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ApplyTo_PinnedValues_OverrideConfig()
    {
        var config = new AppConfig
        {
            PatchProfile = PatchProfile.Safe,
            IncludeServerKey = false,
            SkipWarnings = false,
            CompatTelemetryEnabled = true,
        };
        var overlay = new PolicyOverlay
        {
            PatchProfile = PatchProfile.Full,
            IncludeServerKey = true,
            SkipWarnings = true,
            CompatTelemetryEnabled = false,
        };

        GpoPolicyService.ApplyTo(config, overlay);

        Assert.Equal(PatchProfile.Full, config.PatchProfile);
        Assert.True(config.IncludeServerKey);
        Assert.True(config.SkipWarnings);
        Assert.False(config.CompatTelemetryEnabled);
    }

    [Fact]
    public void ApplyTo_EmptyOverlay_LeavesConfigUntouched()
    {
        var config = new AppConfig
        {
            PatchProfile = PatchProfile.Full,
            IncludeServerKey = true,
            SkipWarnings = true,
            CompatTelemetryEnabled = false,
        };

        GpoPolicyService.ApplyTo(config, new PolicyOverlay());

        Assert.Equal(PatchProfile.Full, config.PatchProfile);
        Assert.True(config.IncludeServerKey);
        Assert.True(config.SkipWarnings);
        Assert.False(config.CompatTelemetryEnabled);
    }

    [Fact]
    public void ApplyTo_WatchdogOverlay_PropagatesToWatchdogState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_Gpo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new AppConfig { WorkingDir = dir };
            var saved = GpoPolicyService.ApplyTo(config, new PolicyOverlay { WatchdogAutoRevert = false, WatchdogWindowHours = 72 });

            Assert.NotNull(saved);
            Assert.True(saved.Success, saved.Summary);
            var state = EventLogWatchdogService.LoadState(config);
            Assert.False(state.AutoRevertEnabled);
            Assert.Equal(72, state.WindowHours);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
