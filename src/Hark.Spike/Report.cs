using System.Text;

namespace Hark.Spike;

/// <summary>Writes to the console and to a log file, masking every known secret value.</summary>
public sealed class Report : IDisposable
{
    private readonly StreamWriter _log;
    private readonly List<string> _secrets = new();
    private readonly List<(string Step, bool Ok, string Detail)> _results = new();
    private readonly object _gate = new();

    public string LogPath { get; }

    public Report(string logPath)
    {
        LogPath = logPath;
        _log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public void AddSecrets(IEnumerable<string?> values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v) && v.Length >= 6) _secrets.Add(v);
    }

    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var s in _secrets) text = text.Replace(s, "***");
        return text;
    }

    public void Line(string text = "")
    {
        var safe = Mask(text);
        lock (_gate)
        {
            Console.WriteLine(safe);
            _log.WriteLine(safe);
        }
    }

    public void Write(string text)
    {
        var safe = Mask(text);
        lock (_gate)
        {
            Console.Write(safe);
            _log.Write(safe);
        }
    }

    public void Step(string name, bool ok, string detail = "")
    {
        _results.Add((name, ok, detail));
        Line($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    public bool Summary()
    {
        Line();
        Line("==== Summary ====");
        foreach (var (step, ok, detail) in _results)
            Line($"{(ok ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        return _results.All(r => r.Ok);
    }

    public void Dispose() => _log.Dispose();
}
