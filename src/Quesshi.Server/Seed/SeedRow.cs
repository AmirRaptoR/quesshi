namespace Quesshi.Server.Seed;

/// <summary>One row of a seed question file. Short names because a human maintains these by hand.</summary>
public sealed record SeedRow(string Cat, int Level, string Q, List<string> C, int A, string? E, SeedMedia? Media);
