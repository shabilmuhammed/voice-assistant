namespace Hark.Voice;

/// <summary>
/// Looks for the wake phrase in text produced by the speech recognizer. This is the second way to wake:
/// it works on whole words the recognizer understood, so it copes with accents the sound-based spotter misses.
/// </summary>
public static class WakeMatcher
{
    // How "hey" tends to come out of a recognizer. Only accepted as the very first word of what was said,
    // so "we had a chat" does not wake Hark.
    private static readonly HashSet<string> HeyVariants = new(StringComparer.Ordinal)
        { "HEY", "HAY", "HI", "HEI", "HE", "A", "AYE", "EH", "OK", "OKAY" };

    public sealed record Match(bool Found, string Rest);

    private static string Bare(string word) => new string(word.Where(c => char.IsLetter(c) || c == '\'').ToArray()).ToUpperInvariant();

    /// <param name="text">What the recognizer heard.</param>
    /// <param name="phrases">The wake phrase and any "also wake on" phrases.</param>
    public static Match Find(string text, IEnumerable<string> phrases)
    {
        var original = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = original.Select(Bare).ToArray();

        var best = -1;
        var bestLength = 0;
        foreach (var phrase in phrases)
        {
            var wake = WakePhrase.Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (wake.Length == 0) continue;
            for (var start = 0; start + wake.Length <= words.Length; start++)
            {
                var ok = true;
                for (var i = 0; i < wake.Length && ok; i++)
                {
                    var heard = words[start + i];
                    var exact = heard == wake[i];
                    var looseHey = i == 0 && start == 0 && wake.Length > 1 && wake[0] == "HEY" && HeyVariants.Contains(heard);
                    ok = exact || looseHey;
                }
                if (!ok) continue;
                if (best < 0 || start < best) (best, bestLength) = (start, wake.Length);
                break;
            }
        }

        if (best < 0) return new Match(false, "");
        var rest = string.Join(' ', original.Skip(best + bestLength)).TrimStart(',', '.', '!', '?', ' ');
        if (rest.Length > 0) rest = char.ToUpperInvariant(rest[0]) + rest[1..];
        return new Match(true, rest);
    }
}
