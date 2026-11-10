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
        _autoApproveReadOnlyTools = main.Settings.AutoApproveReadOnlyTools;
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
        _loading = false;
    }

    public ObservableCollection<ModelChoice> Models { get; } = new();

    [ObservableProperty]
    private ModelChoice? _defaultModel;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _autoApproveReadOnlyTools;

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

    partial void OnAutoApproveReadOnlyToolsChanged(bool value)
    {
        if (_loading) return;
        _main.Settings.AutoApproveReadOnlyTools = value;
        Save();
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
