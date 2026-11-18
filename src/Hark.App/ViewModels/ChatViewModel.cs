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
    private bool? _sessionUsesAzureCli;
    private bool _azureNoteShown;
    private readonly AzureLoginGate _azureLogin = new(AzureCli.HasWorkingSignInAsync);
    private bool _suppressModelChange;
    private bool _serversChecked;

    public ChatViewModel(MainViewModel main)
    {
        _main = main;
        Messages.CollectionChanged += (_, _) => IsEmpty = Messages.Count == 0;
    }

    public ObservableCollection<MessageVm> Messages { get; } = new();
    public ObservableCollection<ModelChoice> Models { get; } = new();

    [ObservableProperty]
    private string _title = NewChatTitle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendOrStopCommand))]
    private string _input = "";

    partial void OnInputChanged(string value) => UpdateSlash();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendOrStopCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private ModelChoice? _selectedModel;

    private const int MaxAttachments = 10;
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;

    /// <summary>Files and images that will go with the next message.</summary>
    public ObservableCollection<AttachmentVm> PendingAttachments { get; } = new();

    [ObservableProperty]
    private bool _hasPendingAttachments;

    private Dictionary<string, DateTime> _outputBefore = new();

    /// <summary>When on, the next message is sent with a capture of the whole screen.</summary>
    [ObservableProperty]
    private bool _includeScreen;

    // Phrases that mean "look at what I am looking at".
    private static readonly Regex ScreenPhrase = new(
        @"\b(my|the|this) screen\b|\bon[- ]screen\b|\bthis code\b|\bwhat('?s| is) (going on|happening) (here|in this)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Suggestion(string Title, string Detail, string Prompt);

    // ---- the "/" menu ---------------------------------------------------------------

    /// <summary>Skills matching what follows "/" in the message box.</summary>
    public ObservableCollection<SkillInfo> SlashMatches { get; } = new();

    [ObservableProperty]
    private bool _isSlashOpen;

    [ObservableProperty]
    private int _slashIndex;

    [ObservableProperty]
    private bool _hasSlashMatches;

    private string? _slashDismissedFor;

    /// <summary>The highlighted skill row, or -1 while the "Create a skill" row is highlighted.</summary>
    public int SlashListIndex => SlashIndex < SlashMatches.Count ? SlashIndex : -1;

    public bool IsCreateHighlighted => IsSlashOpen && SlashIndex >= SlashMatches.Count;

    public string SlashEmptyText => $"No skill matches \u201C{Input}\u201D.";

    partial void OnSlashIndexChanged(int value) => RaiseSlashHighlight();

    partial void OnIsSlashOpenChanged(bool value) => RaiseSlashHighlight();

    private void RaiseSlashHighlight()
    {
        OnPropertyChanged(nameof(SlashListIndex));
        OnPropertyChanged(nameof(IsCreateHighlighted));
    }

    /// <summary>Opens, filters or closes the menu as the message changes. Open while the message is "/" plus a partial name.</summary>
    private void UpdateSlash()
    {
        var text = Input;
        var typingCommand = text.StartsWith('/') && text.IndexOfAny([' ', '\n', '\r', '\t']) < 0;
        if (!typingCommand || text == _slashDismissedFor)
        {
            IsSlashOpen = false;
            if (!typingCommand) _slashDismissedFor = null;
            return;
        }

        var query = text[1..];
        var matches = _main.Skills.Items
            .Select(s => (Skill: s, Rank:
                s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 :
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 :
                // Descriptions are searched only once a few letters are typed, so "/re" does not match every "are".
                query.Length >= 3 && s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : -1))
            .Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank).ThenBy(m => m.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Skill)
            .ToList();

        SlashMatches.Clear();
        foreach (var skill in matches) SlashMatches.Add(skill);
        HasSlashMatches = SlashMatches.Count > 0;
        OnPropertyChanged(nameof(SlashEmptyText));
        SlashIndex = 0;
        IsSlashOpen = true;
        RaiseSlashHighlight();
    }

    /// <summary>Moves the highlight with the arrow keys. The last row is "Create a skill".</summary>
    public void MoveSlash(int step)
    {
        if (!IsSlashOpen) return;
        var rows = SlashMatches.Count + 1;
        SlashIndex = ((SlashIndex + step) % rows + rows) % rows;
    }

    /// <summary>Enter or Tab on the highlighted row.</summary>
    public void AcceptSlash()
    {
        if (!IsSlashOpen) return;
        if (SlashIndex < SlashMatches.Count) PickSkill(SlashMatches[SlashIndex]);
        else CreateSkillFromMenu();
    }

    public void DismissSlash()
    {
        _slashDismissedFor = Input;
        IsSlashOpen = false;
    }

    [RelayCommand]
    private void PickSkill(SkillInfo? skill)
    {
        if (skill is null) return;
        Input = "/" + skill.Name + " ";
        IsSlashOpen = false;
        FocusRequested?.Invoke();
    }

    [RelayCommand]
    private void CreateSkillFromMenu()
    {
        IsSlashOpen = false;
        Input = "";
        _main.CreateSkill();
    }

    /// <summary>After a skill is created or edited: the live chat picks it up, and an open menu shows it.</summary>
    public async Task ReloadSkillsAsync()
    {
        if (_session is not null) await _session.ReloadSkillsAsync();
        if (IsSlashOpen) UpdateSlash();
    }

    /// <summary>Starting points shown on an empty chat.</summary>
    public IReadOnlyList<Suggestion> Suggestions { get; } =
    [
        new("What is open this sprint?", "Searches Azure DevOps work items", "What is still open this sprint?"),
        new("Where does this table come from?", "Traces lineage in Purview", "Where does this table come from? Table: "),
        new("Start work on a repo", "Pulls, branches and edits in your repos folder", "Start work on the repo "),
        new("What is on my screen?", "Sends a capture of your desktop", "What is on my screen?"),
    ];

    /// <summary>True while the chat has no messages, so the greeting is shown.</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    public string ModelLabel => SelectedModel?.Name ?? "Model";

    /// <summary>Raised when the message box should take the keyboard focus.</summary>
    public event Action? FocusRequested;

    [RelayCommand]
    private void UseSuggestion(Suggestion? suggestion)
    {
        if (suggestion is null) return;
        Input = suggestion.Prompt;
        FocusRequested?.Invoke();
    }

    /// <summary>The round button: sends, or stops while Hark is working.</summary>
    private bool CanSendOrStop() => IsBusy || CanSend();

    [RelayCommand(CanExecute = nameof(CanSendOrStop))]
    private async Task SendOrStopAsync()
    {
        if (IsBusy) await StopAsync();
        else if (CanSend()) await SendAsync();
    }

    [RelayCommand]
    private void SelectModel(ModelChoice? model)
    {
        if (model is not null) SelectedModel = model;
    }

    private void Retry(AssistantMessageVm answer)
    {
        if (IsBusy) return;
        var index = Messages.IndexOf(answer);
        var question = Messages.Take(Math.Max(index, 0)).OfType<UserMessageVm>().LastOrDefault();
        if (question is null) return;
        Input = question.Text;
        if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
    }

    /// <summary>The answer being written, in full, each time it changes.</summary>
    public event Action<string>? AssistantTextChanged;

    /// <summary>A turn finished. The argument is the error message when it failed.</summary>
    public event Action<string?>? TurnEnded;

    /// <summary>A tool is waiting for the user's approval.</summary>
    public event Action? AttentionNeeded;

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
        OnPropertyChanged(nameof(ModelLabel));
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

        var wantsScreen = IncludeScreen || ScreenPhrase.IsMatch(prompt);
        var hasImage = wantsScreen || PendingAttachments.Any(f => f.IsImage);
        if (hasImage && SelectedModel?.Vision == false)
        {
            Input = prompt;
            Add(new ErrorVm($"{SelectedModel.Name} cannot read images, so nothing was sent. Pick another model, or remove the image, and send again."));
            IsBusy = false;
            return;
        }

        ScreenShot? shot = null;
        if (wantsScreen)
        {
            IncludeScreen = false;
            Status = "Capturing your screen…";
            shot = _main.CaptureScreen is null ? null : await _main.CaptureScreen();
            if (shot is null) Add(new NoteVm("The screen could not be captured, so the message was sent without it."));
        }

        var files = new List<string>();
        if (shot is not null) files.Add(shot.Path);
        files.AddRange(PendingAttachments.Select(f => f.Path).Where(File.Exists));
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        Add(new UserMessageVm(prompt, files));

        try
        {
            if (_conversationId is null)
            {
                Title = prompt.Length > 60 ? prompt[..60].TrimEnd() + "…" : prompt;
                _conversationId = _main.Store.CreateConversation(Title, SelectedModel?.Id);
                _main.RefreshRecents();
            }
            foreach (var file in files)
                _main.Store.AddMessage(_conversationId.Value, new AttachmentVm(file).IsImage ? "image" : "file", file);
            _main.Store.AddMessage(_conversationId.Value, "user", prompt);

            // The Azure CLI setting changed since this chat started: reopen the chat with the new instructions, keeping its context.
            if (_session is not null && _sessionUsesAzureCli != _main.Settings.AzureCliFallback)
            {
                var keep = _session.SessionId;
                await CloseSessionAsync();
                _resumeSessionId = keep;
            }

            if (_session is null)
            {
                var mcp = _main.LoadMcpServers();
                var azureCli = _main.Settings.AzureCliFallback;
                Status = mcp.Count > 0
                    ? "Starting your tools. The first message can take a minute or two."
                    : "Starting…";
                var repos = _main.Settings.ReposRoot;
                var hasRepos = !string.IsNullOrWhiteSpace(repos) && Directory.Exists(repos);
                var output = _main.Settings.EffectiveOutputFolder;
                var options = new SessionOptions(SelectedModel?.Id, mcp, _resumeSessionId,
                    hasRepos ? repos : null,
                    RepoPolicy.Instructions(hasRepos ? repos : null, output) + "\n\n" +
                    AzureCli.Instructions(azureCli, AzureCli.FindExecutable() is not null, AzureCli.FindDevOpsOrganization(mcp)),
                    [output, SkillLibrary.PersonalFolder],
                    [SkillLibrary.PersonalFolder]);
                _session = await _main.Copilot.OpenSessionAsync(options, ApproveAsync);
                _session.Event += OnChatEvent;
                _sessionUsesAzureCli = azureCli;
                if (_resumeSessionId is not null && !_session.Resumed)
                    Add(new NoteVm("The earlier context of this chat could not be restored, so Copilot only sees messages from here on."));
                _resumeSessionId = null;
                _main.Store.SetSession(_conversationId.Value, _session.SessionId, SelectedModel?.Id);
            }

            Status = "Working…";
            var text = shot is { ActiveWindowTitle.Length: > 0 }
                ? $"{prompt}\n\n(A capture of my whole screen is attached. The window in front is titled \"{shot.ActiveWindowTitle}\".)"
                : shot is not null ? $"{prompt}\n\n(A capture of my whole screen is attached.)" : prompt;
            _outputBefore = SnapshotOutput();
            if (SkillLibrary.MatchCommand(prompt, _main.Skills.Items) is { } call)
            {
                var arguments = text.Length > prompt.Length ? call.Arguments + text[prompt.Length..] : call.Arguments;
                Status = $"Running /{call.Skill.Name}…";
                var run = await _session.RunSkillAsync(call.Skill, arguments, files.Count == 0 ? null : files);
                if (run.Message is { Length: > 0 } message) Add(new NoteVm(message));
                if (!run.TurnStarted)
                {
                    IsBusy = false;
                    Status = "";
                    TurnEnded?.Invoke(null);
                }
            }
            else
            {
                await _session.SendAsync(text, files.Count == 0 ? null : files);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Send failed", ex);
            Add(new ErrorVm($"Could not send the message: {ex.Message}"));
            IsBusy = false;
            Status = "";
            TurnEnded?.Invoke($"Could not send the message: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Attach()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files or images",
            Multiselect = true,
            CheckFileExists = true,
        };
        var repos = _main.Settings.ReposRoot;
        if (!string.IsNullOrWhiteSpace(repos) && Directory.Exists(repos)) dialog.InitialDirectory = repos;
        if (dialog.ShowDialog() == true) AddAttachments(dialog.FileNames);
    }

    /// <summary>Adds files to the next message. Used by the Attach button, drag and drop, and paste.</summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { skipped.Add($"{Path.GetFileName(path)} is a folder"); continue; }
            if (!File.Exists(path)) continue;
            if (PendingAttachments.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (PendingAttachments.Count >= MaxAttachments) { skipped.Add($"only {MaxAttachments} files fit in one message"); break; }
            if (new FileInfo(path).Length > MaxAttachmentBytes) { skipped.Add($"{Path.GetFileName(path)} is larger than 25 MB"); continue; }
            PendingAttachments.Add(new AttachmentVm(path));
        }
        HasPendingAttachments = PendingAttachments.Count > 0;
        Status = skipped.Count > 0 ? "Not attached: " + string.Join("; ", skipped) + "." : "";
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentVm? attachment)
    {
        if (attachment is not null) PendingAttachments.Remove(attachment);
        HasPendingAttachments = PendingAttachments.Count > 0;
    }

    // ---- generated files: whatever appears or changes in the output folder during a turn ----

    private Dictionary<string, DateTime> SnapshotOutput()
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_main.Settings.EffectiveOutputFolder, "*", SearchOption.AllDirectories).Take(5000))
                snapshot[file] = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the output folder failed", ex);
        }
        return snapshot;
    }

    private void ReportGeneratedFiles()
    {
        var after = SnapshotOutput();
        var changed = after.Where(f => !_outputBefore.TryGetValue(f.Key, out var before) || before != f.Value)
            .Select(f => f.Key).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        _outputBefore = after;
        if (changed.Count == 0) return;
        Add(new GeneratedFilesVm(changed));
        foreach (var file in changed) Persist("generated", file);
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
        PendingAttachments.Clear();
        HasPendingAttachments = false;
        Title = NewChatTitle;
        Status = "";
        IsBusy = false;
        _main.ShowChat();
        _main.RefreshRecents();
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

        var pendingFiles = new List<string>();
        var generated = new List<string>();
        void FlushGenerated()
        {
            if (generated.Count == 0) return;
            Messages.Add(new GeneratedFilesVm(generated.ToList()));
            generated.Clear();
        }
        foreach (var m in _main.Store.GetMessages(conversation.Id))
        {
            if (m.Kind != "generated") FlushGenerated();
            switch (m.Kind)
            {
                case "image" or "file": pendingFiles.Add(m.Content); break;
                case "generated": generated.Add(m.Content); break;
                case "user":
                    Messages.Add(new UserMessageVm(m.Content, pendingFiles.ToList()));
                    pendingFiles.Clear();
                    break;
                case "assistant": Messages.Add(new AssistantMessageVm("", m.Content, null, Retry)); break;
                case "error": Messages.Add(new ErrorVm(m.Content)); break;
                case "tool":
                    var parts = m.Content.Split('|', 2);
                    var ok = parts.Length < 2 || parts[1] == "ok";
                    if (Messages.LastOrDefault() is not ToolActivityVm activity) Messages.Add(activity = new ToolActivityVm());
                    activity.AddFinished(parts[0], ok);
                    break;
            }
        }

        FlushGenerated();
        PendingAttachments.Clear();
        HasPendingAttachments = false;

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
        Add(new UserMessageVm("What is going on in this code?", [path, @"C:\Users\you\source\repos\cdp-orchestrator\src\loader.py"]));
        Add(new GeneratedFilesVm([@"C:\Users\you\Documents\Hark\sprint-42-open-items.csv", @"C:\Users\you\Documents\Hark\loader-review.md"]));
        PendingAttachments.Add(new AttachmentVm(@"C:\Users\you\Documents\requirements.docx"));
        PendingAttachments.Add(new AttachmentVm(@"C:\Users\you\Pictures\error-dialog.png"));
        HasPendingAttachments = true;
    }

    public void LoadDemo(bool approvals)
    {
        Title = "Ingestion pipeline: sprint status";
        Messages.Add(new UserMessageVm("What is still open on the ingestion pipeline repo this sprint?"));
        var used = new ToolActivityVm();
        Messages.Add(used);
        used.Finish(used.Start("1", "martechchat: search_work_items"), true, null);
        used.Finish(used.Start("2", "martechchat: get_work_item"), true, null);
        if (!approvals)
        {
        Messages.Add(new AssistantMessageVm("a", """
            Three items are still open in **Sprint 42**:

            | Id | Title | Type |
            |---|---|---|
            | #4812 | Null schema crashes the bronze loader | Bug |
            | #4790 | Add retry policy to the blob trigger | Task |
            | #4755 | Backfill job for March partitions | Task |

            The bug is the urgent one. The loader fails in `load_bronze()` when the schema is missing:

            ```python
            def load_bronze(frame):
                schema = frame.schema  # None for empty files
                return write(frame, schema.names)
            ```

            1. Guard against an empty schema.
            2. Add a test with a zero-row file.
            """, "Claude Sonnet 5.5", Retry));
            return;
        }
        Messages.Add(new AssistantMessageVm("a", "Three items are still open in **Sprint 42**.", "Claude Sonnet 5.5", Retry));
        Messages.Add(new UserMessageVm("File a bug for the retry gap and assign it to me."));
        Messages.Add(new ApprovalVm(new ToolApprovalRequest("demo", "martechchat: create_bug",
            "{\n  \"title\": \"Blob trigger has no retry policy\",\n  \"assignedTo\": \"me\"\n}", false), _ => { }));
        Messages.Add(new ApprovalVm(new ToolApprovalRequest("shell", "this command to run on your PC",
            "git checkout -b fix/blob-trigger-retry", false, ToolKind.Shell, "git checkout -b fix/blob-trigger-retry"), _ => { }));
        var failed = new ToolActivityVm();
        Messages.Add(failed);
        failed.Finish(failed.Start("3", "martechchat: get_work_item"), false, "Work item 9999 was not found.");
        Messages.Add(new ErrorVm("Copilot could not be reached. Check your network connection and try again."));
        Status = "Waiting for your answer…";
    }

    public async Task CloseSessionAsync()
    {
        CancelPendingApprovals();
        _alwaysAllowed.Clear();
        _serversChecked = false;
        _sessionUsesAzureCli = null;
        _azureNoteShown = false;
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
                var streaming = Streaming(d.MessageId);
                streaming.Text += d.Text;
                AssistantTextChanged?.Invoke(streaming.Text);
                ScrollRequested?.Invoke();
                break;

            case MessageComplete m:
                Streaming(m.MessageId).Text = m.Content;
                AssistantTextChanged?.Invoke(m.Content);
                Persist("assistant", m.Content);
                ScrollRequested?.Invoke();
                break;

            case ToolStarted t:
                if (Messages.LastOrDefault() is not ToolActivityVm running) Add(running = new ToolActivityVm());
                running.Start(t.CallId, t.Name);
                Status = "";
                ScrollRequested?.Invoke();
                break;

            case ToolFinished t:
                foreach (var group in Messages.OfType<ToolActivityVm>().Reverse())
                {
                    if (group.Steps.LastOrDefault(x => x.CallId == t.CallId && x.IsRunning) is not { } step) continue;
                    group.Finish(step, t.Ok, t.Error);
                    Persist("tool", $"{step.Name}|{(t.Ok ? "ok" : "failed")}");
                    break;
                }
                break;

            case TurnError e:
                Add(new ErrorVm(e.Message));
                Persist("error", e.Message);
                IsBusy = false;
                Status = "";
                TurnEnded?.Invoke(e.Message);
                break;

            case TurnIdle:
                IsBusy = false;
                Status = "";
                ReportGeneratedFiles();
                TurnEnded?.Invoke(null);
                _ = ReportServerProblemsAsync();
                break;
        }
    }

    /// <summary>Once per session, says which MCP servers did not start, so a missing tool is not a mystery.</summary>
    private async Task ReportServerProblemsAsync()
    {
        if (_serversChecked || _session is not { } session) return;
        _serversChecked = true;
        foreach (var server in await session.GetServerProblemsAsync())
            Add(new NoteVm($"The {server.Name} server is not available ({server.Status}){(server.Error.Length > 0 ? ": " + server.Error : ".")}"));
    }

    private AssistantMessageVm Streaming(string messageId)
    {
        if (Messages.OfType<AssistantMessageVm>().LastOrDefault(m => m.MessageId == messageId && messageId.Length > 0) is { } existing)
            return existing;
        var created = new AssistantMessageVm(messageId, "", SelectedModel?.Name, Retry);
        Add(created);
        return created;
    }

    private Task<ToolApproval> ApproveAsync(ToolApprovalRequest request)
    {
        // The Azure CLI is only used when the fallback is switched on in Settings, and signs in at most once.
        if (request.Kind == ToolKind.Shell && AzureCli.UsesAz(request.Target, request.Executables))
        {
            if (!_main.Settings.AzureCliFallback)
            {
                Log.Info("Refused an Azure CLI command: the fallback is off.");
                ShowAzureNote("Copilot tried the Azure CLI. It is turned off in Settings, so the command was skipped.");
                return Task.FromResult(ToolApproval.DenyAzureCliOff);
            }
            if (AzureCli.IsLogin(request.Target)) return ApproveAzureLoginAsync(request);
        }
        return AskAsync(request);
    }

    private async Task<ToolApproval> ApproveAzureLoginAsync(ToolApprovalRequest request)
    {
        switch (await _azureLogin.DecideAsync())
        {
            case AzureLoginGate.Decision.AlreadySignedIn:
                Log.Info("Refused az login: the cached Azure CLI sign-in still works.");
                return ToolApproval.DenyAzureAlreadySignedIn;
            case AzureLoginGate.Decision.InProgress:
                Log.Info("Refused az login: a sign-in was started moments ago.");
                return ToolApproval.DenyAzureSignInInProgress;
            default:
                ShowAzureNote("Copilot is signing in to the Azure CLI. Finish in the browser window; the sign-in is kept for later chats.");
                return await AskAsync(request);
        }
    }

    private void ShowAzureNote(string text)
    {
        if (_azureNoteShown) return;
        _azureNoteShown = true;
        Application.Current?.Dispatcher.BeginInvoke(() => Add(new NoteVm(text)));
    }

    private Task<ToolApproval> AskAsync(ToolApprovalRequest request)
    {
        var mode = _main.Settings.ApprovalMode;

        // "Allow all": tools run without asking. This is the default and can be changed in Settings.
        if (mode == "AllowAll") return Task.FromResult(ToolApproval.AllowOnce);

        // Saving into the output folder is what that folder is for, so it never asks.
        if (request.Kind == ToolKind.Write && RepoPolicy.IsInside(_main.Settings.EffectiveOutputFolder, request.Target))
            return Task.FromResult(ToolApproval.AllowOnce);

        // A file edit outside the repos folder always asks, even after "allow edits for this chat".
        var insideRepos = request.Kind != ToolKind.Write || RepoPolicy.IsInside(_main.Settings.ReposRoot, request.Target);
        if ((insideRepos && _alwaysAllowed.Contains(request.Key)) || (request.ReadOnly && mode != "AskAlways"))
            return Task.FromResult(ToolApproval.AllowOnce);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(ToolApproval.Deny);

        return dispatcher.InvokeAsync(() =>
        {
            var card = new ApprovalVm(request, key => _alwaysAllowed.Add(key));
            Add(card);
            Status = "Waiting for your answer…";
            _main.RequestAttention();
            AttentionNeeded?.Invoke();
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
