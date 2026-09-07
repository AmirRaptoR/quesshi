namespace Quesshi.Shared;

/// <summary>One row of <c>/admin/live</c>: everything an operator needs to recognise a duel and
/// judge whether it looks wedged, resolved server-side from <c>ILiveDirectory</c> so the browser
/// never has to know player ids.</summary>
public sealed record AdminLiveRowDto(string Id, string Code, string ChallengerName, string OpponentName,
    string Lang, int RoundIndex, int TotalRounds, string Phase, DateTimeOffset StartedAt);

public sealed record AdminLivePageDto(List<AdminLiveRowDto> Items, long Total);
