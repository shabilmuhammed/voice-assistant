using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hark.Core;

/// <summary>A Copilot skill: a folder holding a SKILL.md with a name, a description and instructions.</summary>
/// <param name="Source">Personal, Project, Built-in, Plugin or Custom.</param>
/// <param name="Path">The SKILL.md file, when the skill has one on this PC.</param>
public sealed record SkillInfo(string Name, string Description, string? ArgumentHint, string Source, string? Path, bool Editable)
{
    public string Command => "/" + Name;
}

/// <summary>What the editor works with.</summary>
public sealed record SkillDraft(string Name, string Description, string? ArgumentHint, string Instructions);

/// <summary>
/// Reads and writes SKILL.md files: YAML front matter between "---" lines, then Markdown instructions.
/// Only name, description and argument-hint are edited; any other front matter (allowed-tools, license, …) is kept as written.
/// </summary>
public static partial class SkillFile
{
    public const string FileName = "SKILL.md";
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    private static readonly string[] EditedKeys = ["name", "description", "argument-hint"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*:\s*(.*)$")]
    private static partial Regex KeyLine();

    public sealed record Parsed(Dictionary<string, string> Fields, List<(string Key, List<string> Lines)> Entries, string Body);

    /// <summary>Lower case letters, digits and single hyphens, up to 64 characters. Returns null when valid.</summary>
    public static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the skill a name.";
        if (name.Length > MaxNameLength) return $"Keep the name to {MaxNameLength} characters or fewer.";
        if (!NamePattern().IsMatch(name)) return "Use lower case letters, numbers and single hyphens only, for example \"release-notes\".";
        return null;
    }

    /// <summary>Turns "Release Notes!" into "release-notes", for suggesting a name.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > MaxNameLength ? slug[..MaxNameLength].Trim('-') : slug;
    }

    public static Parsed Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string, List<string>)>();
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal) && text.TrimEnd() != "---")
            return new Parsed(fields, entries, text.Trim('\n'));

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return new Parsed(fields, entries, text.Trim('\n'));
        var front = text[4..(end + 1)];
        var after = text[(end + 4)..];
        var nl = after.IndexOf('\n');
        var body = nl < 0 ? "" : after[(nl + 1)..];

        string? key = null;
        List<string>? lines = null;
        foreach (var line in front.Split('\n'))
        {
            var m = KeyLine().Match(line);
            if (m.Success && !char.IsWhiteSpace(line[0]))
            {
                key = m.Groups[1].Value;
                lines = [line];
                entries.Add((key, lines));
                continue;
            }
            if (lines is not null) lines.Add(line);
            else if (line.Trim().Length > 0) entries.Add(("", [line]));
        }

        foreach (var (k, ls) in entries)
            if (k.Length > 0) fields[k] = ReadValue(ls);

        return new Parsed(fields, entries, body.Trim('\n'));
    }

    /// <summary>Reads a YAML scalar: plain, quoted, or a | / > block on the following indented lines.</summary>
    private static string ReadValue(List<string> lines)
    {
        var first = KeyLine().Match(lines[0]).Groups[2].Value.Trim();
        var rest = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList();
        if (first is "|" or "|-" or "|+") return string.Join("\n", rest);
        if (first is ">" or ">-" or ">+" || first.Length == 0) return string.Join(" ", rest);
        if (first.StartsWith('"') && first.EndsWith('"') && first.Length >= 2)
        {
            try { return JsonSerializer.Deserialize<string>(first) ?? ""; } catch { return first.Trim('"'); }
        }
        if (first.StartsWith('\'') && first.EndsWith('\'') && first.Length >= 2) return first[1..^1].Replace("''", "'");
        return rest.Count == 0 ? first : first + " " + string.Join(" ", rest);
    }

    private static string Quote(string value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Builds the SKILL.md text, keeping front matter this app does not edit from <paramref name="existing"/>.</summary>
    public static string Compose(SkillDraft draft, string? existing = null)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(draft.Name.Trim()).Append('\n');
        sb.Append("description: ").Append(Quote(draft.Description.Trim().ReplaceLineEndings(" "))).Append('\n');
        if (!string.IsNullOrWhiteSpace(draft.ArgumentHint))
            sb.Append("argument-hint: ").Append(Quote(draft.ArgumentHint.Trim())).Append('\n');
        if (existing is not null)
            foreach (var (key, lines) in Parse(existing).Entries)
                if (!EditedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    foreach (var line in lines) sb.Append(line).Append('\n');
        sb.Append("---\n\n");
        sb.Append(draft.Instructions.Replace("\r\n", "\n").Trim('\n')).Append('\n');
        return sb.ToString();
    }

    public static SkillInfo? Read(string skillFile, string source, bool editable)
    {
        try
        {
            var parsed = Parse(File.ReadAllText(skillFile));
            var folderName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(skillFile)) ?? "";
            var name = parsed.Fields.TryGetValue("name", out var n) && n.Trim().Length > 0 ? n.Trim() : folderName;
            parsed.Fields.TryGetValue("description", out var description);
            parsed.Fields.TryGetValue("argument-hint", out var hint);
            return new SkillInfo(name, description?.Trim() ?? "", string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(), source, skillFile, editable);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {skillFile}", ex);
            return null;
        }
    }
}

