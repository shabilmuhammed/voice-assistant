using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Hark.Core;

namespace Hark.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Settings = AppSettings.Load();
        if (!Settings.ReposRootSuggested)
        {
            // First run: if the usual Visual Studio repos folder exists, start with it. It can be changed or cleared in Settings.
            var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
            if (string.IsNullOrWhiteSpace(Settings.ReposRoot) && Directory.Exists(usual)) Settings.ReposRoot = usual;
            Settings.ReposRootSuggested = true;
            try { Settings.Save(); } catch (Exception ex) { Log.Error("Saving settings failed", ex); }
        }
        AzureCli.SkipSubscriptionQuestion();
        Store = new ChatStore();
        Copilot = new CopilotService();
        Chat = new ChatViewModel(this);
        History = new HistoryViewModel(this);
        Connections = new ConnectionsViewModel(this);
        Skills = new SkillsViewModel(this);
        Skills.Changed += () => _ = Chat.ReloadSkillsAsync();
        QuickPrompt = new QuickPromptViewModel(this);
        Preferences = new SettingsViewModel(this);
        _currentPage = Chat;
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.Title)) UpdatePageTitle();
        };
    }

    public AppSettings Settings { get; }
    public ChatStore Store { get; }
    public CopilotService Copilot { get; }

    public ChatViewModel Chat { get; }
    public HistoryViewModel History { get; }
    public ConnectionsViewModel Connections { get; }
    public SettingsViewModel Preferences { get; }
    public QuickPromptViewModel QuickPrompt { get; }

    /// <summary>False when another app already owns Ctrl + Alt + Space.</summary>
    [ObservableProperty]
    private bool _quickPromptShortcutAvailable = true;
    public SkillsViewModel Skills { get; }

    // Sidebar order. The selection pill and the page shown both follow these numbers.
    public const int ChatPage = 0, HistoryPage = 1, SkillsPage = 2, ConnectionsPage = 3, SettingsPage = 4;

    private bool _demo;

    [ObservableProperty]
    private object _currentPage;

    /// <summary>Recent chats for the sidebar: day headings interleaved with chats.</summary>
    public ObservableCollection<object> Recents { get; } = new();

    /// <summary>Shown in the top bar: the chat's title, or the page name.</summary>
    [ObservableProperty]
    private string _pageTitle = "New chat";

    [ObservableProperty]
    private string _toolsStatus = "";

    public void RefreshRecents()
    {
        if (_demo) return;
        try
        {
            Recents.Clear();
            foreach (var item in ChatGroups.Build(Store.ListConversations().Take(30), Chat.CurrentConversationId)) Recents.Add(item);
        }
        catch (Exception ex)
        {
            Log.Error("Loading recent chats failed", ex);
        }
    }

    private void UpdatePageTitle() => PageTitle = SelectedNavIndex switch
    {
        HistoryPage => "History",
        SkillsPage => "Skills",
        ConnectionsPage => "Connections",
        SettingsPage => "Settings",
        _ => Chat.Title,
    };

    private void UpdateToolsStatus()
    {
        var count = Connections.Servers.Count(s => s.Enabled);
        ToolsStatus = count == 0 ? "No tool servers" : count == 1 ? "1 tool server" : $"{count} tool servers";
    }

    [RelayCommand]
    private void SearchChats()
    {
        SelectedNavIndex = HistoryPage;
        History.FocusSearch();
    }

    /// <summary>One of ChatPage, HistoryPage, SkillsPage, ConnectionsPage, SettingsPage.</summary>
    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private string _connectionStatus = "Not connected";

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Set by the app shell: captures the whole screen, or returns null if it cannot.</summary>
    public Func<Task<ScreenShot?>>? CaptureScreen { get; set; }

    /// <summary>Raised when the window should come to the front (for example, a tool is waiting for approval).</summary>
    public event Action? AttentionRequested;

    public void RequestAttention() => AttentionRequested?.Invoke();

    partial void OnSelectedNavIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            HistoryPage => History,
            SkillsPage => Skills,
            ConnectionsPage => Connections,
            SettingsPage => Preferences,
            _ => Chat,
        };
        UpdatePageTitle();
        if (_demo) return;
        if (value == HistoryPage) History.Refresh();
        if (value == SkillsPage) _ = Skills.RefreshAsync();
        if (value == ConnectionsPage) { Connections.Reload(); UpdateToolsStatus(); }
    }

    public void ShowChat() => SelectedNavIndex = ChatPage;

    /// <summary>Opens the Skills page with an empty editor.</summary>
    public void CreateSkill()
    {
        SelectedNavIndex = SkillsPage;
        Skills.New();
    }

    public async Task InitializeAsync()
    {
        Connections.Reload();
        UpdateToolsStatus();
        RefreshRecents();
        await Skills.RefreshAsync();
        if (string.IsNullOrWhiteSpace(Settings.GitHubPat))
        {
            ConnectionStatus = "Add your GitHub PAT to get started";
            SelectedNavIndex = ConnectionsPage;
            return;
        }
        await ConnectAsync(Settings.GitHubPat);
    }

    public async Task<AuthResult> ConnectAsync(string pat)
    {
        ConnectionStatus = "Connecting to Copilot…";
        IsConnected = false;
        await Chat.CloseSessionAsync();

        var result = await Copilot.ConnectAsync(pat);
        IsConnected = result.Ok;
        ConnectionStatus = result.Ok ? "Copilot connected" : "Copilot not connected";
        if (!result.Ok) return result;

        _ = Skills.RefreshAsync(); // now the runtime can add built-in and plugin skills
        try
        {
            Chat.SetModels(await Copilot.GetModelsAsync());
            Preferences.RefreshModels();
        }
        catch (Exception ex)
        {
            Log.Error("Listing models failed", ex);
            return new AuthResult(true, $"{result.Message}, but the model list could not be loaded: {ex.Message}");
        }
        return result;
    }

    /// <summary>MCP servers from the VS Code mcp.json, minus the ones switched off in Connections.</summary>
    public Dictionary<string, McpServerConfig> LoadMcpServers()
    {
        try
        {
            var path = Settings.EffectiveMcpConfigPath;
            if (!File.Exists(path)) return new();
            var loaded = McpConfig.Load(path);
            Log.AddSecrets(loaded.Secrets);
            foreach (var name in Settings.DisabledServers) loaded.Servers.Remove(name);
            return loaded.Servers;
        }
        catch (Exception ex)
        {
            Log.Error("Reading mcp.json failed", ex);
            return new();
        }
    }

    /// <summary>Sample content for screenshots. Nothing is sent or saved.</summary>
    public void LoadDemo(int page, bool approvals = false, bool skillEditor = false)
    {
        _demo = true;
        ConnectionStatus = "Copilot connected";
        IsConnected = true;
        Chat.SetModels([new("auto", "Auto", true), new("claude-sonnet-5.5", "Claude Sonnet 5.5", true), new("gpt-6-luna", "GPT-6 Luna", true)]);
        Preferences.RefreshModels();
        Chat.LoadDemo(approvals);
        History.LoadDemo();
        Connections.LoadDemo();
        Skills.LoadDemo(skillEditor);
        UpdateToolsStatus();
        foreach (var item in History.Items) Recents.Add(item);
        UpdatePageTitle();
        SelectedNavIndex = page;
    }

    public async Task ShutdownAsync()
    {
        await Chat.CloseSessionAsync();
        await Copilot.DisposeAsync();
    }
}
