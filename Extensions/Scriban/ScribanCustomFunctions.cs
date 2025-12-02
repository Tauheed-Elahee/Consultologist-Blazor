using Scriban;
using Scriban.Runtime;
using System.Globalization;

namespace BlazorWasm.Extensions.Scriban;

public static class ScribanCustomFunctions
{
    /// <summary>
    /// Converts newlines to HTML br tags
    /// Liquid: {{ text | newline_to_br }}
    /// </summary>
    public static string NewlineToBr(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("\r\n", "<br>\n")
            .Replace("\n", "<br>\n")
            .Replace("\r", "<br>\n");
    }

    /// <summary>
    /// Provides a default value if the input is null or empty
    /// Liquid: {{ value | default: 'fallback' }}
    /// </summary>
    public static object? Default(object? value, object? defaultValue)
    {
        if (value == null)
            return defaultValue;

        if (value is string str && string.IsNullOrEmpty(str))
            return defaultValue;

        return value;
    }

    /// <summary>
    /// Replaces occurrences of a string with another string
    /// Liquid: {{ text | replace: 'old', 'new' }}
    /// </summary>
    public static string Replace(string? text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue))
            return text ?? string.Empty;

        return text.Replace(oldValue, newValue);
    }

    /// <summary>
    /// Removes leading and trailing whitespace
    /// Liquid: {{ text | strip }}
    /// </summary>
    public static string Strip(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Joins array elements with a separator
    /// Liquid: {{ array | join: ', ' }}
    /// </summary>
    public static string Join(object? array, string separator)
    {
        if (array == null)
            return string.Empty;

        if (array is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? string.Empty);
            }
            return string.Join(separator, items);
        }

        return array.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Converts string to lowercase
    /// Liquid: {{ text | downcase }}
    /// </summary>
    public static string Downcase(string? text)
    {
        return text?.ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>
    /// Capitalizes the first character of a string
    /// Liquid: {{ text | capitalize }}
    /// </summary>
    public static string Capitalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (text.Length == 1)
            return text.ToUpperInvariant();

        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>
    /// Formats a date string
    /// Liquid: {{ date | date: "%B %-d, %Y" }}
    /// </summary>
    public static string Date(object? date, string format)
    {
        if (date == null)
            return string.Empty;

        DateTime dateTime;

        if (date is DateTime dt)
        {
            dateTime = dt;
        }
        else if (date is string dateStr && DateTime.TryParse(dateStr, out var parsed))
        {
            dateTime = parsed;
        }
        else
        {
            return date.ToString() ?? string.Empty;
        }

        // Convert strftime format to .NET format
        // This is a simplified conversion - may need to expand for more formats
        var dotnetFormat = format
            .Replace("%B", "MMMM")      // Full month name
            .Replace("%b", "MMM")       // Abbreviated month name
            .Replace("%d", "dd")        // Day with leading zero
            .Replace("%-d", "d")        // Day without leading zero
            .Replace("%Y", "yyyy")      // 4-digit year
            .Replace("%y", "yy")        // 2-digit year
            .Replace("%H", "HH")        // Hour (24-hour, with leading zero)
            .Replace("%M", "mm")        // Minute with leading zero
            .Replace("%S", "ss")        // Second with leading zero
            .Replace("%Z", "zzz");      // Timezone

        try
        {
            return dateTime.ToString(dotnetFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return dateTime.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Register all custom functions with Scriban template context
    /// </summary>
    public static void Register(ScriptObject scriptObject)
    {
        scriptObject.Import("newline_to_br", new Func<string?, string>(NewlineToBr));
        scriptObject.Import("default", new Func<object?, object?, object?>(Default));
        scriptObject.Import("replace", new Func<string?, string, string, string>(Replace));
        scriptObject.Import("strip", new Func<string?, string>(Strip));
        scriptObject.Import("join", new Func<object?, string, string>(Join));
        scriptObject.Import("downcase", new Func<string?, string>(Downcase));
        scriptObject.Import("capitalize", new Func<string?, string>(Capitalize));
        scriptObject.Import("date", new Func<object?, string, string>(Date));
    }
}
