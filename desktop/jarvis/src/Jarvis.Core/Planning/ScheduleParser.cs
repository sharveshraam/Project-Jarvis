using System.Globalization;
using System.Text.RegularExpressions;
using Jarvis.Core.Models;

namespace Jarvis.Core.Planning;

public sealed record ParsedSchedule(DateTime DueLocal, RecurrenceKind Recurrence, string Text);

/// <summary>
/// Local natural-language schedule parsing ("tomorrow at 7 PM", "every Sunday", "in 20 minutes").
/// Works fully offline; Gemini is not involved.
/// </summary>
public static class ScheduleParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string Days = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";
    private const string Months =
        "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";

    private static readonly Regex RxEvery = new($@"\bevery\s+(day|weekdays?|week|month|morning|evening|night|{Days})\b", Opt);
    private static readonly Regex RxAdverb = new(@"\b(daily|weekly|monthly)\b", Opt);
    private static readonly Regex RxRelative = new(
        @"\bin\s+(\d+|an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|half an)\s*(minutes?|mins?|hours?|hrs?|days?|weeks?)\b", Opt);
    private static readonly Regex RxDayWord = new(@"\b(day after tomorrow|tomorrow|today|tonight)\b", Opt);
    private static readonly Regex RxWeekday = new($@"\b(?:(on|next|this)\s+)?({Days})\b", Opt);
    private static readonly Regex RxIsoDate = new(@"\b(?:on\s+)?(\d{4})-(\d{1,2})-(\d{1,2})\b", Opt);
    private static readonly Regex RxDayMonth = new($@"\b(?:on\s+)?(?:the\s+)?(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?({Months})\b", Opt);
    private static readonly Regex RxMonthDay = new($@"\b(?:on\s+)?({Months})\s+(\d{{1,2}})(?:st|nd|rd|th)?\b", Opt);
    private static readonly Regex RxTimeMeridiem = new(@"\b(?:at\s+|by\s+)?(\d{1,2})(?:[:.](\d{2}))?\s*(a\.?m\.?|p\.?m\.?)(?=\W|$)", Opt);
    private static readonly Regex RxTimeAt = new(@"\b(?:at|by)\s+(\d{1,2})(?:[:.](\d{2}))?\b", Opt);
    private static readonly Regex RxTimeClock = new(@"\b(\d{1,2}):(\d{2})\b", Opt);
    private static readonly Regex RxNamedTime = new(@"\b(?:at\s+)?(noon|midday|midnight)\b", Opt);
    private static readonly Regex RxPartOfDay = new(@"\b(?:in the\s+|this\s+)?(morning|afternoon|evening|night)\b", Opt);
    private static readonly Regex RxLeadIn = new(
        @"^\s*(please\s+)?((can|could|would)\s+you\s+)?(remind\s+me|set\s+(a|an)\s+(reminder|alarm)|add\s+(a\s+)?reminder|create\s+(a\s+)?reminder)\b", Opt);
    private static readonly Regex RxLeadingJoiner = new(@"^\s*(to|that|about|for)\s+", Opt);
    private static readonly Regex RxTrailingJoiner = new(@"\s+(to|at|on|by|for|in|and)\s*$", Opt);
    private static readonly Regex RxSpaces = new(@"\s{2,}", Opt);

    public static ParsedSchedule? Parse(string input, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = " " + input.Trim() + " ";

        var recurrence = RecurrenceKind.None;
        DayOfWeek? weekday = null;
        var weekdayIsNext = false;
        DateTime? date = null;
        TimeSpan? time = null;
        TimeSpan? defaultTime = null;
        DateTime? relative = null;
        var meridiemGiven = false;

        Match? m;
        if ((m = Take(ref s, RxEvery)) is not null)
        {
            var unit = m.Groups[1].Value.ToLowerInvariant();
            switch (unit)
            {
                case "day": recurrence = RecurrenceKind.Daily; break;
                case "weekday": case "weekdays": recurrence = RecurrenceKind.Weekdays; break;
                case "week": recurrence = RecurrenceKind.Weekly; break;
                case "month": recurrence = RecurrenceKind.Monthly; break;
                case "morning": recurrence = RecurrenceKind.Daily; defaultTime = new TimeSpan(9, 0, 0); break;
                case "evening": recurrence = RecurrenceKind.Daily; defaultTime = new TimeSpan(19, 0, 0); break;
                case "night": recurrence = RecurrenceKind.Daily; defaultTime = new TimeSpan(21, 0, 0); break;
                default: recurrence = RecurrenceKind.Weekly; weekday = ParseDay(unit); break;
            }
        }
        else if ((m = Take(ref s, RxAdverb)) is not null)
        {
            recurrence = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "daily" => RecurrenceKind.Daily,
                "weekly" => RecurrenceKind.Weekly,
                _ => RecurrenceKind.Monthly
            };
        }

        if ((m = Take(ref s, RxRelative)) is not null)
        {
            var amount = ParseAmount(m.Groups[1].Value);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            if (unit.StartsWith("min", StringComparison.Ordinal)) relative = now.AddMinutes(amount);
            else if (unit.StartsWith("h", StringComparison.Ordinal)) relative = now.AddHours(amount);
            else if (unit.StartsWith("d", StringComparison.Ordinal)) relative = now.AddDays(amount);
            else relative = now.AddDays(amount * 7);
        }

        if ((m = Take(ref s, RxDayWord)) is not null)
        {
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "tomorrow": date = now.Date.AddDays(1); break;
                case "day after tomorrow": date = now.Date.AddDays(2); break;
                case "tonight": date = now.Date; defaultTime ??= new TimeSpan(20, 0, 0); break;
                default: date = now.Date; break;
            }
        }
        else if ((m = Take(ref s, RxIsoDate)) is not null)
        {
            date = SafeDate(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        else if ((m = Take(ref s, RxDayMonth)) is not null)
        {
            date = CalendarDate(now, ParseMonth(m.Groups[2].Value), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        else if ((m = Take(ref s, RxMonthDay)) is not null)
        {
            date = CalendarDate(now, ParseMonth(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        else if (weekday is null && (m = Take(ref s, RxWeekday)) is not null)
        {
            weekday = ParseDay(m.Groups[2].Value);
            weekdayIsNext = m.Groups[1].Value.Equals("next", StringComparison.OrdinalIgnoreCase);
        }

        if ((m = Take(ref s, RxTimeMeridiem)) is not null)
        {
            var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var pm = m.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            if (hour is >= 1 and <= 12 && minute <= 59)
            {
                if (pm && hour != 12) hour += 12;
                if (!pm && hour == 12) hour = 0;
                time = new TimeSpan(hour, minute, 0);
                meridiemGiven = true;
            }
        }
        else if ((m = Take(ref s, RxTimeAt)) is not null || (m = Take(ref s, RxTimeClock)) is not null)
        {
            var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (hour <= 23 && minute <= 59) time = new TimeSpan(hour, minute, 0);
        }
        else if ((m = Take(ref s, RxNamedTime)) is not null)
        {
            time = m.Groups[1].Value.Equals("midnight", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : new TimeSpan(12, 0, 0);
            meridiemGiven = true;
        }

        if ((m = Take(ref s, RxPartOfDay)) is not null)
        {
            var part = m.Groups[1].Value.ToLowerInvariant();
            if (time is null)
            {
                defaultTime ??= part switch
                {
                    "morning" => new TimeSpan(9, 0, 0),
                    "afternoon" => new TimeSpan(15, 0, 0),
                    "evening" => new TimeSpan(19, 0, 0),
                    _ => new TimeSpan(21, 0, 0)
                };
            }
            else if (!meridiemGiven && time.Value.Hours < 12 && part != "morning")
            {
                time = time.Value.Add(TimeSpan.FromHours(12));
                meridiemGiven = true;
            }
        }

        DateTime due;
        if (relative.HasValue)
        {
            due = relative.Value;
        }
        else
        {
            var hasDay = date.HasValue || weekday.HasValue;
            var t = time ?? defaultTime;
            if (t is null)
            {
                if (!hasDay && recurrence == RecurrenceKind.None) return null;
                t = new TimeSpan(9, 0, 0);
            }

            var day = date ?? now.Date;
            if (!date.HasValue && weekday.HasValue)
            {
                var ahead = ((int)weekday.Value - (int)now.DayOfWeek + 7) % 7;
                if (ahead == 0 && weekdayIsNext) ahead = 7;
                day = now.Date.AddDays(ahead);
            }

            // "at 5" with no am/pm: prefer the afternoon/evening reading when the morning one is unlikely or already past.
            if (time.HasValue && !meridiemGiven && t.Value.Hours is >= 1 and < 12)
            {
                var morning = day + t.Value;
                var evening = morning.AddHours(12);
                if (t.Value.Hours <= 7 || (!hasDay && morning <= now && evening > now)) t = t.Value.Add(TimeSpan.FromHours(12));
            }

            due = day + t.Value;
            if (due <= now)
            {
                if (weekday.HasValue && !date.HasValue) due = due.AddDays(7);
                else if (!date.HasValue)
                {
                    due = recurrence switch
                    {
                        RecurrenceKind.Weekly => due.AddDays(7),
                        RecurrenceKind.Monthly => due.AddMonths(1),
                        _ => due.AddDays(1)
                    };
                }
            }
        }

        if (recurrence == RecurrenceKind.Weekdays)
        {
            while (due.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) due = due.AddDays(1);
        }

        return new ParsedSchedule(due, recurrence, CleanText(s));
    }

    private static Match? Take(ref string s, Regex rx)
    {
        var m = rx.Match(s);
        if (!m.Success) return null;
        s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
        return m;
    }

    private static string CleanText(string s)
    {
        var text = RxLeadIn.Replace(s, " ");
        text = RxSpaces.Replace(text, " ").Trim();
        for (var i = 0; i < 3; i++)
        {
            text = RxLeadingJoiner.Replace(text, "");
            text = RxTrailingJoiner.Replace(text, "");
        }
        text = text.Trim(' ', '.', ',', ';', ':', '!', '?', '-');
        if (text.Length == 0) return "Reminder";
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static double ParseAmount(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return t switch
        {
            "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7,
            "eight" => 8, "nine" => 9, "ten" => 10, "fifteen" => 15, "twenty" => 20, "thirty" => 30, "half an" => 0.5,
            _ => 1
        };
    }

    private static DayOfWeek ParseDay(string text) => text.Trim().ToLowerInvariant() switch
    {
        "monday" => DayOfWeek.Monday, "tuesday" => DayOfWeek.Tuesday, "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday, "friday" => DayOfWeek.Friday, "saturday" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday
    };

    private static int ParseMonth(string text)
    {
        var key = text.Trim().ToLowerInvariant();
        key = key.Length > 3 ? key.Substring(0, 3) : key;
        var index = Array.IndexOf(new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" }, key);
        return index < 0 ? 1 : index + 1;
    }

    private static DateTime? SafeDate(int year, int month, int day)
    {
        if (year < 2000 || year > 2200 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateTime(year, month, day);
    }

    private static DateTime? CalendarDate(DateTime now, int month, int day)
    {
        var date = SafeDate(now.Year, month, day);
        if (date.HasValue && date.Value < now.Date) date = SafeDate(now.Year + 1, month, day);
        return date;
    }
}

public static class Recurrence
{
    /// <summary>The first occurrence strictly after <paramref name="nowLocal"/>.</summary>
    public static DateTime Next(DateTime dueLocal, RecurrenceKind kind, DateTime nowLocal)
    {
        if (kind == RecurrenceKind.None) return dueLocal;
        var next = dueLocal;
        var guard = 0;
        do
        {
            next = kind switch
            {
                RecurrenceKind.Daily => next.AddDays(1),
                RecurrenceKind.Weekly => next.AddDays(7),
                RecurrenceKind.Monthly => next.AddMonths(1),
                _ => NextWeekday(next)
            };
        } while (next <= nowLocal && ++guard < 100000);
        return next;
    }

    private static DateTime NextWeekday(DateTime from)
    {
        var next = from.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }

    public static string Describe(RecurrenceKind kind) => kind switch
    {
        RecurrenceKind.Daily => "every day",
        RecurrenceKind.Weekdays => "every weekday",
        RecurrenceKind.Weekly => "every week",
        RecurrenceKind.Monthly => "every month",
        _ => "once"
    };
}

public static class TimeText
{
    public static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    public static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>"today 19:00", "tomorrow 09:00", "Sun 16 Nov 19:00".</summary>
    public static string Friendly(DateTime local, DateTime nowLocal)
    {
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (local.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            -1 => $"yesterday {time}",
            _ => local.ToString("ddd d MMM", CultureInfo.InvariantCulture) + " " + time
        };
    }

    /// <summary>Parses "HH:mm" (settings) into a time of day.</summary>
    public static bool TryParseClock(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time)
               && time < TimeSpan.FromDays(1);
    }
}
