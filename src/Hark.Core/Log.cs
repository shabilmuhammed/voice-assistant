namespace Hark.Core;

/// <summary>Small file log. Every registered secret is masked before a line is written.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Secrets = new();

    public static void AddSecrets(IEnumerable<string?> values)
    {
        lock (Gate)
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v) && v.Length >= 6) Secrets.Add(v);
    }

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        lock (Gate)
            foreach (var s in Secrets) text = text.Replace(s, "***");
        return text;
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {Mask(message)}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(AppPaths.LogFile, line);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
