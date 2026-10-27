namespace Lighthouse.Core;

/// <summary>
/// The flyout's cards that were closed with their ×. A closed card stays closed until Windows restarts: it doesn't
/// come back when its alert clears and fires again, or when Lighthouse restarts (an update), because the keys are
/// saved with the time Windows started. A health check's "Don't show again" is the permanent kind.
/// </summary>
public sealed class ClosedCards(IEnumerable<string>? saved = null)
{
    /// <summary>Enough for weeks of job runs and games, which each close as their own card.</summary>
    public const int Most = 300;

    private readonly List<string> _closed = [.. (saved ?? []).Distinct().TakeLast(Most)];

    /// <summary>The closed keys, oldest first, for saving.</summary>
    public IReadOnlyList<string> Keys => _closed;

    public void Close(string key)
    {
        _closed.Remove(key);
        _closed.Add(key);
        if (_closed.Count > Most) _closed.RemoveAt(0);
    }

    /// <summary>The cards still to show.</summary>
    public List<T> Visible<T>(IReadOnlyList<T> cards, Func<T, string> key) => cards.Where(c => !_closed.Contains(key(c))).ToList();

    /// <summary>The keys saved last time, if Windows hasn't restarted since (the start times agree to a couple of minutes).</summary>
    public static IEnumerable<string> Restore(IEnumerable<string> keys, DateTime? savedBoot, DateTime boot) =>
        savedBoot is { } saved && Math.Abs((saved - boot).TotalMinutes) < 2 ? keys : [];

    /// <summary>When Windows started, from how long it has been running.</summary>
    public static DateTime BootTime(DateTime now, long uptimeMilliseconds) => now - TimeSpan.FromMilliseconds(uptimeMilliseconds);
}
