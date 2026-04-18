using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class EventLogServiceTests
{
    [Fact]
    public void Truncate_ReturnsInputUnchanged_WhenShorterThanLimit()
    {
        var input = "short message";
        var result = EventLogService.TruncatePreservingSurrogates(input, 100);
        Assert.Same(input, result);
    }

    [Fact]
    public void Truncate_ReturnsInputUnchanged_WhenEqualToLimit()
    {
        var input = new string('a', 50);
        var result = EventLogService.TruncatePreservingSurrogates(input, 50);
        Assert.Same(input, result);
    }

    [Fact]
    public void Truncate_SplitsAtExpectedLength_WhenBmpOnly()
    {
        var input = new string('x', 1000);
        var result = EventLogService.TruncatePreservingSurrogates(input, 42);
        Assert.Equal(42, result.Length);
        Assert.Equal(new string('x', 42), result);
    }

    [Fact]
    public void Truncate_DropsLoneHighSurrogate_WhenCutoffLandsMidPair()
    {
        // Build a string whose character at index 4 is a HIGH surrogate and character at
        // index 5 is its paired LOW surrogate. Trimming to length 5 with plain Substring
        // would leave the high surrogate dangling — the helper must back up by one to keep
        // the string well-formed.
        // U+1F600 (😀) encodes as high=0xD83D, low=0xDE00.
        var emoji = "\uD83D\uDE00";              // one emoji, two chars
        var input = new string('a', 4) + emoji;  // "aaaa" + "\uD83D\uDE00" (length 6)

        var result = EventLogService.TruncatePreservingSurrogates(input, 5);

        // Expected: 4 chars ("aaaa"), because backing off one to avoid splitting the pair.
        Assert.Equal(4, result.Length);
        Assert.All(result, c => Assert.False(char.IsSurrogate(c), "truncated string must not contain any surrogate half"));
    }

    [Fact]
    public void Truncate_KeepsCompleteSurrogatePair_WhenCutoffLandsAfterPair()
    {
        // Emoji at the start, pure ASCII afterward — trimming to a length that lands past
        // the pair must keep the full pair intact.
        var input = "\uD83D\uDE00" + new string('a', 100);  // length 102
        var result = EventLogService.TruncatePreservingSurrogates(input, 10);
        Assert.Equal(10, result.Length);
        Assert.Equal('\uD83D', result[0]);
        Assert.Equal('\uDE00', result[1]);
        // And nothing suspicious in the tail.
        Assert.All(result.AsSpan(2).ToArray(), c => Assert.Equal('a', c));
    }

    [Fact]
    public void Truncate_HandlesEmptyAndNull()
    {
        Assert.Equal(string.Empty, EventLogService.TruncatePreservingSurrogates(string.Empty, 10));
        Assert.Null(EventLogService.TruncatePreservingSurrogates(null!, 10));
    }
}
