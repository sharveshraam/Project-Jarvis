using Jarvis.Core.Models;
using Jarvis.Core.Planning;

namespace Jarvis.Tests;

public class ScheduleParserTests
{
    // Saturday 14 November 2026, 10:00 local time.
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    private static ParsedSchedule Parse(string text)
    {
        var parsed = ScheduleParser.Parse(text, Now);
        Assert.NotNull(parsed);
        return parsed!;
    }

    [Fact]
    public void Tomorrow_with_pm_time()
    {
        var parsed = Parse("remind me to submit the report tomorrow at 5 pm");
        Assert.Equal(new DateTime(2026, 11, 15, 17, 0, 0), parsed.DueLocal);
        Assert.Equal(RecurrenceKind.None, parsed.Recurrence);
        Assert.Equal("Submit the report", parsed.Text);
    }

    [Fact]
    public void Relative_minutes()
    {
        var parsed = Parse("remind me in 20 minutes to check the oven");
        Assert.Equal(Now.AddMinutes(20), parsed.DueLocal);
        Assert.Equal("Check the oven", parsed.Text);
    }

    [Fact]
    public void Relative_hours_written_as_a_word()
    {
        var parsed = Parse("remind me in two hours to call back");
        Assert.Equal(Now.AddHours(2), parsed.DueLocal);
        Assert.Equal("Call back", parsed.Text);
    }

    [Fact]
    public void Weekly_recurrence_on_a_named_day()
    {
        var parsed = Parse("remind me every Sunday at 7 pm to call mom");
        Assert.Equal(new DateTime(2026, 11, 15, 19, 0, 0), parsed.DueLocal);
        Assert.Equal(RecurrenceKind.Weekly, parsed.Recurrence);
        Assert.Equal("Call mom", parsed.Text);
    }

    [Fact]
    public void Daily_recurrence_whose_time_already_passed_starts_tomorrow()
    {
        var parsed = Parse("remind me to stretch every day at 9 am");
        Assert.Equal(new DateTime(2026, 11, 15, 9, 0, 0), parsed.DueLocal);
        Assert.Equal(RecurrenceKind.Daily, parsed.Recurrence);
        Assert.Equal("Stretch", parsed.Text);
    }

    [Fact]
    public void Weekday_recurrence_skips_the_weekend()
    {
        var parsed = Parse("remind me every weekday at 8 am to check email");
        Assert.Equal(new DateTime(2026, 11, 16, 8, 0, 0), parsed.DueLocal);
        Assert.Equal(DayOfWeek.Monday, parsed.DueLocal.DayOfWeek);
        Assert.Equal(RecurrenceKind.Weekdays, parsed.Recurrence);
    }

    [Fact]
    public void Bare_small_hour_is_read_as_afternoon()
    {
        var parsed = Parse("remind me at 5 to leave");
        Assert.Equal(new DateTime(2026, 11, 14, 17, 0, 0), parsed.DueLocal);
        Assert.Equal("Leave", parsed.Text);
    }

    [Fact]
    public void Iso_date_with_24_hour_time()
    {
        var parsed = Parse("remind me on 2026-12-01 at 09:30 to renew the licence");
        Assert.Equal(new DateTime(2026, 12, 1, 9, 30, 0), parsed.DueLocal);
        Assert.Equal("Renew the licence", parsed.Text);
    }

    [Fact]
    public void Day_without_time_defaults_to_nine()
    {
        var parsed = Parse("remind me tomorrow to water the plants");
        Assert.Equal(new DateTime(2026, 11, 15, 9, 0, 0), parsed.DueLocal);
        Assert.Equal("Water the plants", parsed.Text);
    }

    [Fact]
    public void Named_weekday_resolves_to_the_next_occurrence()
    {
        var parsed = Parse("remind me on monday at 10 am to send invoices");
        Assert.Equal(new DateTime(2026, 11, 16, 10, 0, 0), parsed.DueLocal);
        Assert.Equal("Send invoices", parsed.Text);
    }

    [Fact]
    public void Day_and_month_in_words()
    {
        var parsed = Parse("remind me on 3rd december at 4 pm to pay rent");
        Assert.Equal(new DateTime(2026, 12, 3, 16, 0, 0), parsed.DueLocal);
        Assert.Equal("Pay rent", parsed.Text);
    }

    [Fact]
    public void Tonight_defaults_to_eight_in_the_evening()
    {
        var parsed = Parse("remind me tonight to take out the bins");
        Assert.Equal(new DateTime(2026, 11, 14, 20, 0, 0), parsed.DueLocal);
    }

    [Theory]
    [InlineData("remind me to buy milk")]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_without_any_time_information_is_not_a_schedule(string text)
    {
        Assert.Null(ScheduleParser.Parse(text, Now));
    }

    [Fact]
    public void Next_occurrence_is_always_after_now()
    {
        var due = new DateTime(2026, 11, 14, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 15, 9, 0, 0), Recurrence.Next(due, RecurrenceKind.Daily, Now));
        Assert.Equal(new DateTime(2026, 11, 21, 9, 0, 0), Recurrence.Next(due, RecurrenceKind.Daily, new DateTime(2026, 11, 20, 10, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 21, 9, 0, 0), Recurrence.Next(due, RecurrenceKind.Weekly, Now));
        Assert.Equal(due, Recurrence.Next(due, RecurrenceKind.None, Now));
    }

    [Fact]
    public void Weekday_recurrence_moves_friday_to_monday()
    {
        var friday = new DateTime(2026, 11, 13, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0), Recurrence.Next(friday, RecurrenceKind.Weekdays, friday.AddHours(1)));
    }

    [Fact]
    public void Monthly_recurrence_clamps_to_short_months()
    {
        var due = new DateTime(2026, 1, 31, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 2, 28, 9, 0, 0), Recurrence.Next(due, RecurrenceKind.Monthly, new DateTime(2026, 2, 1, 0, 0, 0)));
    }

    [Theory]
    [InlineData("07:30", true, 7, 30)]
    [InlineData("7:05", true, 7, 5)]
    [InlineData("23:59", true, 23, 59)]
    [InlineData("25:00", false, 0, 0)]
    [InlineData("soon", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Clock_settings_are_parsed_strictly(string text, bool valid, int hour, int minute)
    {
        var ok = TimeText.TryParseClock(text, out var time);
        Assert.Equal(valid, ok);
        if (valid) Assert.Equal(new TimeSpan(hour, minute, 0), time);
    }

    [Fact]
    public void Friendly_times_are_relative_to_today()
    {
        Assert.Equal("today 19:00", TimeText.Friendly(new DateTime(2026, 11, 14, 19, 0, 0), Now));
        Assert.Equal("tomorrow 09:00", TimeText.Friendly(new DateTime(2026, 11, 15, 9, 0, 0), Now));
        Assert.Equal("yesterday 08:15", TimeText.Friendly(new DateTime(2026, 11, 13, 8, 15, 0), Now));
        Assert.Equal("Fri 20 Nov 08:05", TimeText.Friendly(new DateTime(2026, 11, 20, 8, 5, 0), Now));
    }

    [Fact]
    public void Local_and_utc_conversions_round_trip()
    {
        var local = new DateTime(2026, 11, 14, 10, 0, 0);
        Assert.Equal(local, TimeText.ToLocal(TimeText.ToUtc(local)));
    }
}
