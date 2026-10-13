namespace Quesshi.Engine.Contracts;

/// <summary>Provides the single platform time source used for game decisions.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <summary>Reports whether the platform clock is safe to use for scored play.</summary>
public interface IClockHealth
{
    bool HasCalibration { get; }

    TimeSpan? Uncertainty { get; }

    TimeSpan? LastSuccessfulCalibrationAge { get; }

    bool IsHealthy { get; }
}

/// <summary>Controllable clock for deterministic engine and contract tests.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; private set; } = now;

    public void Set(DateTimeOffset now) => Now = now;

    public void Advance(TimeSpan duration) => Now = Now.Add(duration);
}

/// <summary>Lets the host decide readiness without depending on a particular clock implementation.</summary>
public interface IClockReadiness
{
    bool IsReady { get; }
}

public sealed class ClockReadiness(IClockHealth health) : IClockReadiness
{
    public bool IsReady => health.IsHealthy;
}

/// <summary>Allows the engine to gate a scored command before it changes game state.</summary>
public interface IScoredCommandAdmission
{
    ScoredCommandAdmission Admit();
}

public sealed class ClockAdmission(IClockHealth health) : IScoredCommandAdmission
{
    public ScoredCommandAdmission Admit() => health.IsHealthy
        ? ScoredCommandAdmission.Allowed
        : ScoredCommandAdmission.ClockUnavailable;
}

public sealed record ScoredCommandAdmission(bool IsAllowed, string? RefusalCode)
{
    public static ScoredCommandAdmission Allowed { get; } = new(true, null);

    public static ScoredCommandAdmission ClockUnavailable { get; } = new(false, "clock_unavailable");
}
