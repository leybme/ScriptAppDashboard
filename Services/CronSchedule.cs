using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScriptAppDashboard.Services;

/// <summary>
/// A 5-field cron expression (minute hour day-of-month month day-of-week) in local time.
/// Supports <c>*</c>, lists, ranges, steps (<c>*/15</c>, <c>1-10/2</c>), month / weekday names and <c>@daily</c>-style shortcuts.
/// As in classic cron, when both day-of-month and day-of-week are restricted a day matches if either does.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7]; // 0 = Sunday
    private bool _anyDay, _anyWeekday;

    private static readonly string[] MonthNames = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
    private static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    private CronSchedule() { }

    /// <summary>Parses an expression; returns null and an error message when it is not valid.</summary>
    public static CronSchedule? TryParse(string? text, out string error)
    {
        error = "";
        text = (text ?? "").Trim();
        text = text.ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "@yearly" or "@annually" => "0 0 1 1 *",
            _ => text,
        };
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = "A cron expression has 5 fields: minute hour day-of-month month day-of-week.";
            return null;
        }
        var cron = new CronSchedule();
        try
        {
            Fill(parts[0], 0, 59, null, cron._minutes);
            Fill(parts[1], 0, 23, null, cron._hours);
            Fill(parts[2], 1, 31, null, cron._days);
            Fill(parts[3], 1, 12, MonthNames, cron._months, nameBase: 1);
            Fill(parts[4], 0, 7, DayNames, cron._weekdays, nameBase: 0, wrapSeven: true);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
        cron._anyDay = parts[2].StartsWith('*');
        cron._anyWeekday = parts[4].StartsWith('*');
        return cron;
    }

    private static void Fill(string field, int min, int max, string[]? names, bool[] set, int nameBase = 0, bool wrapSeven = false)
    {
        foreach (var item in field.Split(','))
        {
            var stepParts = item.Split('/');
            if (stepParts.Length > 2) throw new FormatException($"“{field}” is not valid.");
            var step = 1;
            if (stepParts.Length == 2 && (!int.TryParse(stepParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
                throw new FormatException($"“{item}” has an invalid step.");

            int from, to;
            var range = stepParts[0];
            if (range == "*")
            {
                from = min;
                to = wrapSeven ? 6 : max;
            }
            else
            {
                var ends = range.Split('-');
                if (ends.Length > 2) throw new FormatException($"“{item}” is not valid.");
                from = Value(ends[0], min, max, names, nameBase);
                to = ends.Length == 2 ? Value(ends[1], min, max, names, nameBase)
                    : stepParts.Length == 2 ? (wrapSeven ? 6 : max) : from;
                if (to < from) throw new FormatException($"“{item}” is a backwards range.");
            }
            for (var v = from; v <= to; v += step)
                set[wrapSeven && v == 7 ? 0 : v] = true;
        }
    }

    private static int Value(string s, int min, int max, string[]? names, int nameBase)
    {
        if (names is not null)
        {
            var i = Array.IndexOf(names, s.ToUpperInvariant());
            if (i >= 0) return i + nameBase;
        }
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"“{s}” is not a number.");
        if (v < min || v > max) throw new FormatException($"{v} is outside {min}–{max}.");
        return v;
    }

    private bool DayMatches(DateTime d)
    {
        if (!_months[d.Month]) return false;
        var dom = _days[d.Day];
        var dow = _weekdays[(int)d.DayOfWeek];
        if (_anyDay && _anyWeekday) return true;
        if (_anyDay) return dow;
        if (_anyWeekday) return dom;
        return dom || dow;
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/>, or null if none within 5 years (e.g. 31 Feb).</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = after.AddYears(5);
        while (t < limit)
        {
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!_hours[t.Hour])
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind).AddHours(1);
                continue;
            }
            if (!_minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }
            return t;
        }
        return null;
    }

    /// <summary>The next few run times, for showing in the editor.</summary>
    public List<DateTime> Upcoming(DateTime after, int count)
    {
        var list = new List<DateTime>();
        var t = after;
        while (list.Count < count && Next(t) is { } n)
        {
            list.Add(n);
            t = n;
        }
        return list;
    }
}
