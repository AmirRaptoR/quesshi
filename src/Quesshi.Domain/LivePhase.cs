namespace Quesshi.Domain;

/// <summary>Where a live duel sits in its round cycle. <see cref="MatchState"/> tracks the lifecycle
/// (waiting, in progress, finished); this tracks the beat within it.</summary>
public enum LivePhase { Lobby, Countdown, Question, Reveal, Over }
