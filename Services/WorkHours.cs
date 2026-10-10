using System;
using System.Globalization;
using System.Linq;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>The user's working hours (Settings → Work log): which moments count as working time.</summary>
public sealed class WorkHours
{
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public bool[] Days { get; } = new bool[7];

    public WorkHours(AppConfig config)
    {
        Start = TryParseTime(config.WorkHoursStart, out var s) ? s : TimeSpan.FromHours(9);
        End = TryParseTime(config.WorkHoursEnd, out var e) ? e : TimeSpan.FromHours(18);
        foreach (var d in config.WorkDays.Where(d => d is >= 0 and <= 6)) Days[d] = true;
    }

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (!TimeSpan.TryParseExact(text?.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t))
            return false;
        if (t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return false;
        time = t;
        return true;
    }

    /// <summary>True when the moment falls inside working hours. An end before the start (night shift) wraps
    /// past midnight and belongs to the day the shift started.</summary>
    public bool Contains(DateTime when)
    {
        var tod = when.TimeOfDay;
        if (Start == End) return Days[(int)when.DayOfWeek];
        if (Start < End) return Days[(int)when.DayOfWeek] && tod >= Start && tod < End;
        if (tod >= Start) return Days[(int)when.DayOfWeek];
        return tod < End && Days[(int)when.AddDays(-1).DayOfWeek];
    }

    /// <summary>"09:00–18:00, Mon–Fri".</summary>
    public string Describe()
    {
        var hours = Start == End ? "all day" : $"{Start:hh\\:mm}–{End:hh\\:mm}";
        var selected = Enumerable.Range(0, 7).Where(d => Days[d]).ToList();
        string days;
        if (selected.Count == 7) days = "every day";
        else if (selected.Count == 0) days = "no days selected";
        else if (selected.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) days = "Mon–Fri";
        else
        {
            // list Monday first, as people read a working week
            days = string.Join(" ", new[] { 1, 2, 3, 4, 5, 6, 0 }.Where(d => Days[d]).Select(d => DayNames[d]));
        }
        return $"{hours}, {days}";
    }
}
