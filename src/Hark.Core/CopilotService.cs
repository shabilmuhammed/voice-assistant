using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Hark.Core;

public sealed record AuthResult(bool Ok, string Message);

public sealed record ModelChoice(string Id, string Name, bool Enabled)
{
    public override string ToString() => Name;
}

public enum ToolApproval { AllowOnce, Deny }

/// <summary>A tool wants to run. Key identifies the tool so the UI can remember "always allow".</summary>
public sealed record ToolApprovalRequest(string Key, string Title, string Detail, bool ReadOnly);

public abstract record ChatEvent;
public sealed record TextDelta(string MessageId, string Text) : ChatEvent;
public sealed record MessageComplete(string MessageId, string Content) : ChatEvent;
public sealed record ToolStarted(string CallId, string Name) : ChatEvent;
public sealed record ToolFinished(string CallId, bool Ok, string? Error) : ChatEvent;
public sealed record TurnError(string Message) : ChatEvent;
public sealed record TurnIdle : ChatEvent;

/// <summary>
/// Thin wrapper over the Copilot SDK. The PAT is the only credential: UseLoggedInUser is off,
/// so no other GitHub login on the PC can be picked up.
/// </summary>
public sealed class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConnected { get; private set; }

    public async Task<AuthResult> ConnectAsync(string pat)
    {
        await _gate.WaitAsync();
        try
        {
            Log.AddSecrets([pat]);
            if (_client is not null)
            {
                try { await _client.DisposeAsync(); } catch (Exception ex) { Log.Error("Disposing old client failed", ex); }
                _client = null;
                IsConnected = false;
            }

            // Give the agent its own empty folder so its built-in file tools do not start in the install folder.
            var workspace = Path.Combine(AppPaths.DataDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var client = new CopilotClient(new CopilotClientOptions
            {
                GitHubToken = pat,
                UseLoggedInUser = false,
                WorkingDirectory = workspace,
            });
            await client.StartAsync();
            var auth = await client.GetAuthStatusAsync();
            if (!auth.IsAuthenticated)
            {
                await client.DisposeAsync();
                return new AuthResult(false, auth.StatusMessage is { Length: > 0 } m ? m : "GitHub did not accept the token.");
            }
            _client = client;
            IsConnected = true;
            Log.Info($"Copilot connected ({auth.AuthType}, {auth.Host}).");
            return new AuthResult(true, auth.Login is { Length: > 0 } login ? $"Signed in as {login}" : "Token accepted");
        }
        catch (Exception ex)
        {
            Log.Error("Copilot connect failed", ex);
            return new AuthResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ModelChoice>> GetModelsAsync()
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Copilot.");
        var models = await client.ListModelsAsync();
        return models.Select(m => new ModelChoice(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
            m.Policy is null || string.Equals(m.Policy.State.ToString(), "enabled", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Starts a new session, or resumes one when <paramref name="resumeSessionId"/> is given and still exists.</summary>
    public async Task<ChatSession> OpenSessionAsync(
        string? model,
        Dictionary<string, McpServerConfig>? servers,
        string? resumeSessionId,
        Func<ToolApprovalRequest, Task<ToolApproval>> approve)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Copilot.");
        var chat = new ChatSession(approve);

        if (resumeSessionId is not null)
        {
            try
            {
                var resume = new ResumeSessionConfig { Streaming = true, OnPermissionRequest = chat.HandlePermissionAsync };
                if (!string.IsNullOrEmpty(model)) resume.Model = model;
                if (servers is { Count: > 0 }) resume.McpServers = servers;
                chat.Attach(await client.ResumeSessionAsync(resumeSessionId, resume), resumed: true);
                return chat;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not resume session {resumeSessionId}; starting a new one.", ex);
            }
        }

        var config = new SessionConfig { Streaming = true, OnPermissionRequest = chat.HandlePermissionAsync };
        if (!string.IsNullOrEmpty(model)) config.Model = model;
        if (servers is { Count: > 0 }) config.McpServers = servers;
        chat.Attach(await client.CreateSessionAsync(config), resumed: false);
        return chat;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try { await _client.DisposeAsync(); } catch (Exception ex) { Log.Error("Client dispose failed", ex); }
            _client = null;
        }
        IsConnected = false;
    }
}

public sealed class ChatSession : IAsyncDisposable
{
    private readonly Func<ToolApprovalRequest, Task<ToolApproval>> _approve;
    private CopilotSession? _session;
    private IDisposable? _subscription;

    internal ChatSession(Func<ToolApprovalRequest, Task<ToolApproval>> approve) => _approve = approve;

    public string SessionId => _session?.SessionId ?? "";
    public bool Resumed { get; private set; }

    /// <summary>Raised on a background thread. The UI must marshal to its own thread.</summary>
    public event Action<ChatEvent>? Event;

    internal void Attach(CopilotSession session, bool resumed)
    {
        _session = session;
        Resumed = resumed;
        _subscription = session.On<SessionEvent>(OnSessionEvent);
    }

    public Task SendAsync(string prompt) =>
        (_session ?? throw new InvalidOperationException("Session is closed.")).SendAsync(new MessageOptions { Prompt = prompt });

    public Task AbortAsync() => _session?.AbortAsync() ?? Task.CompletedTask;

    public Task SetModelAsync(string model) => _session?.SetModelAsync(model) ?? Task.CompletedTask;

    private void OnSessionEvent(SessionEvent evt)
    {
        try
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent d when d.Data.ParentToolCallId is null && !string.IsNullOrEmpty(d.Data.DeltaContent):
                    Event?.Invoke(new TextDelta(d.Data.MessageId ?? "", d.Data.DeltaContent!));
                    break;
                case AssistantMessageEvent m when m.Data.ParentToolCallId is null && !string.IsNullOrWhiteSpace(m.Data.Content):
                    Event?.Invoke(new MessageComplete(m.Data.MessageId ?? "", m.Data.Content!));
                    break;
                case ToolExecutionStartEvent t:
                    var name = t.Data.McpServerName is { Length: > 0 } server
                        ? $"{server}: {t.Data.McpToolName ?? t.Data.ToolName}"
                        : t.Data.ToolName ?? "tool";
                    Event?.Invoke(new ToolStarted(t.Data.ToolCallId ?? "", name));
                    break;
                case ToolExecutionCompleteEvent t:
                    Event?.Invoke(new ToolFinished(t.Data.ToolCallId ?? "", t.Data.Success, t.Data.Error?.Message));
                    break;
                case SessionErrorEvent e:
                    Log.Error($"Session error {e.Data.ErrorType}: {e.Data.Message}");
                    Event?.Invoke(new TurnError(e.Data.Message ?? "Copilot reported an error."));
                    break;
                case SessionIdleEvent:
                    Event?.Invoke(new TurnIdle());
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Handling a session event failed", ex);
        }
    }

    internal async Task<PermissionDecision> HandlePermissionAsync(PermissionRequest request, PermissionInvocation invocation)
    {
        try
        {
            var ask = Describe(request);
            var answer = await _approve(ask);
            return answer == ToolApproval.AllowOnce
                ? PermissionDecision.ApproveOnce()
                : PermissionDecision.Reject("The user declined this action.");
        }
        catch (Exception ex)
        {
            Log.Error("Permission prompt failed", ex);
            return PermissionDecision.Reject("The approval prompt failed.");
        }
    }

    private static ToolApprovalRequest Describe(PermissionRequest request) => request switch
    {
        PermissionRequestMcp m => new ToolApprovalRequest(
            $"mcp:{m.ServerName}/{m.ToolName}",
            $"{m.ServerName}: {m.ToolTitle ?? m.ToolName}",
            Pretty(m.Args),
            m.ReadOnly == true),
        PermissionRequestShell s => new ToolApprovalRequest("shell", "Run a command on this PC", s.FullCommandText ?? "", false),
        PermissionRequestWrite w => new ToolApprovalRequest("write", "Write a file", w.FileName ?? "", false),
        PermissionRequestRead r => new ToolApprovalRequest("read", "Read a file", r.Path ?? "", true),
        PermissionRequestUrl u => new ToolApprovalRequest("url", "Open a web address", u.Url ?? "", true),
        PermissionRequestCustomTool c => new ToolApprovalRequest($"tool:{c.ToolName}", c.ToolName ?? "Tool", Pretty(c.Args), false),
        _ => new ToolApprovalRequest($"other:{request.Kind}", request.Kind.ToString() ?? "Action", "", false),
    };

    private static string Pretty(object? args)
    {
        if (args is null) return "";
        try
        {
            var text = args is JsonElement el ? el.GetRawText() : JsonSerializer.Serialize(args);
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return args.ToString() ?? "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); } catch (Exception ex) { Log.Error("Session dispose failed", ex); }
            _session = null;
        }
    }
}
