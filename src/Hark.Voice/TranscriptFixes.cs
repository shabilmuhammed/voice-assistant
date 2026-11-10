using System.Text.RegularExpressions;

namespace Hark.Voice;

/// <summary>Replaces words the recognizer commonly mishears (for example "per view" for "Purview").</summary>
public static class TranscriptFixes
{
    public static string Apply(string text, IReadOnlyDictionary<string, string>? corrections)
    {
        if (string.IsNullOrWhiteSpace(text) || corrections is null) return text;
        // Longest phrases first, so "azure dev ops" wins over a shorter overlapping entry.
        foreach (var (heard, meant) in corrections.OrderByDescending(c => c.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(heard)) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(heard.Trim()).Replace(@"\ ", @"\s+") + @"(?![\p{L}\p{N}])";
            text = Regex.Replace(text, pattern, meant.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        return text;
    }
}
