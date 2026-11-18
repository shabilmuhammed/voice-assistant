// Parked: the voice members of src/Hark.App/ViewModels/SettingsViewModel.cs, kept for when voice comes back.
// Not compiled. To restore, put these members back into SettingsViewModel (it is a partial class, so this file can
// also be added to the app as it is) and add the constructor lines below to its constructor, before _loading = false.
//
//     _alsoWakeOn = string.Join(", ", main.Settings.AlsoWakeOn);
//     _wakeOnWords = main.Settings.WakeOnRecognisedWords;
//     _voiceEnabled = main.Settings.VoiceEnabled;
//     _wakePhrase = main.Settings.WakePhrase;
//     _wakeSensitivity = main.Settings.WakeSensitivity;
//     foreach (var mic in Hark.App.Voice.VoiceService.Microphones()) Microphones.Add(mic);
//     _microphone = Microphones.FirstOrDefault(m => m.Device == main.Settings.MicrophoneDevice) ?? Microphones.FirstOrDefault();

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hark.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private string _alsoWakeOn;

    [ObservableProperty]
    private bool _wakeOnWords;

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
}
