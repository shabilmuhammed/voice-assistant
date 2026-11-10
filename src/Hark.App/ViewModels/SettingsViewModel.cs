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
