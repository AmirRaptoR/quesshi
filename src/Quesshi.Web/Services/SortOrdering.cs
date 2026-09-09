namespace Quesshi.Web.Services;

/// <summary>
/// A sorting question's answer while the player is still building it: which served item sits in
/// which place, and the string that gets submitted for it.
/// <para>
/// The card serves its items already shuffled (<c>Question.ServedChoices</c>), so the only thing a
/// player can talk about is <b>served positions</b> — where on the card each item appeared. This
/// holds exactly that: <c>Placed[p]</c> is the served position of whatever the player has put in
/// place <c>p</c>, first to last. <see cref="Response"/> is that list joined with commas, which is
/// precisely what <c>AnswerDto.Response</c> means for a sort: <c>"2,0,3,1"</c> is "the item you
/// showed me third goes first".
/// </para>
/// <para>
/// The other reading of that string — "served item 0 belongs in place 2" — is the inverse
/// permutation, and an implementer who picks it submits every answer backwards while a test written
/// with the same misreading passes happily. <c>SortOrder.ToStoredOrder</c> on the server documents
/// the same trap from the other side; this is the client half of that one agreement, which is why it
/// is a class with a name rather than four lines inlined in a component.
/// </para>
/// <para>
/// Immutable, so every move is a value a test can compare against rather than a mutation to observe.
/// A four-item list rebuilt on each arrow press costs nothing worth measuring.
/// </para>
/// </summary>
public sealed class SortOrdering
{
    private readonly int[] _placed;

    private SortOrdering(int[] placed) => _placed = placed;

    /// <summary>Served positions in the order the player has placed them, first to last.</summary>
    public IReadOnlyList<int> Placed => _placed;

    public int Count => _placed.Length;

    /// <summary>
    /// The untouched starting point: the items exactly as the card served them. A player who submits
    /// without moving anything sends <c>"0,1,2,3"</c>, which is a real answer — "the order you showed
    /// me is already right" — and not a null one.
    /// </summary>
    public static SortOrdering Served(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "A card cannot serve fewer than no items.");

        var placed = new int[count];
        for (var i = 0; i < count; i++) placed[i] = i;
        return new SortOrdering(placed);
    }

    /// <summary>Which served item the player has put in place <paramref name="place"/>.</summary>
    public int ServedAt(int place) => _placed[place];

    /// <summary>
    /// Take the row out of <paramref name="from"/> and drop it in at <paramref name="to"/>, sliding
    /// everything between them along by one. This is one operation for both input paths on purpose:
    /// a drag is a move to wherever the finger is, and an arrow key is a move to the next place up or
    /// down, so the keyboard cannot end up somewhere a drag could not reach and neither can drift
    /// from the other.
    /// <para>
    /// Out-of-range destinations are clamped rather than rejected: a finger dragged past the end of
    /// the list means "put it last", which is the useful reading, and a stray index from a pointer
    /// event that raced a re-render must never throw on the answering path.
    /// </para>
    /// </summary>
    public SortOrdering Move(int from, int to)
    {
        if (from < 0 || from >= Count) return this;

        to = Math.Clamp(to, 0, Count - 1);
        if (to == from) return this;

        var moved = _placed[from];
        var next = new int[Count];

        // Copy every other entry in order, leaving the destination free for the moved one. Written
        // as one pass rather than a remove-then-insert so there is no intermediate shorter list to
        // get an index wrong against.
        for (int read = 0, write = 0; read < Count; read++)
        {
            if (read == from) continue;
            if (write == to) write++;
            next[write++] = _placed[read];
        }

        next[to] = moved;
        return new SortOrdering(next);
    }

    /// <summary>Arrow-up: the row swaps with the one above it, or stays put at the top.</summary>
    public SortOrdering MoveUp(int place) => Move(place, place - 1);

    /// <summary>Arrow-down: the row swaps with the one below it, or stays put at the bottom.</summary>
    public SortOrdering MoveDown(int place) => Move(place, place + 1);

    /// <summary>
    /// The answer as it goes on the wire. Invariant by construction — these are small non-negative
    /// integers formatted by <see cref="string.Join{T}(char, IEnumerable{T})"/>, so no culture is
    /// involved and Persian digits can never reach the server. <c>SortOrder.TryParseOrder</c> on the
    /// far side rejects them explicitly anyway; that belt and this brace are cheap.
    /// </summary>
    public string Response => string.Join(',', _placed);
}

/// <summary>
/// The one measurement-dependent decision in a drag, kept out of the component so it can be tested
/// without a browser: given where each row currently sits on screen and where the finger is, which
/// place is the finger over?
/// <para>
/// The midpoints come from JavaScript (<c>quesshi.sort.midpoints</c>) because element geometry is
/// the one thing C# genuinely cannot see. Everything decided <i>from</i> that geometry lives here.
/// </para>
/// </summary>
public static class SortDrag
{
    /// <summary>
    /// The place whose row centre the pointer is nearest. Nearest-centre rather than "crossed an
    /// edge" because it degrades gracefully: a finger dragged well past the end of the list still
    /// resolves to the last place instead of to nothing, and a list whose rows are not all the same
    /// height still picks the row a person would say they were pointing at.
    /// <para>
    /// Returns -1 for an empty list, which callers read as "there is nothing to drop onto".
    /// </para>
    /// </summary>
    public static int TargetPlace(IReadOnlyList<double> midpoints, double pointerY)
    {
        if (midpoints.Count == 0) return -1;

        var best = 0;
        var bestDistance = Math.Abs(midpoints[0] - pointerY);

        for (var i = 1; i < midpoints.Count; i++)
        {
            var distance = Math.Abs(midpoints[i] - pointerY);

            // Strictly less, so a tie between two rows keeps the higher one and a pointer sitting
            // exactly between two rows does not flicker between them on every move event.
            if (distance >= bestDistance) continue;

            best = i;
            bestDistance = distance;
        }

        return best;
    }
}
