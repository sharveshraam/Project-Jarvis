using Jarvis.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Core.Scheduling;

/// <summary>Reminder scheduler. Polls the store on a modest interval (30s) and
/// fires due reminders via callback. On startup, reminders that became due while
/// JARVIS was closed are fired immediately (recovery). Works fully offline.</summary>
public sealed class ReminderScheduler(
    IReminderStore store,
    Func<Reminder, Task> onDue,
    ILogger<ReminderScheduler>? logger = null)
{
    private readonly ILogger<ReminderScheduler> _log = logger ?? NullLogger<ReminderScheduler>.Instance;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckDueAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Reminder scheduler tick failed"); }
        }
    }

    public async Task<IReadOnlyList<Reminder>> CheckDueAsync(CancellationToken ct = default)
    {
        var due = new List<Reminder>();
        foreach (var r in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (r.IsCompleted || r.DueAt > DateTimeOffset.Now) continue;
            due.Add(r);
            await onDue(r).ConfigureAwait(false);
            if (r.Kind == ReminderKind.OneTime)
            {
                await store.MarkCompletedAsync(r.Id, ct).ConfigureAwait(false);
            }
            else
            {
                var next = NextOccurrence(r, DateTimeOffset.Now);
                await store.SaveAsync(r with { DueAt = next }, ct).ConfigureAwait(false);
            }
        }
        return due;
    }

    public static DateTimeOffset NextOccurrence(Reminder r, DateTimeOffset from)
    {
        return r.Kind switch
        {
            ReminderKind.Daily => from.AddDays(1),
            ReminderKind.Weekly => from.AddDays(7),
            _ => from.AddDays(1)
        };
    }

    /// <summary>Parse simple natural-language times: "tomorrow at 7pm", "in 2 hours", "today 9am".</summary>
    public static DateTimeOffset? TryParseDue(string text, DateTimeOffset now)
    {
        var t = text.ToLowerInvariant();
        // "in N hour(s)/minute(s)"
        var inMatch = System.Text.RegularExpressions.Regex.Match(t, @"in\s+(\d+)\s*(hour|minute|min|day)");
        if (inMatch.Success)
        {
            var n = int.Parse(inMatch.Groups[1].Value);
            return inMatch.Groups[2].Value switch
            {
                "hour" => now.AddHours(n),
                "minute" or "min" => now.AddMinutes(n),
                "day" => now.AddDays(n),
                _ => null
            };
        }
        var timeMatch = System.Text.RegularExpressions.Regex.Match(t, @"(\d{1,2})(?::(\d{2}))?\s*(am|pm)?");
        var date = t.Contains("tomorrow") ? now.AddDays(1).Date
            : t.Contains("today") ? now.Date
            : now.Date;
        if (timeMatch.Success)
        {
            var hour = int.Parse(timeMatch.Groups[1].Value);
            var minute = timeMatch.Groups[2].Success ? int.Parse(timeMatch.Groups[2].Value) : 0;
            var meridiem = timeMatch.Groups[3].Value;
            if (meridiem == "pm" && hour < 12) hour += 12;
            if (meridiem == "am" && hour == 12) hour = 0;
            var due = date.AddHours(hour).AddMinutes(minute);
            if (due <= now && !t.Contains("tomorrow")) due = due.AddDays(1);
            return due;
        }
        return null;
    }
}
