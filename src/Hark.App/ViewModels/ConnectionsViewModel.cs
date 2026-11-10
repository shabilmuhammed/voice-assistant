using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Hark.Core;

namespace Hark.App.ViewModels;

public sealed partial class ServerVm : ObservableObject
{
    private readonly Action<ServerVm> _changed;

    public ServerVm(string name, string description, bool enabled, Action<ServerVm> changed)
    {
        Name = name;
        Description = description;
        _enabled = enabled;
        _changed = changed;
    }

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => _changed(this);
}

public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ConnectionsViewModel(MainViewModel main)
    {
        _main = main;
        _hasSavedPat = !string.IsNullOrWhiteSpace(main.Settings.GitHubPat);
        _patStatus = _hasSavedPat ? "A token is saved on this PC." : "No token saved yet.";
    }

    /// <summary>Typed into the password box; never shown back.</summary>
    public string PendingPat { get; set; } = "";

    [ObservableProperty]
    private bool _hasSavedPat;

    [ObservableProperty]
    private string _patStatus;

    [ObservableProperty]
    private bool _patOk;

    [ObservableProperty]
    private bool _isTesting;

    public ObservableCollection<ServerVm> Servers { get; } = new();

    [ObservableProperty]
    private string _mcpPath = "";

    [ObservableProperty]
    private string _mcpStatus = "";

    [ObservableProperty]
    private string _warnings = "";

    /// <summary>Raised after a successful save so the view can clear the password box.</summary>
    public event Action? PatSaved;

    [RelayCommand]
    private async Task SaveAndTestAsync()
    {
        var pat = string.IsNullOrWhiteSpace(PendingPat) ? _main.Settings.GitHubPat : PendingPat.Trim();
        if (string.IsNullOrWhiteSpace(pat))
        {
            PatOk = false;
            PatStatus = "Paste your fine-grained PAT first.";
            return;
        }

        IsTesting = true;
        PatStatus = "Checking the token with GitHub…";
        try
        {
            var result = await _main.ConnectAsync(pat);
            PatOk = result.Ok;
            if (result.Ok)
            {
                _main.Settings.GitHubPat = pat;
                _main.Settings.Save();
                HasSavedPat = true;
                PendingPat = "";
                PatSaved?.Invoke();
                PatStatus = $"{result.Message}. {_main.Chat.Models.Count} models available.";
            }
            else
            {
                PatStatus = $"GitHub did not accept this token: {result.Message}";
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    public void Reload()
    {
        Servers.Clear();
        Warnings = "";
        McpPath = _main.Settings.EffectiveMcpConfigPath;

        if (!File.Exists(McpPath))
        {
            McpStatus = "This file does not exist. Set the path under Settings.";
            return;
        }

        try
        {
            var loaded = McpConfig.Load(McpPath);
            Log.AddSecrets(loaded.Secrets);
            foreach (var (name, config) in loaded.Servers.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                Servers.Add(new ServerVm(name, Describe(config), !_main.Settings.DisabledServers.Contains(name), OnServerToggled));
            Warnings = string.Join(Environment.NewLine, loaded.Warnings);
            McpStatus = Servers.Count == 1 ? "1 server found." : $"{Servers.Count} servers found.";
        }
        catch (Exception ex)
        {
            Log.Error("Reading mcp.json failed", ex);
            McpStatus = $"Could not read this file: {ex.Message}";
        }
    }

    private void OnServerToggled(ServerVm server)
    {
        var disabled = _main.Settings.DisabledServers;
        disabled.RemoveAll(n => n == server.Name);
        if (!server.Enabled) disabled.Add(server.Name);
        _main.Settings.Save();
        McpStatus = "Saved. The change applies to the next new chat.";
    }

    private static string Describe(McpServerConfig config) => config switch
    {
        McpStdioServerConfig s when s.Command.Equals("dotnet", StringComparison.OrdinalIgnoreCase) => ".NET project on this PC",
        McpStdioServerConfig s when s.Command.Equals("npx", StringComparison.OrdinalIgnoreCase) => "npx package",
        McpStdioServerConfig s => $"Local program ({Path.GetFileName(s.Command)})",
        McpHttpServerConfig h => $"Web server ({SafeHost(h.Url)})",
        _ => "MCP server",
    };

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "remote";

    [RelayCommand]
    private void OpenMcpFile()
    {
        try
        {
            if (File.Exists(McpPath)) Process.Start(new ProcessStartInfo(McpPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening mcp.json failed", ex);
            McpStatus = $"Could not open the file: {ex.Message}";
        }
    }
}