/// <summary>The user's personal skills folder, where this app creates and edits skills.</summary>
public static class SkillLibrary
{
    /// <summary>%USERPROFILE%\.copilot\skills, or COPILOT_HOME\skills when that is set.</summary>
    public static string PersonalFolder
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
            return System.IO.Path.Combine(home, "skills");
        }
    }

    /// <summary>Every folder under <paramref name="root"/> that holds a SKILL.md.</summary>
    public static List<SkillInfo> Scan(string root, string source = "Personal")
    {
        var list = new List<SkillInfo>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = System.IO.Path.Combine(dir, SkillFile.FileName);
                if (File.Exists(file) && SkillFile.Read(file, source, editable: true) is { } skill) list.Add(skill);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not list skills in {root}", ex);
        }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Load(SkillInfo skill) =>
        skill.Path is { } path && File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>Creates a skill (<paramref name="existingFile"/> null) or updates one. Returns the SKILL.md path.</summary>
    public static string Save(SkillDraft draft, string? existingFile, string? root = null)
    {
        if (SkillFile.CheckName(draft.Name) is { } nameProblem) throw new ArgumentException(nameProblem);
        if (string.IsNullOrWhiteSpace(draft.Description)) throw new ArgumentException("Describe what the skill does and when Copilot should use it.");
        if (draft.Description.Length > SkillFile.MaxDescriptionLength)
            throw new ArgumentException($"Keep the description to {SkillFile.MaxDescriptionLength} characters or fewer.");
        if (string.IsNullOrWhiteSpace(draft.Instructions)) throw new ArgumentException("Write the instructions Copilot should follow.");

        string file;
        string? existing = null;
        if (existingFile is null)
        {
            var folder = System.IO.Path.Combine(root ?? PersonalFolder, draft.Name);
            file = System.IO.Path.Combine(folder, SkillFile.FileName);
            if (File.Exists(file)) throw new ArgumentException($"A skill called \"{draft.Name}\" already exists. Pick another name, or edit that one.");
            Directory.CreateDirectory(folder);
        }
        else
        {
            file = existingFile;
            if (File.Exists(file)) existing = File.ReadAllText(file);
        }

        // Write next to the file first, then swap it in, so a failure never leaves a half-written skill.
        var temp = file + ".tmp";
        File.WriteAllText(temp, SkillFile.Compose(draft, existing), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, file, overwrite: true);
        return file;
    }

    /// <summary>Splits "/release-notes for 2.4" into the skill and "for 2.4" when the first word names a known skill.</summary>
    public static (SkillInfo Skill, string Arguments)? MatchCommand(string text, IEnumerable<SkillInfo> skills)
    {
        if (!text.StartsWith('/')) return null;
        var space = text.IndexOfAny([' ', '\n', '\r', '\t']);
        var name = (space < 0 ? text[1..] : text[1..space]).Trim();
        var skill = skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return skill is null ? null : (skill, space < 0 ? "" : text[(space + 1)..].Trim());
    }

    /// <summary>The prompt used when the runtime cannot run the skill itself: its instructions travel with the request.</summary>
    public static string InlinePrompt(SkillInfo skill, string content, string arguments)
    {
        var body = SkillFile.Parse(content).Body;
        var folder = skill.Path is { } p ? System.IO.Path.GetDirectoryName(p) : null;
        var sb = new StringBuilder();
        sb.Append("Use the \"").Append(skill.Name).Append("\" skill for this request. Follow its instructions below.");
        if (folder is not null) sb.Append(" Any supporting files it mentions are in ").Append(folder).Append('.');
        sb.Append("\n\n<skill name=\"").Append(skill.Name).Append("\">\n").Append(body).Append("\n</skill>\n\n");
        sb.Append(arguments.Length > 0 ? arguments : "Run this skill.");
        return sb.ToString();
    }
}
