using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Formats "now" for pasting into notes and files. The format is either a plain .NET custom date format
/// ("yyyy-MM-dd HH:mm") or, if it contains {…}, a template where only the braced parts are formatted
/// ("[{HH:mm}] Standup" → "[09:30] Standup"). Invariant culture keeps separators and digits predictable.
/// </summary>
public static class TimestampService
{
    public const string DefaultFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Description + format; the UI shows a live example next to each description.</summary>
    public static readonly (string Description, string Format)[] Presets =
    {
        ("Date and time with seconds", "yyyy-MM-dd HH:mm:ss"),
        ("Date and time", "yyyy-MM-dd HH:mm"),
        ("Day first", "dd/MM/yyyy HH:mm"),
        ("Time only, in brackets", "[{HH:mm}] "),
        ("Long, with weekday", "ddd, dd MMM yyyy HH:mm"),
        ("File-name friendly", "yyyyMMdd_HHmmss"),
        ("ISO 8601", "yyyy-MM-ddTHH:mm:sszzz"),
    };

    private static readonly Regex Braced = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    public static bool TryFormat(string? format, DateTime when, out string result)
    {
        var f = string.IsNullOrEmpty(format) ? DefaultFormat : format;
        try
        {
            result = Braced.IsMatch(f)
                ? Braced.Replace(f, m => m.Groups[1].Value.Length == 0 ? "" : when.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture))
                : when.ToString(f, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            result = "";
            return false;
        }
    }

    /// <summary>Formats the current time, falling back to the default format if the saved one is invalid.</summary>
    public static string Now(string? format)
        => TryFormat(format, DateTime.Now, out var text) ? text : DateTime.Now.ToString(DefaultFormat, CultureInfo.InvariantCulture);
}
