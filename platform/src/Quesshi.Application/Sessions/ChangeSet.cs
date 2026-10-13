namespace Quesshi.Application.Sessions;

/// <summary>One ordered transition log, shaped like the rows the persistence layer will write.</summary>
public sealed record RowChange(string Table, string Key, object Row);

public sealed record ChangeSet(IReadOnlyList<RowChange> Changes)
{
    public static ChangeSet Empty { get; } = new([]);

    public ChangeSet Add(string table, string key, object row) => new(Changes.Concat([new RowChange(table, key, row)]).ToArray());
}
