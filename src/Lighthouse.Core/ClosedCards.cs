namespace Lighthouse.Core;

/// <summary>
/// The flyout's cards that were closed with their ×. A closed card stays hidden for as long as it would still be
/// showing; once it goes away (the alert clears, the run ends, the check passes), the next one like it shows again.
/// Only for this run of Lighthouse: a card's "Don't show again" is the permanent kind.
/// </summary>
public sealed class ClosedCards
{
    private readonly HashSet<string> _closed = [];

    public void Close(string key) => _closed.Add(key);

    /// <summary>The cards still to show. Forgets closed keys that are no longer among the cards.</summary>
    public List<T> Visible<T>(IReadOnlyList<T> cards, Func<T, string> key)
    {
        _closed.IntersectWith(cards.Select(key));
        return cards.Where(c => !_closed.Contains(key(c))).ToList();
    }
}
