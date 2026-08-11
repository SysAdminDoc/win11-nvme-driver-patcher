using System.Globalization;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class EventLogXPathCultureTests
{
    /// <summary>
    /// In a custom .NET format string ':' and '.' are the CULTURE's time and decimal separators,
    /// not literals. A hand-rolled "yyyy-MM-ddTHH:mm:ss.fffffffZ" therefore emits something like
    /// "2026-08-11T14.30.00,0000000Z" under fi-FI or de-DE, which is not a valid SystemTime: the
    /// event-log query either throws — leaving the watchdog permanently Unavailable and auto-revert
    /// dead — or matches nothing and certifies an unstable patch as healthy. Neither is visible on
    /// an en-US dev box, which is why this shipped.
    /// </summary>
    [Theory]
    [InlineData("fi-FI")]   // time separator '.', decimal separator ','
    [InlineData("de-DE")]   // decimal separator ','
    [InlineData("en-US")]
    [InlineData("")]        // invariant
    public void XPathTimestamp_IsIdenticalUnderEveryCulture(string culture)
    {
        var moment = new DateTime(2026, 8, 11, 14, 30, 0, DateTimeKind.Utc);
        var expected = "2026-08-11T14:30:00.0000000Z";

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var formatted = EventLogService.FormatXPathTimestamp(moment);

            Assert.Equal(expected, formatted);
            Assert.Contains(":", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(",", formatted, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
