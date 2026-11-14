using System.Globalization;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Planning;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

public sealed record Announcement(string Title, string Text, bool Speak);

/// <summary>
/// The persistent local scheduler: reminders (one-off and recurring), the morning briefing, the end-of-day
/// summary and focus-session time-ups. Everything is read from SQLite, so it works offline and reminders that
/// came due while JARVIS was not running are delivered at the next start.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private const string MorningKey = "briefing_morning_last";
    private const string EveningKey = "briefing_evening_last";
    private const string FocusKey = "focus_timeup_notified";
    private static readonly TimeSpan BriefingWindow = TimeSpan.FromHours(6);

    private readonly IPlannerStore _planner;
    private readonly IFocusStore _focus;
    private readonly ISettingsStore _settings;
    private readonly INotifier _notifier;
    private readonly IClock _clock;
    private readonly PlannerSignal _signal;
    private readonly ILogger<SchedulerService> _log;
    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
    private readonly object _gate = new object();
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;

    public SchedulerService(IPlannerStore planner, IFocusStore focus, ISettingsStore settings, INotifier notifier, IClock clock,
        PlannerSignal signal, ILogger<SchedulerService> log)
    {
        _planner = planner;
        _focus = focus;
        _settings = settings;
        _notifier = notifier;
        _clock = clock;
        _signal = signal;
        _log = log;
    }

    /// <summary>Raised for everything the scheduler delivers, so the UI can show (and speak) it.</summary>
    public event Action<Announcement>? Announced;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            _signal.Changed += Wake;
            _settings.Changed += OnSettingsChanged;
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Wake();

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunDue();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduler pass failed.");
            }

            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var next = _planner.NextDueUtc();
                if (next.HasValue)
                {
                    var until = next.Value - _clock.UtcNow;
                    if (until < delay) delay = until < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : until;
                }
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduler wait failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Delivers everything that is due right now. Returns how many announcements were made.</summary>
    public int RunDue()
    {
        var fired = 0;
        var nowUtc = _clock.UtcNow;
        var nowLocal = _clock.LocalNow;

        foreach (var reminder in _planner.DueReminders(nowUtc))
        {
            var dueLocal = TimeText.ToLocal(reminder.DueUtc);
            var late = nowUtc - reminder.DueUtc > TimeSpan.FromMinutes(2);
            var text = late ? $"{reminder.Text} (was due {TimeText.Friendly(dueLocal, nowLocal)})" : reminder.Text;

            reminder.LastFiredUtc = nowUtc;
            if (reminder.Recurrence == RecurrenceKind.None) reminder.Active = false;
            else reminder.DueUtc = TimeText.ToUtc(Recurrence.Next(dueLocal, reminder.Recurrence, nowLocal));
            _planner.UpdateReminder(reminder);

            Deliver(new Announcement("Reminder", text, true), NotificationKind.Info);
            fired++;
        }

        var settings = _settings.Current;
        var today = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (IsBriefingDue(settings.MorningBriefingTime, nowLocal) && _settings.GetValue(MorningKey) != today)
        {
            _settings.SetValue(MorningKey, today);
            var text = BriefingBuilder.MorningBriefing(settings.UserName, _planner.ListTodos(false), _planner.ListReminders(true), nowLocal);
            Deliver(new Announcement("Morning briefing", text, true), NotificationKind.Info);
            fired++;
        }

        if (IsBriefingDue(settings.EndOfDaySummaryTime, nowLocal) && _settings.GetValue(EveningKey) != today)
        {
            _settings.SetValue(EveningKey, today);
            var text = BriefingBuilder.EndOfDay(_planner.ListTodos(true), _planner.ListReminders(true), nowLocal);
            Deliver(new Announcement("End-of-day summary", text, false), NotificationKind.Info);
            fired++;
        }

        var session = _focus.Active();
        if (session is not null && nowUtc >= session.StartUtc.AddMinutes(session.PlannedMinutes))
        {
            var marker = session.Id.ToString(CultureInfo.InvariantCulture);
            if (_settings.GetValue(FocusKey) != marker)
            {
                _settings.SetValue(FocusKey, marker);
                Deliver(new Announcement("Focus session", $"Time is up for: {session.Goal}. Say 'end focus session' when you are ready to wrap up.", true),
                    NotificationKind.Info);
                fired++;
            }
        }
        return fired;
    }

    /// <summary>True from the configured time until a few hours after it, so a late start still gets the briefing but midnight does not.</summary>
    public static bool IsBriefingDue(string configured, DateTime nowLocal)
    {
        if (!TimeText.TryParseClock(configured, out var time)) return false;
        var sinceDue = nowLocal.TimeOfDay - time;
        return sinceDue >= TimeSpan.Zero && sinceDue <= BriefingWindow;
    }

    private void Deliver(Announcement announcement, NotificationKind kind)
    {
        try
        {
            _notifier.Notify(announcement.Title, announcement.Text, kind);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Notification could not be shown.");
        }
        Announced?.Invoke(announcement);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _signal.Changed -= Wake;
            _settings.Changed -= OnSettingsChanged;
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _cts.Dispose();
            _cts = null;
        }
    }
}
