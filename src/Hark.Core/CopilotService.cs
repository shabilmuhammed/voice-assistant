using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Hark.Core;

public sealed record AuthResult(bool Ok, string Message);

/// <param name="Vision">True or false when Copilot says so; null when it does not say.</param>
public sealed record ModelChoice(string Id, string Name, bool Enabled, bool? Vision = null)
{
    public override string ToString() => Name;
}

public enum ToolApproval { AllowOnce, Deny }

public enum ToolKind { Mcp, Shell, Write, Read, Url, Other }

/// <summary>
/// A tool wants to run. Key identifies it so the UI can remember "allow for this chat".
/// Target is the command for Shell and the file path for Write and Read.
/// </summary>
public sealed record ToolApprovalRequest(string Key, string Title, string Detail, bool ReadOnly,
    ToolKind Kind = ToolKind.Other, string Target = "");

/// <summary>What a chat session is started with.</summary>
public sealed record SessionOptions(
    string? Model,
    Dictionary<string, McpServerConfig>? Servers,
    string? ResumeSessionId,
    string? WorkingDirectory,
    string? Instructions,
    IReadOnlyList<string>? AdditionalDirectories = null);

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
            m.Policy is null || string.Equals(m.Policy.State.ToString(), "enabled", StringComparison.OrdinalIgnoreCase),
            m.Capabilities?.Supports?.Vision)).ToList();
    }

    /// <summary>Starts a new session, or resumes one when a session id is given and still exists.</summary>
    public async Task<ChatSession> OpenSessionAsync(SessionOptions options, Func<ToolApprovalRequest, Task<ToolApproval>> approve)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Copilot.");
        var chat = new ChatSession(approve);

        if (options.ResumeSessionId is not null)
        {
            try
            {
                var resume = new ResumeSessionConfig();
                Apply(resume, options, chat);
                chat.Attach(await client.ResumeSessionAsync(options.ResumeSessionId, resume), resumed: true);
                return chat;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not resume session {options.ResumeSessionId}; starting a new one.", ex);
            }
        }

        var config = new SessionConfig();
        Apply(config, options, chat);
        chat.Attach(await client.CreateSessionAsync(config), resumed: false);
        return chat;
    }

    private static void Apply(SessionConfigBase config, SessionOptions options, ChatSession chat)
    {
        config.Streaming = true;
        config.OnPermissionRequest = chat.HandlePermissionAsync;
        if (!string.IsNullOrEmpty(options.Model)) config.Model = options.Model;
        if (options.Servers is { Count: > 0 }) config.McpServers = options.Servers;
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory))
            config.WorkingDirectory = options.WorkingDirectory;
        var extra = options.AdditionalDirectories?.Where(Directory.Exists).ToList();
        if (extra is { Count: > 0 }) config.AdditionalDirectories = extra;
        if (!string.IsNullOrWhiteSpace(options.Instructions))
            config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = options.Instructions };
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

    public Task SendAsync(string prompt, IReadOnlyList<string>? attachmentPaths = null)
    {
        var session = _session ?? throw new InvalidOperationException("Session is closed.");
        var message = new MessageOptions { Prompt = prompt };
        if (attachmentPaths is { Count: > 0 })
            message.Attachments = attachmentPaths
                .Select(p => (Attachment)new AttachmentFile { Path = p, DisplayName = Path.GetFileName(p) })
                .ToList();
        return session.SendAsync(message);
    }

    /// <summary>MCP servers that are not connected, with the reason when the runtime gives one.</summary>
    public async Task<IReadOnlyList<(string Name, string Status, string Error)>> GetServerProblemsAsync()
    {
        var problems = new List<(string, string, string)>();
        try
        {
            if (_session is null) return problems;
            var list = await _session.Rpc.Mcp.ListAsync();
            foreach (var server in list.Servers)
            {
                var status = server.Status.ToString() ?? "";
                var error = server.Error ?? "";
                var healthy = error.Length == 0 && !status.Contains("fail", StringComparison.OrdinalIgnoreCase)
                              && !status.Contains("error", StringComparison.OrdinalIgnoreCase);
                if (!healthy) problems.Add((server.Name, status, error));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading MCP server status failed", ex);
        }
        return problems;
    }

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
            m.ReadOnly == true, ToolKind.Mcp),
        PermissionRequestShell s => new ToolApprovalRequest("shell", "this command to run on your PC", s.FullCommandText ?? "", false,
            ToolKind.Shell, s.FullCommandText ?? ""),
        PermissionRequestWrite w => new ToolApprovalRequest("write", "a file to be changed", w.ResolvedPath ?? w.FileName ?? "", false,
            ToolKind.Write, w.ResolvedPath ?? w.FileName ?? ""),
        PermissionRequestRead r => new ToolApprovalRequest("read", "a file to be read", r.ResolvedPath ?? r.Path ?? "", true,
            ToolKind.Read, r.ResolvedPath ?? r.Path ?? ""),
        PermissionRequestUrl u => new ToolApprovalRequest("url", "a web address to be opened", u.Url ?? "", true, ToolKind.Url, u.Url ?? ""),
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
