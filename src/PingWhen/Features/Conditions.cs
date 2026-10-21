using System.Text.RegularExpressions;

namespace PingWhen.Features;

/// <summary>
/// Pure steps: (state, sample, now) → (state, fired). Times are Environment.TickCount64 milliseconds.
/// A gap longer than <see cref="HoleMs"/> between samples (sleep, a stalled tick) restarts the timers, never fires.
/// </summary>
public static class Conditions
{
    public const long HoleMs = 5000;
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Quiet: armed once the value was at or above the threshold (or "already running"), fires after N s below it.</summary>
    public record struct QuietState(bool Armed, long? BelowSince, long LastAt);

    public static (QuietState, bool) Quiet(QuietState s, double? value, double below, int seconds, long now)
    {
        var hole = s.LastAt != 0 && now - s.LastAt > HoleMs;
        s.LastAt = now;
        if (value is null || hole) return (s with { BelowSince = null }, false);
        if (value >= below) return (s with { Armed = true, BelowSince = null }, false);
        if (!s.Armed) return (s, false);
        s.BelowSince ??= now;
        return (s, now - s.BelowSince >= seconds * 1000L);
    }

    /// <summary>Stopped growing: fires N s after the last growth, only once growth was seen. A smaller size restarts the timer.</summary>
    public record struct GrowthState(long Size, bool GrowthSeen, long? StableSince, long LastAt);

    public static (GrowthState, bool) Growth(GrowthState s, long size, int seconds, long now)
    {
        var hole = s.LastAt != 0 && now - s.LastAt > HoleMs;
        s.LastAt = now;
        if (size != s.Size)
        {
            if (size > s.Size) s.GrowthSeen = true;
            return (s with { Size = size, StableSince = null }, false);
        }
        if (!s.GrowthSeen) return (s, false);
        if (hole) return (s with { StableSince = now }, false);
        s.StableSince ??= now;
        return (s, now - s.StableSince >= seconds * 1000L);
    }

    /// <summary>Window: null title = window gone, which fires for every condition ("closed"). Flashed fires from ShellHook.</summary>
    public static bool Window(Until until, string? title, string? initialTitle, string? pattern, bool isRegex) => title is null || until switch
    {
        Until.TitleChanged => title != initialTitle,
        Until.TitleMatches => Matches(title, pattern, isRegex),
        _ => false,
    };

    /// <summary>
    /// Region (v1.0): frames arrive only when the window changes, so "unchanged for N s" is checked on the tick.
    /// The first frame is the baseline; a frame differing from the last kept one by more than the threshold is a change.
    /// </summary>
    public record struct RegionState(byte[]? Base, byte[]? Last, bool ChangeSeen, long ChangedAt);

    public static (RegionState, bool Changed) RegionFrame(RegionState s, byte[] gray, double threshold, long now)
    {
        if (s.Last is null) return (s with { Base = gray, Last = gray, ChangedAt = now }, false);
        var changed = Frames.Diff(s.Last, gray) > threshold;
        return (changed ? s with { Last = gray, ChangeSeen = true, ChangedAt = now } : s, changed);
    }

    /// <summary>Stopped changing: N s since the last change, once a change was seen (a static region alone never fires).</summary>
    public static bool RegionUnchanged(RegionState s, int seconds, long now) => s.ChangeSeen && now - s.ChangedAt >= seconds * 1000L;

    /// <summary>Changed: the current frame differs from the first one.</summary>
    public static bool RegionChanged(RegionState s, byte[] gray, double threshold) => s.Base is not null && Frames.Diff(s.Base, gray) > threshold;

    /// <summary>Contains, ignoring case; or a regex with a 100 ms timeout (agents send patterns too). A timeout is no match.</summary>
    public static bool Matches(string text, string? pattern, bool isRegex)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (!isRegex) return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Null when the pattern is fine, else the parser's message.</summary>
    public static string? RegexError(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.None, RegexTimeout); return null; }
        catch (ArgumentException e) { return e.Message; }
    }
}
