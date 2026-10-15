using System.Diagnostics;
using Dapper;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Clock;

/// <summary>Reads PostgreSQL's wall clock for calibration samples.</summary>
public interface IDatabaseClock
{
    Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provides monotonic timestamps for clock calibration.</summary>
public interface IMonotonicClock
{
    long Timestamp { get; }

    long Frequency { get; }
}

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public long Timestamp => Stopwatch.GetTimestamp();

    public long Frequency => Stopwatch.Frequency;
}

/// <summary>Production PostgreSQL timestamp reader.</summary>
public sealed class PostgresDatabaseClock(DatabaseSources sources) : IDatabaseClock
{
    public async Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await sources.OpenApplicationConnectionAsync(cancellationToken);
        var utc = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", cancellationToken: cancellationToken));
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}

/// <summary>Disciplines platform time to PostgreSQL without relying on a process wall clock.</summary>
public sealed class PostgresClock : IClock, IClockHealth, IAsyncDisposable
{
    public static readonly TimeSpan CalibrationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumHealthyUncertainty = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaximumHealthyAge = TimeSpan.FromMinutes(5);

    private const int SamplesPerCalibration = 5;
    private readonly IDatabaseClock _databaseClock;
    private readonly IMonotonicClock _monotonicClock;
    private readonly TimeSpan _calibrationInterval;
    private readonly long _origin;
    private readonly SemaphoreSlim _calibrationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateLock = new();
    private TimeSpan? _offset;
    private TimeSpan? _uncertainty;
    private long? _lastSuccessfulCalibration;
    private DateTimeOffset _lastNow = DateTimeOffset.MinValue;
    private Task? _periodicCalibration;
    private int _disposed;

    public PostgresClock(IDatabaseClock databaseClock, IMonotonicClock? monotonicClock = null, TimeSpan? calibrationInterval = null)
    {
        _databaseClock = databaseClock;
        _monotonicClock = monotonicClock ?? new StopwatchMonotonicClock();
        _calibrationInterval = calibrationInterval ?? CalibrationInterval;
        if (_calibrationInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(calibrationInterval));
        if (_monotonicClock.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicClock));
        _origin = _monotonicClock.Timestamp;
    }

    public DateTimeOffset Now
    {
        get
        {
            lock (_stateLock)
            {
                var offset = _offset ?? throw new InvalidOperationException("The PostgreSQL clock has not calibrated successfully.");
                var candidate = DateTimeOffset.UnixEpoch.Add(ElapsedAt(_monotonicClock.Timestamp)).Add(offset);
                // A recalibration may lower the offset; never let readings go backward.
                if (candidate < _lastNow) candidate = _lastNow;
                _lastNow = candidate;
                return candidate;
            }
        }
    }

    public bool HasCalibration
    {
        get
        {
            lock (_stateLock) return _lastSuccessfulCalibration is not null;
        }
    }

    public TimeSpan? Uncertainty
    {
        get
        {
            lock (_stateLock) return _uncertainty;
        }
    }

    public TimeSpan? LastSuccessfulCalibrationAge
    {
        get
        {
            long? lastSuccess;
            lock (_stateLock) lastSuccess = _lastSuccessfulCalibration;
            return lastSuccess is { } timestamp ? ElapsedBetween(timestamp, _monotonicClock.Timestamp) : null;
        }
    }

    public bool IsHealthy
    {
        get
        {
            TimeSpan? uncertainty;
            long? lastSuccess;
            lock (_stateLock)
            {
                uncertainty = _uncertainty;
                lastSuccess = _lastSuccessfulCalibration;
            }

            return uncertainty is { } u && u <= MaximumHealthyUncertainty
                && lastSuccess is { } timestamp
                && ElapsedBetween(timestamp, _monotonicClock.Timestamp) <= MaximumHealthyAge;
        }
    }

    /// <summary>Runs the required startup calibration and begins 30-second recalibration.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await CalibrateAsync(cancellationToken);
        lock (_stateLock)
        {
            _periodicCalibration ??= PeriodicallyCalibrateAsync(_shutdown.Token);
        }
    }

    /// <summary>Attempts five readings. A failed attempt preserves the prior good calibration.</summary>
    public async Task<bool> CalibrateAsync(CancellationToken cancellationToken = default)
    {
        await _calibrationLock.WaitAsync(cancellationToken);
        try
        {
            var samples = new CalibrationSample[SamplesPerCalibration];
            for (var index = 0; index < samples.Length; index++)
            {
                var started = _monotonicClock.Timestamp;
                var databaseNow = await _databaseClock.ReadAsync(cancellationToken);
                var completed = _monotonicClock.Timestamp;
                var roundTrip = ElapsedBetween(started, completed);
                var midpoint = ElapsedAt(started).Add(roundTrip / 2);
                samples[index] = new CalibrationSample(databaseNow - DateTimeOffset.UnixEpoch - midpoint, roundTrip);
            }

            var best = samples.MinBy(sample => sample.RoundTrip);
            lock (_stateLock)
            {
                _offset = best.Offset;
                _uncertainty = best.RoundTrip / 2;
                _lastSuccessfulCalibration = _monotonicClock.Timestamp;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            _calibrationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
        Task? periodicCalibration;
        lock (_stateLock) periodicCalibration = _periodicCalibration;
        if (periodicCalibration is not null)
        {
            try
            {
                await periodicCalibration;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _calibrationLock.Dispose();
    }

    private async Task PeriodicallyCalibrateAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_calibrationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CalibrateAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan ElapsedAt(long timestamp) => ElapsedBetween(_origin, timestamp);

    private TimeSpan ElapsedBetween(long earlier, long later) => TimeSpan.FromSeconds((later - earlier) / (double)_monotonicClock.Frequency);

    private readonly record struct CalibrationSample(TimeSpan Offset, TimeSpan RoundTrip);
}
