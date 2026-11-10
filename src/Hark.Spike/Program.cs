#pragma warning disable GHCP001 // PermissionDecision is marked experimental in the SDK.
using System.Text;
using GitHub.Copilot;
using Hark.Spike;

// Hark spike: proves the pieces the full app depends on, on the real laptop.
//   1. The fine-grained PAT is accepted (and is the only credential used).
//   2. Copilot returns the list of models.
//   3. A plain prompt gets an answer.
//   4. MCP servers from the VS Code mcp.json start and their tools are visible.
// Usage: hark-spike [--mcp <path>] [--model <id>] [--server <name>] [--prompt "<text>"] [--no-mcp]

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var logPath = Path.Combine(AppContext.BaseDirectory, "hark-spike.log");
using var report = new Report(logPath);
report.Line($"Hark spike, {DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.OSVersion}, .NET {Environment.Version}");

// ---- PAT -------------------------------------------------------------------
var pat = Environment.GetEnvironmentVariable("HARK_GITHUB_PAT");
if (string.IsNullOrWhiteSpace(pat))
{
    Console.Write("Paste your GitHub fine-grained PAT (input is hidden), then Enter: ");
    pat = ReadHidden();
}
pat = pat.Trim();
report.AddSecrets([pat]);
if (!pat.StartsWith("github_pat_", StringComparison.Ordinal))
    report.Line("Note: this does not look like a fine-grained PAT (those start with github_pat_). Classic tokens are not accepted by Copilot.");

// ---- MCP config ------------------------------------------------------------
var mcpPath = Arg("--mcp") ?? McpConfig.DefaultPath;
McpConfig.Loaded? mcp = null;
if (!args.Contains("--no-mcp"))
{
    try
    {
        mcp = McpConfig.Load(mcpPath);
        report.AddSecrets(mcp.Secrets);
        var only = Arg("--server");
        if (only is not null)
            foreach (var key in mcp.Servers.Keys.Where(k => !k.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList())
                mcp.Servers.Remove(key);
        report.Step("Read mcp.json", mcp.Servers.Count > 0, $"{mcpPath}: {string.Join(", ", mcp.Servers.Keys)}");
        foreach (var w in mcp.Warnings) report.Line("  warning: " + w);
    }
    catch (Exception ex)
    {
        report.Step("Read mcp.json", false, $"{mcpPath}: {ex.Message}");
    }
}

// ---- Copilot client: PAT only ---------------------------------------------
CopilotClient? client = null;
try
{
    client = new CopilotClient(new CopilotClientOptions
    {
        GitHubToken = pat,
        UseLoggedInUser = false, // never fall back to another GitHub login on this PC
    });
    await client.StartAsync();
    report.Step("Start Copilot runtime", true);
}
catch (Exception ex)
{
    report.Step("Start Copilot runtime", false, ex.Message);
    report.Line(ex.ToString());
    return Finish();
}

await using (client)
{
    try
    {
        var auth = await client.GetAuthStatusAsync();
        report.Step("PAT accepted", auth.IsAuthenticated,
            $"login={auth.Login ?? "?"}, type={auth.AuthType ?? "?"}{(auth.StatusMessage is { Length: > 0 } m ? ", " + m : "")}");
        if (!auth.IsAuthenticated) return Finish();
    }
    catch (Exception ex)
    {
        report.Step("PAT accepted", false, ex.Message);
        return Finish();
    }

    // ---- Models ------------------------------------------------------------
    string? model = Arg("--model");
    try
    {
        var models = await client.ListModelsAsync();
        report.Step("List models", models.Count > 0, $"{models.Count} models");
        foreach (var mi in models)
            report.Line($"    {mi.Id,-34} {mi.Name,-34} x{mi.Billing?.Multiplier.ToString() ?? "?"}  {mi.Policy?.State.ToString() ?? ""}");
        model ??= models.FirstOrDefault()?.Id;
    }
    catch (Exception ex)
    {
        report.Step("List models", false, ex.Message);
    }

    // ---- Plain prompt ------------------------------------------------------
    await RunPrompt("Plain prompt", "Reply with exactly the word: OK", servers: null, TimeSpan.FromMinutes(2));

    // ---- Prompt with MCP servers ------------------------------------------
    if (mcp is { Servers.Count: > 0 })
    {
        var prompt = Arg("--prompt") ??
            "List the names of the tools you have from MCP servers, grouped by server. Do not call any of them.";
        report.Line();
        report.Line("Starting MCP servers. 'dotnet run' servers build first, so this can take a minute or two.");
        await RunPrompt("Prompt with MCP servers", prompt, mcp.Servers, TimeSpan.FromMinutes(6));
    }

    async Task RunPrompt(string step, string prompt, Dictionary<string, McpServerConfig>? servers, TimeSpan timeout)
    {
        report.Line();
        report.Line($"> {prompt}");
        try
        {
            var config = new SessionConfig
            {
                Model = model,
                Streaming = true,
                OnPermissionRequest = AskOnConsole,
            };
            if (servers is not null) config.McpServers = servers;

            await using var session = await client.CreateSessionAsync(config);
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new StringBuilder();
            var toolCalls = 0;

            using var sub = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent d:
                        report.Write(d.Data.DeltaContent ?? "");
                        break;
                    case AssistantMessageEvent msg:
                        answer.Append(msg.Data.Content);
                        break;
                    case ToolExecutionStartEvent t:
                        toolCalls++;
                        report.Line($"\n  [tool start] {t.Data.McpServerName ?? "built-in"} / {t.Data.McpToolName ?? t.Data.ToolName}");
                        break;
                    case ToolExecutionCompleteEvent t:
                        report.Line($"  [tool {(t.Data.Success ? "ok" : "failed")}] {t.Data.Error?.Message}");
                        break;
                    case SessionErrorEvent err:
                        done.TrySetResult($"{err.Data.ErrorType}: {err.Data.Message}");
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult(null);
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt });
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
            report.Line();
            if (finished != done.Task)
                report.Step(step, false, $"no answer after {timeout.TotalMinutes:0} min");
            else if (done.Task.Result is { } error)
                report.Step(step, false, error);
            else
                report.Step(step, answer.Length > 0, $"{answer.Length} chars, {toolCalls} tool calls, model {model ?? "default"}");
        }
        catch (Exception ex)
        {
            report.Step(step, false, ex.Message);
            report.Line(ex.ToString());
        }
    }
}

return Finish();

// ---- helpers ----------------------------------------------------------------
int Finish()
{
    var ok = report.Summary();
    report.Line();
    report.Line($"Log saved to {report.LogPath} (tokens are masked). Paste that file back.");
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return ok ? 0 : 1;
}

Task<PermissionDecision> AskOnConsole(PermissionRequest request, PermissionInvocation invocation)
{
    report.Line($"\n  [permission] {request.Kind}: {System.Text.Json.JsonSerializer.Serialize(request, request.GetType())}");
    if (Console.IsInputRedirected)
        return Task.FromResult<PermissionDecision>(PermissionDecision.UserNotAvailable());
    Console.Write("  Allow this once? (y/N): ");
    var reply = Console.ReadLine();
    var allow = reply is not null && reply.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    report.Line($"  [permission] {(allow ? "allowed" : "denied")}");
    return Task.FromResult<PermissionDecision>(allow ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject("Denied by the user."));
}

static string ReadHidden()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}
