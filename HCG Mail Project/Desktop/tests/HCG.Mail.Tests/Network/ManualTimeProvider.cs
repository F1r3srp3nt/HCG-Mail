namespace HCG.Mail.TestSupport;

/// <summary>A clock and timers that move only when a test calls <see cref="Advance"/> (shared by the engine and UI tests).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Time until the next timer fires, or null when none is pending.</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_timers)
                return _timers.Where(t => t.DueAt is not null).Select(t => t.DueAt!.Value - _now).OrderBy(d => d).Cast<TimeSpan?>().FirstOrDefault();
        }
    }

    public int PendingTimers
    {
        get
        {
            lock (_timers)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
                next = _timers.Where(t => t.DueAt is { } d && d <= target).OrderBy(t => t.DueAt).FirstOrDefault();
            if (next is null)
                break;
            if (next.DueAt!.Value > _now)
                _now = next.DueAt.Value; // a timer that fell due during a Jump fires now
            next.Fire();
        }
        _now = target;
    }

    /// <summary>Jumps the clock without firing anything in between, as when a computer sleeps.</summary>
    public void Jump(TimeSpan by) => _now += by;

    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;
            callback(state);
        }

        public void Dispose()
        {
            DueAt = null;
            lock (owner._timers)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
