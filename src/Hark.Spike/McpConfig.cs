using System.Text.Json;
using GitHub.Copilot;

namespace Hark.Spike;

/// <summary>Reads a VS Code style mcp.json (top-level "servers") into Copilot SDK server configs.</summary>
public static class McpConfig
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "mcp.json");

    public sealed record Loaded(Dictionary<string, McpServerConfig> Servers, List<string> Secrets, List<string> Warnings);

    public static Loaded Load(string path)
    {
        var servers = new Dictionary<string, McpServerConfig>();
        var secrets = new List<string>();
        var warnings = new List<string>();

        // VS Code allows comments and trailing commas in this file.
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path), options);

        if (!doc.RootElement.TryGetProperty("servers", out var serversEl) &&
            !doc.RootElement.TryGetProperty("mcpServers", out serversEl))
        {
            warnings.Add("No \"servers\" section found.");
            return new Loaded(servers, secrets, warnings);
        }

        foreach (var entry in serversEl.EnumerateObject())
        {
            var s = entry.Value;
            if (s.GetRawText().Contains("${input:", StringComparison.Ordinal))
                warnings.Add($"{entry.Name}: uses ${{input:...}}, which only VS Code can fill in. Put the value in directly.");

            if (s.TryGetProperty("url", out var url))
            {
                var http = new McpHttpServerConfig { Url = url.GetString()! };
                if (s.TryGetProperty("headers", out var headers))
                {
                    http.Headers = ReadMap(headers);
                    secrets.AddRange(http.Headers.Values);
                }
                servers[entry.Name] = http;
            }
            else if (s.TryGetProperty("command", out var command))
            {
                var stdio = new McpStdioServerConfig { Command = command.GetString()! };
                if (s.TryGetProperty("args", out var args))
                    stdio.Args = args.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
                if (s.TryGetProperty("env", out var env))
                {
                    stdio.Env = ReadMap(env);
                    secrets.AddRange(stdio.Env.Where(kv => LooksSecret(kv.Key)).Select(kv => kv.Value));
                }
                if (s.TryGetProperty("cwd", out var cwd)) stdio.WorkingDirectory = cwd.GetString();
                servers[entry.Name] = stdio;
            }
            else
            {
                warnings.Add($"{entry.Name}: has neither \"command\" nor \"url\"; skipped.");
            }
        }
        return new Loaded(servers, secrets, warnings);
    }

    private static Dictionary<string, string> ReadMap(JsonElement el) =>
        el.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());

    private static bool LooksSecret(string key)
    {
        var k = key.ToUpperInvariant();
        return k.Contains("PAT") || k.Contains("TOKEN") || k.Contains("SECRET") || k.Contains("KEY") || k.Contains("PASSWORD");
    }
}
