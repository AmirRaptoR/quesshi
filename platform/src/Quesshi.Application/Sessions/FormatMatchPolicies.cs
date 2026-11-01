using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Sessions;

/// <summary>Reads each format's <see cref="MatchRule"/> and the platform's presence window.</summary>
public sealed class FormatMatchPolicies(IFormatRegistry formats) : IMatchPolicies
{
    public MatchPolicy? Find(string format) =>
        formats.TryGet(format, out var found) && found!.Spec.Matchmaking is { } rule
            ? new MatchPolicy(rule.RequireOnline, rule.Lifetime, PresenceTiming.OnlineWindow)
            : null;
}
