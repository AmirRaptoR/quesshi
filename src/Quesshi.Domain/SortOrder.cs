using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quesshi.Domain;

/// <summary>
/// The one place that decides how a sorting question's items are shuffled for a given round, and
/// the only place allowed to.
/// <para>
/// Cards are built in three places and each duel kind's submission path is a fourth and fifth
/// caller, since submission is where the shuffle is inverted. If any of them derived the order
/// differently, a reconnect or a silo restart could show a player one arrangement and grade them
/// against another — which reads, to the player, as the game marking a right answer wrong. So the
/// permutation is a pure function of <c>(matchId, slot)</c>: same inputs, same order, on any silo,
/// in any process, for as long as the duel exists.
/// </para>
/// <para>
/// Seeded by the match and the slot rather than by the question id, because everyone in a live duel
/// must see the same arrangement, and seeding by the question alone would hand a returning player
/// the very order they saw last time.
/// </para>
/// </summary>
public sealed class SortOrder
{
    private readonly int[] _served;
    private readonly int[] _inverse;

    private SortOrder(int[] served, int[] inverse)
    {
        _served = served;
        _inverse = inverse;
    }

    /// <summary>
    /// The served permutation: <c>Served[p]</c> is the stored index of the item shown at served
    /// position <c>p</c>. This is what a card builder walks to lay the items out.
    /// </summary>
    public IReadOnlyList<int> Served => _served;

    /// <summary>
    /// The inverse: <c>Inverse[s]</c> is the served position the item stored at index <c>s</c> was
    /// shown in. This is what turns the correct stored order back into "where on the card the right
    /// answer was", for a reveal that has to point at what the player actually saw.
    /// </summary>
    public IReadOnlyList<int> Inverse => _inverse;

    public int Count => _served.Length;

    /// <inheritdoc cref="Served"/>
    public int StoredIndexAt(int servedPosition) => _served[servedPosition];

    /// <inheritdoc cref="Inverse"/>
    public int ServedPositionOf(int storedIndex) => _inverse[storedIndex];

    /// <summary>
    /// The permutation for one round. Deterministic Fisher–Yates over a seed derived from the match
    /// id and the slot with SHA-256.
    /// <para>
    /// Neither half of that is incidental. <see cref="Random.Shared"/> is per-process and would give
    /// two silos two different orders for the same round. <c>string.GetHashCode</c> is randomised
    /// per process on .NET Core, so it would give the <i>same</i> silo a different order after a
    /// restart — and a player who reconnects mid-round would then be graded against an arrangement
    /// nobody ever showed them. SHA-256 of the concatenation is stable everywhere and forever, which
    /// is the only property this needs; it is not being used for secrecy.
    /// </para>
    /// </summary>
    public static SortOrder For(string matchId, int slot, int count = MatchRules.ChoicesPerQuestion)
    {
        if (string.IsNullOrEmpty(matchId))
            throw new ArgumentException("A sort order needs a match id to seed it.", nameof(matchId));
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot), slot, "A slot cannot be negative.");
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "There is nothing to shuffle.");

        // The separator matters: without it ("m1" , 23) and ("m12", 3) would hash the same bytes and
        // share a permutation. It cannot appear in an id, so the encoding is unambiguous.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{matchId}|{slot}"));
        var state = BinaryPrimitives.ReadUInt64BigEndian(digest);

        var served = new int[count];
        for (var i = 0; i < count; i++) served[i] = i;

        // Fisher-Yates, high to low. The modulo is very slightly biased towards the low end of the
        // range; at a 64-bit stream and four items that bias is around 2^-62 of a card, and a
        // rejection loop would only add a branch that changes the pinned output for no player-visible
        // gain.
        for (var i = count - 1; i > 0; i--)
        {
            var j = (int)(NextUInt64(ref state) % (ulong)(i + 1));
            (served[i], served[j]) = (served[j], served[i]);
        }

        var inverse = new int[count];
        for (var p = 0; p < count; p++) inverse[served[p]] = p;

        return new SortOrder(served, inverse);
    }

    /// <summary>
    /// Lays a stored list out in served order — the shuffled items a card carries.
    /// </summary>
    public IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> stored)
    {
        if (stored.Count != Count)
            throw new ArgumentException($"This order shuffles {Count} items, got {stored.Count}.", nameof(stored));

        var shuffled = new T[Count];
        for (var p = 0; p < Count; p++) shuffled[p] = stored[_served[p]];
        return shuffled;
    }

    /// <summary>
    /// Turns what the player submitted into what gets stored. <paramref name="servedPositions"/> is
    /// the answer as the player gave it: served positions in the order they placed them, first to
    /// last — <c>"2,0,3,1"</c> means "the item you showed me third goes first". The result is the
    /// same answer in stored-index terms.
    /// <para>
    /// The other reading of that string — "served item 0 belongs at position 2" — is the inverse
    /// permutation, and an implementer who picks it grades every answer backwards while a test
    /// written with the same reading passes happily. Hence one helper, one direction, here.
    /// </para>
    /// <para>
    /// Normalising once, at submission, is what keeps the seed's callers down to the card builders
    /// and the submission paths: correctness becomes "is this the identity order", and no reveal or
    /// history mapper ever needs the seed at all.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> ToStoredOrder(IReadOnlyList<int> servedPositions)
    {
        if (!IsPermutation(servedPositions, Count))
            throw new ArgumentException($"Expected a permutation of 0..{Count - 1}.", nameof(servedPositions));

        var stored = new int[Count];
        for (var i = 0; i < Count; i++) stored[i] = _served[servedPositions[i]];
        return stored;
    }

    /// <summary>
    /// Reads an answer string — <c>"2,0,3,1"</c> — into indices, or fails. Invariant culture and
    /// <see cref="NumberStyles.None"/>, so Persian digits from a Persian browser are rejected rather
    /// than misread, and every caller that touches a sort answer parses it the same way.
    /// </summary>
    public static bool TryParseOrder(string? response, int count, out int[] order)
    {
        order = [];
        if (string.IsNullOrWhiteSpace(response)) return false;

        var parts = response.Split(',');
        if (parts.Length != count) return false;

        var parsed = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        if (!IsPermutation(parsed, count)) return false;

        order = parsed;
        return true;
    }

    /// <summary>The wire form of an order, matching what <see cref="TryParseOrder"/> reads.</summary>
    public static string FormatOrder(IReadOnlyList<int> order)
        => string.Join(',', order.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Whether an order is the stored order itself. Once submission has normalised a sort answer
    /// into stored-index terms, this is the whole of "correct". It says nothing about the length —
    /// that is <see cref="TryParseOrder"/>'s job, and every caller here goes through it first.
    /// </summary>
    public static bool IsIdentity(IReadOnlyList<int> order)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] != i) return false;

        return true;
    }

    /// <summary>An order is only an answer if it uses every index exactly once.</summary>
    public static bool IsPermutation(IReadOnlyList<int> order, int count)
    {
        if (order.Count != count) return false;

        var seen = new bool[count];
        foreach (var index in order)
        {
            if (index < 0 || index >= count || seen[index]) return false;
            seen[index] = true;
        }

        return true;
    }

    /// <summary>SplitMix64: a fixed, tiny, fully specified generator, so the algorithm this class
    /// pins can be reimplemented anywhere from these four lines if it ever needs to be.</summary>
    private static ulong NextUInt64(ref ulong state)
    {
        unchecked
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
