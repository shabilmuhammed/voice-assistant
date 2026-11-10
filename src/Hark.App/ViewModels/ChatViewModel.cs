using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;

namespace Hark.App.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private const string NewChatTitle = "New chat";

    private readonly MainViewModel _main;
    private readonly HashSet<string> _alwaysAllowed = new();
    private ChatSession? _session;
    private long? _conversationId;
    private string? _resumeSessionId;
    private bool _suppressModelChange;

    public ChatViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<MessageVm> Messages { get; } = new();
    public ObservableCollection<ModelChoice> Models { get; } = new();

    [ObservableProperty]
    private string _title = NewChatTitle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private ModelChoice? _selectedModel;

    /// <summary>When on, the next message is sent with a capture of the whole screen.</summary>
    [ObservableProperty]
    private bool _includeScreen;

    // Phrases that mean "look at what I am looking at".
    private static readonly Regex ScreenPhrase = new(
        @"\b(my|the|this) screen\b|\bon[- ]screen\b|\bthis code\b|\bwhat('?s| is) (going on|happening) (here|in this)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Raised when the view should scroll to the newest message.</summary>
    public event Action? ScrollRequested;

    public void SetModels(IEnumerable<ModelChoice> models)
    {
        _suppressModelChange = true;
        try
        {
            var keep = SelectedModel?.Id ?? _main.Settings.DefaultModel;
            Models.Clear();
            foreach (var m in models) Models.Add(m);
            SelectedModel = Models.FirstOrDefault(m => m.Id == keep) ?? Models.FirstOrDefault();
        }
        finally
        {
            _suppressModelChange = false;
        }
    }

    partial void OnSelectedModelChanged(ModelChoice? value)
    {
        if (_suppressModelChange || value is null) return;
        _ = ChangeModelAsync(value);
    }

    private async Task ChangeModelAsync(ModelChoice model)
    {
        try
        {
            if (_session is not null) await _session.SetModelAsync(model.Id);
            if (_conversationId is { } id) _main.Store.SetSession(id, _session?.SessionId ?? _resumeSessionId, model.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Changing the model failed", ex);
            Add(new ErrorVm($"Could not switch to {model.Name}: {ex.Message}"));
        }
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var prompt = Input.Trim();
        if (prompt.Length == 0) return;

        if (!_main.Copilot.IsConnected)
        {
            Add(new ErrorVm("Copilot is not connected yet. Add your GitHub PAT on the Connections tab."));
            return;
        }

        Input = "";
        IsBusy = true;

        ScreenShot? shot = null;
        if (IncludeScreen || ScreenPhrase.IsMatch(prompt))
        {
            IncludeScreen = false;
            if (SelectedModel?.Vision == false)
            {
                Add(new UserMessageVm(prompt));
                Add(new ErrorVm($"{SelectedModel.Name} cannot read images, so the screen was not sent. Pick another model and ask again."));
                IsBusy = false;
                return;
            }
            Status = "Capturing your screen…";
            shot = _main.CaptureScreen is null ? null : await _main.CaptureScreen();
            if (shot is null) Add(new NoteVm("The screen could not be captured, so the message was sent without it."));
        }
        Add(new UserMessageVm(prompt, shot?.Path));

        try
        {
            if (_conversationId is null)
            {
                Title = prompt.Length > 60 ? prompt[..60].TrimEnd() + "…" : prompt;
                _conversationId = _main.Store.CreateConversation(Title, SelectedModel?.Id);
            }
            if (shot is not null) _main.Store.AddMessage(_conversationId.Value, "image", shot.Path);
            _main.Store.AddMessage(_conversationId.Value, "user", prompt);

            if (_session is null)
            {
                var mcp = _main.LoadMcpServers();
                Status = mcp.Count > 0
                    ? "Starting your tools. The first message can take a minute or two."
                    : "Starting…";
                var repos = _main.Settings.ReposRoot;
                var hasRepos = !string.IsNullOrWhiteSpace(repos) && Directory.Exists(repos);
                var options = new SessionOptions(SelectedModel?.Id, mcp, _resumeSessionId,
                    hasRepos ? repos : null,
                    hasRepos ? RepoPolicy.Instructions(repos!) : null);
                _session = await _main.Copilot.OpenSessionAsync(options, ApproveAsync);
                _session.Event += OnChatEvent;
                if (_resumeSessionId is not null && !_session.Resumed)
                    Add(new NoteVm("The earlier context of this chat could not be restored, so Copilot only sees messages from here on."));
                _resumeSessionId = null;
                _main.Store.SetSession(_conversationId.Value, _session.SessionId, SelectedModel?.Id);
            }

            Status = "Working…";
            var text = shot is { ActiveWindowTitle.Length: > 0 }
                ? $"{prompt}\n\n(A capture of my whole screen is attached. The window in front is titled \"{shot.ActiveWindowTitle}\".)"
                : shot is not null ? $"{prompt}\n\n(A capture of my whole screen is attached.)" : prompt;
            await _session.SendAsync(text, shot is null ? null : [shot.Path]);
        }
        catch (Exception ex)
        {
            Log.Error("Send failed", ex);
            Add(new ErrorVm($"Could not send the message: {ex.Message}"));
            IsBusy = false;
            Status = "";
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        CancelPendingApprovals();
        try
        {
            if (_session is not null) await _session.AbortAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Stop failed", ex);
        }
        IsBusy = false;
        Status = "";
    }

    [RelayCommand]
    private async Task NewChatAsync()
    {
        await CloseSessionAsync();
        Messages.Clear();
        _conversationId = null;
        _resumeSessionId = null;
        Title = NewChatTitle;
        Status = "";
        IsBusy = false;
        _main.ShowChat();
    }

    /// <summary>Loads a saved chat. Its Copilot session is resumed on the next message.</summary>
    public async Task OpenConversationAsync(Conversation conversation)
    {
        await CloseSessionAsync();
        Messages.Clear();
        _conversationId = conversation.Id;
        _resumeSessionId = conversation.SessionId;
        Title = conversation.Title;
        Status = "";
        IsBusy = false;

        string? pendingImage = null;
        foreach (var m in _main.Store.GetMessages(conversation.Id))
        {
            switch (m.Kind)
            {
                case "image": pendingImage = m.Content; break;
                case "user":
                    Messages.Add(new UserMessageVm(m.Content, pendingImage));
                    pendingImage = null;
                    break;
                case "assistant": Messages.Add(new AssistantMessageVm("", m.Content)); break;
                case "error": Messages.Add(new ErrorVm(m.Content)); break;
                case "tool":
                    var parts = m.Content.Split('|', 2);
                    var ok = parts.Length < 2 || parts[1] == "ok";
                    Messages.Add(new ToolCallVm("", parts[0]) { IsRunning = false, Status = ok ? "Done" : "Failed" });
                    break;
            }
        }

        if (conversation.Model is { } modelId && Models.FirstOrDefault(m => m.Id == modelId) is { } model)
        {
            _suppressModelChange = true;
            SelectedModel = model;
            _suppressModelChange = false;
        }
        ScrollRequested?.Invoke();
    }

    public long? CurrentConversationId => _conversationId;

    public void AddDemoImage(string path)
    {
        Add(new UserMessageVm("What is going on in this code?", path));
    }

    public void LoadDemo()
    {
        Title = "Ingestion pipeline: sprint status";
        Messages.Add(new UserMessageVm("What is still open on the ingestion pipeline repo this sprint?"));
        Messages.Add(new ToolCallVm("1", "martechchat: search_work_items") { IsRunning = false, Status = "Done" });
        Messages.Add(new AssistantMessageVm("a", "Three items are still open in Sprint 42:\n\n#4812  Null schema crashes the bronze loader (Bug)\n#4790  Add retry policy to the blob trigger (Task)\n#4755  Backfill job for March partitions (Task)"));
        Messages.Add(new UserMessageVm("File a bug for the retry gap and assign it to me."));
        Messages.Add(new ApprovalVm(new ToolApprovalRequest("demo", "martechchat: create_bug",
            "{\n  \"title\": \"Blob trigger has no retry policy\",\n  \"assignedTo\": \"me\"\n}", false), _ => { }));
        Messages.Add(new ToolCallVm("2", "martechchat: get_work_item") { IsRunning = false, Status = "Failed", Error = "Work item 9999 was not found." });
        Messages.Add(new ErrorVm("Copilot could not be reached. Check your network connection and try again."));
        Status = "Waiting for your answer…";
    }

    public async Task CloseSessionAsync()
    {
        CancelPendingApprovals();
        _alwaysAllowed.Clear();
        if (_session is { } session)
        {
            _session = null;
            session.Event -= OnChatEvent;
            await session.DisposeAsync();
        }
    }

    // ---- events from Copilot (background thread) ---------------------------------

    private void OnChatEvent(ChatEvent evt) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(evt));

    private void Apply(ChatEvent evt)
    {
        switch (evt)
        {
            case TextDelta d:
                Streaming(d.MessageId).Text += d.Text;
                ScrollRequested?.Invoke();
                break;

            case MessageComplete m:
                Streaming(m.MessageId).Text = m.Content;
                Persist("assistant", m.Content);
                ScrollRequested?.Invoke();
                break;

            case ToolStarted t:
                Add(new ToolCallVm(t.CallId, t.Name));
                Status = $"Using {t.Name}…";
                break;

            case ToolFinished t:
                if (Messages.OfType<ToolCallVm>().LastOrDefault(x => x.CallId == t.CallId && x.IsRunning) is { } call)
                {
                    call.IsRunning = false;
                    call.Status = t.Ok ? "Done" : "Failed";
                    call.Error = t.Ok ? "" : t.Error ?? "";
                    Persist("tool", $"{call.Name}|{(t.Ok ? "ok" : "failed")}");
                }
                Status = "Working…";
                break;

            case TurnError e:
                Add(new ErrorVm(e.Message));
                Persist("error", e.Message);
                IsBusy = false;
                Status = "";
                break;

            case TurnIdle:
                IsBusy = false;
                Status = "";
                break;
        }
    }

    private AssistantMessageVm Streaming(string messageId)
    {
        if (Messages.OfType<AssistantMessageVm>().LastOrDefault(m => m.MessageId == messageId && messageId.Length > 0) is { } existing)
            return existing;
        var created = new AssistantMessageVm(messageId, "");
        Add(created);
        return created;
    }

    private Task<ToolApproval> ApproveAsync(ToolApprovalRequest request)
    {
        // Hard rule, whatever else is allowed: Hark never commits or pushes.
        if (request.Kind == ToolKind.Shell && RepoPolicy.IsCommitOrPush(request.Target))
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
                Add(new NoteVm("Blocked a git commit or push. Hark saves changes but leaves committing and pushing to you.")));
            return Task.FromResult(ToolApproval.DenyCommitOrPush);
        }

        // A file edit outside the repos folder always asks, even after "allow edits for this chat".
        var insideRepos = request.Kind != ToolKind.Write || RepoPolicy.IsInside(_main.Settings.ReposRoot, request.Target);
        if ((insideRepos && _alwaysAllowed.Contains(request.Key)) || (request.ReadOnly && _main.Settings.AutoApproveReadOnlyTools))
            return Task.FromResult(ToolApproval.AllowOnce);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(ToolApproval.Deny);

        return dispatcher.InvokeAsync(() =>
        {
            var card = new ApprovalVm(request, key => _alwaysAllowed.Add(key));
            Add(card);
            Status = "Waiting for your answer…";
            _main.RequestAttention();
            return card.Answer;
        }).Task.Unwrap();
    }

    private void CancelPendingApprovals()
    {
        foreach (var card in Messages.OfType<ApprovalVm>().Where(a => a.IsPending).ToList()) card.Cancel();
    }

    private void Persist(string kind, string content)
    {
        try
        {
            if (_conversationId is { } id) _main.Store.AddMessage(id, kind, content);
        }
        catch (Exception ex)
        {
            Log.Error("Saving a message failed", ex);
        }
    }

    private void Add(MessageVm message)
    {
        Messages.Add(message);
        ScrollRequested?.Invoke();
    }
}
