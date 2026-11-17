using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;
using Microsoft.Win32;

namespace Hark.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Hark";

    private readonly MainViewModel _main;
    private bool _loading = true;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _closeToTray = main.Settings.CloseToTray;
        _approval = Approvals.FirstOrDefault(a => a.Mode == main.Settings.ApprovalMode) ?? Approvals[0];
        _alsoWakeOn = string.Join(", ", main.Settings.AlsoWakeOn);
        _wakeOnWords = main.Settings.WakeOnRecognisedWords;
        _mcpConfigPath = main.Settings.McpConfigPath ?? "";
        _reposRoot = main.Settings.ReposRoot ?? "";
        _reposNote = DescribeRepos(_reposRoot);
        _outputFolder = main.Settings.OutputFolder ?? "";
        _voiceEnabled = main.Settings.VoiceEnabled;
        _wakePhrase = main.Settings.WakePhrase;
        _wakeSensitivity = main.Settings.WakeSensitivity;
        foreach (var mic in Hark.App.Voice.VoiceService.Microphones()) Microphones.Add(mic);
        _microphone = Microphones.FirstOrDefault(m => m.Device == main.Settings.MicrophoneDevice) ?? Microphones.FirstOrDefault();
        _startWithWindows = ReadStartWithWindows();
        _azureCliFallback = main.Settings.AzureCliFallback;
        _loading = false;
        if (_azureCliFallback) _ = RefreshAzureCliAsync();
    }

    // ---- Azure CLI fallback ----------------------------------------------------

    [ObservableProperty]
    private bool _azureCliFallback;

    [ObservableProperty]
    private string _azureCliStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInToAzureCommand))]
    private bool _canSignInToAzure;

    partial void OnAzureCliFallbackChanged(bool value)
    {
        if (_loading) return;
        _main.Settings.AzureCliFallback = value;
        Save();
        _ = RefreshAzureCliAsync();
    }

    private async Task RefreshAzureCliAsync()
    {
        CanSignInToAzure = false;
        if (!AzureCliFallback)
        {
            AzureCliStatus = "";
            return;
        }
        if (AzureCli.FindExecutable() is null)
        {
            AzureCliStatus = "The Azure CLI is not installed on this PC, so there is nothing to fall back to. Install it first, or leave this off.";
            return;
        }
        AzureCliStatus = "Checking the Azure CLI sign-in…";
        var signedIn = await AzureCli.HasWorkingSignInAsync();
        if (!AzureCliFallback) return;
        AzureCliStatus = signedIn
            ? "Signed in. Copilot reuses this sign-in without opening the browser until it expires."
            : "Not signed in. Sign in once now, or the browser opens the first time Copilot needs it. The sign-in is then kept on this PC.";
        CanSignInToAzure = !signedIn;
    }

    [RelayCommand(CanExecute = nameof(CanSignInToAzure))]
    private async Task SignInToAzureAsync()
    {
        CanSignInToAzure = false;
        AzureCliStatus = "Finish signing in in the browser window…";
        var ok = await AzureCli.SignInAsync();
        await RefreshAzureCliAsync();
        if (!ok && CanSignInToAzure) AzureCliStatus = "The sign-in did not finish. Try again, or close any other sign-in window first.";
    }

    /// <summary>Screenshot state: the fallback on and not yet signed in. Nothing is saved or run.</summary>
    public void ShowAzureDemo()
    {
        _loading = true;
        AzureCliFallback = true;
        _loading = false;
        AzureCliStatus = "Not signed in. Sign in once now, or the browser opens the first time Copilot needs it. The sign-in is then kept on this PC.";
        CanSignInToAzure = true;
    }

    public ObservableCollection<ModelChoice> Models { get; } = new();

    [ObservableProperty]
    private ModelChoice? _defaultModel;

    [ObservableProperty]
    private bool _closeToTray;

    public sealed record ApprovalChoice(string Mode, string Label, string Explanation)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<ApprovalChoice> Approvals { get; } =
    [
        new("AllowAll", "Allow all", "Tools run without asking, including commands, file edits, and creating or changing work items."),
        new("AskForChanges", "Ask before changes", "Looking things up runs straight away. Anything that creates or changes something asks first."),
        new("AskAlways", "Always ask", "Every tool asks before it runs."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalExplanation))]
    private ApprovalChoice _approval;

    public string ApprovalExplanation => Approval.Explanation;

    [ObservableProperty]
    private string _alsoWakeOn;

    [ObservableProperty]
    private bool _wakeOnWords;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _mcpConfigPath;

    [ObservableProperty]
    private string _reposRoot;

    [ObservableProperty]
    private string _reposNote;

    [ObservableProperty]
    private string _outputFolder;

    public string OutputDefault => AppSettings.DefaultOutputFolder;

    // ---- voice ----

    public ObservableCollection<Hark.App.Voice.Microphone> Microphones { get; } = new();

    [ObservableProperty]
    private bool _voiceEnabled;

    [ObservableProperty]
    private string _wakePhrase;

    [ObservableProperty]
    private int _wakeSensitivity;

    [ObservableProperty]
    private Hark.App.Voice.Microphone? _microphone;

    [ObservableProperty]
    private string _voiceNote = "";

    public VoiceViewModel Voice => _main.Voice;

    partial void OnVoiceEnabledChanged(bool value)
    {
        if (_loading) return;
        _main.Settings.VoiceEnabled = value;
        ApplyVoice();
    }

    partial void OnWakePhraseChanged(string value)
    {
        if (_loading) return;
        var problem = Hark.App.Voice.VoiceService.CheckWakePhrase(value);
        if (problem is not null)
        {
            VoiceNote = problem;
            return;
        }
        _main.Settings.WakePhrase = value.Trim();
        ApplyVoice();
    }

    partial void OnWakeSensitivityChanged(int value)
    {
        if (_loading) return;
        _main.Settings.WakeSensitivity = Math.Clamp(value, 1, 10);
        ApplyVoice();
    }

    partial void OnMicrophoneChanged(Hark.App.Voice.Microphone? value)
    {
        if (_loading || value is null) return;
        _main.Settings.MicrophoneDevice = value.Device;
        ApplyVoice();
    }

    private void ApplyVoice()
    {
        Save();
        _main.Voice.ApplySettings();
        VoiceNote = _main.Settings.VoiceEnabled && !_main.Voice.IsRunning ? _main.Voice.StatusText : "";
    }

    [ObservableProperty]
    private string _note = "";

    public string McpDefaultPath => McpConfig.DefaultPath;
    public string DataFolder => AppPaths.DataDirectory;

    public void RefreshModels()
    {
        _loading = true;
        Models.Clear();
        foreach (var m in _main.Chat.Models) Models.Add(m);
        DefaultModel = Models.FirstOrDefault(m => m.Id == _main.Settings.DefaultModel) ?? Models.FirstOrDefault();
        _loading = false;
    }

    partial void OnDefaultModelChanged(ModelChoice? value)
    {
        if (_loading) return;
        _main.Settings.DefaultModel = value?.Id;
        Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loading) return;
        _main.Settings.CloseToTray = value;
        Save();
    }

    partial void OnApprovalChanged(ApprovalChoice value)
    {
        if (_loading || value is null) return;
        _main.Settings.ApprovalMode = value.Mode;
        Save();
    }

    partial void OnAlsoWakeOnChanged(string value)
    {
        if (_loading) return;
        _main.Settings.AlsoWakeOn = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        ApplyVoice();
    }

    partial void OnWakeOnWordsChanged(bool value)
    {
        if (_loading) return;
        _main.Settings.WakeOnRecognisedWords = value;
        ApplyVoice();
    }

    partial void OnMcpConfigPathChanged(string value)
    {
        if (_loading) return;
        _main.Settings.McpConfigPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        Save();
    }

    partial void OnReposRootChanged(string value)
    {
        if (_loading) return;
        var path = value.Trim().Trim('"');
        _main.Settings.ReposRoot = path.Length == 0 ? null : path;
        ReposNote = DescribeRepos(path);
        Save();
    }

    partial void OnOutputFolderChanged(string value)
    {
        if (_loading) return;
        var path = value.Trim().Trim('"');
        _main.Settings.OutputFolder = path.Length == 0 ? null : path;
        Save();
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_main.Settings.EffectiveOutputFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the output folder failed", ex);
        }
    }

    private static string DescribeRepos(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Not set. Copilot cannot work in your repositories until you set this.";
        if (!Directory.Exists(path)) return "This folder does not exist.";
        try
        {
            var count = Directory.EnumerateDirectories(path).Count(d => Directory.Exists(Path.Combine(d, ".git")));
            return $"{count} repositories found. The change applies to the next new chat.";
        }
        catch (Exception ex)
        {
            return $"Could not read this folder: {ex.Message}";
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            Note = "Saved.";
        }
        catch (Exception ex)
        {
            Log.Error("Changing start-with-Windows failed", ex);
            Note = $"Could not change this setting: {ex.Message}";
        }
    }

    private static bool ReadStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch
        {
            return false;
        }
    }

    private void Save()
    {
        try
        {
            _main.Settings.Save();
            Note = "Saved.";
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Note = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the data folder failed", ex);
        }
    }
}
