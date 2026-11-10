using CommunityToolkit.Mvvm.ComponentModel;
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
        Store = new ChatStore();
        Copilot = new CopilotService();
        Chat = new ChatViewModel(this);
        History = new HistoryViewModel(this);
        Connections = new ConnectionsViewModel(this);
        Voice = new VoiceViewModel(this);
        Preferences = new SettingsViewModel(this);
        _currentPage = Chat;
    }

    public AppSettings Settings { get; }
    public ChatStore Store { get; }
    public CopilotService Copilot { get; }

    public ChatViewModel Chat { get; }
    public HistoryViewModel History { get; }
    public ConnectionsViewModel Connections { get; }
    public SettingsViewModel Preferences { get; }
    public VoiceViewModel Voice { get; }

    private bool _demo;

    [ObservableProperty]
    private object _currentPage;

    /// <summary>0 Chat, 1 History, 2 Connections, 3 Settings.</summary>
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
            1 => History,
            2 => Connections,
            3 => Preferences,
            _ => Chat,
        };
        if (_demo) return;
        if (value == 1) History.Refresh();
        if (value == 2) Connections.Reload();
    }

    public void ShowChat() => SelectedNavIndex = 0;

    public async Task InitializeAsync()
    {
        Voice.ApplySettings();
        Connections.Reload();
        if (string.IsNullOrWhiteSpace(Settings.GitHubPat))
        {
            ConnectionStatus = "Add your GitHub PAT to get started";
            SelectedNavIndex = 2;
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
    public void LoadDemo(int page, bool approvals = false)
    {
        _demo = true;
        ConnectionStatus = "Copilot connected";
        IsConnected = true;
        Chat.SetModels([new("auto", "Auto", true), new("claude-sonnet-5.5", "Claude Sonnet 5.5", true), new("gpt-6-luna", "GPT-6 Luna", true)]);
        Preferences.RefreshModels();
        Chat.LoadDemo(approvals);
        History.LoadDemo();
        Connections.LoadDemo();
        SelectedNavIndex = page;
    }

    public async Task ShutdownAsync()
    {
        Voice.Shutdown();
        await Chat.CloseSessionAsync();
        await Copilot.DisposeAsync();
    }
}
