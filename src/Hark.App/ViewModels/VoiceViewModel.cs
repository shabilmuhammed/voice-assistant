using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.App.Voice;
using Hark.Core;
using Hark.Voice;

namespace Hark.App.ViewModels;

public enum OverlayState { Hidden, Listening, Working, Answer, Message }

/// <summary>Drives the wake-phrase overlay: listening, the request that was heard, and the answer.</summary>
public sealed partial class VoiceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly VoiceService _service = new();
    private readonly DispatcherTimer _hideTimer;
    private bool _voiceTurn;

    public VoiceViewModel(MainViewModel main)
    {
        _main = main;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _hideTimer.Tick += (_, _) => Hide();

        _service.Woke += () => OnUi(OnWoke);
        _service.Cancelled += () => OnUi(Hide);
        _service.Transcribed += text => OnUi(() => OnTranscribed(text));
        _service.Level += level => OnUi(() => Level = level, DispatcherPriority.Render);

        main.Chat.AssistantTextChanged += text => { if (_voiceTurn) { Answer = text; State = OverlayState.Answer; } };
        main.Chat.TurnEnded += OnTurnEnded;
        main.Chat.AttentionNeeded += () => { if (_voiceTurn) Show(OverlayState.Message, "Hark needs your approval to continue. Its window is open."); };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsListening), nameof(IsWorking), nameof(ShowAnswer), nameof(Caption))]
    private OverlayState _state = OverlayState.Hidden;

    /// <summary>What Hark heard.</summary>
    [ObservableProperty]
    private string _heard = "";

    [ObservableProperty]
    private string _answer = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _message = "";

    /// <summary>Microphone loudness, 0 to 1.</summary>
    [ObservableProperty]
    private float _level;

    /// <summary>Shown in the main window: whether Hark is listening for the wake phrase.</summary>
    [ObservableProperty]
    private string _statusText = "Voice is off";

    [ObservableProperty]
    private bool _isRunning;

    public bool IsVisible => State != OverlayState.Hidden;
    public bool IsListening => State == OverlayState.Listening;
    public bool IsWorking => State == OverlayState.Working;
    public bool ShowAnswer => State == OverlayState.Answer;

    public string Caption => State switch
    {
        OverlayState.Listening => "Listening…",
        OverlayState.Working => "Working…",
        OverlayState.Message => Message,
        _ => "",
    };

    /// <summary>Raised when Esc should be captured (true) or released (false).</summary>
    public event Action<bool>? EscapeWanted;

    /// <summary>Starts or stops listening to match the current settings.</summary>
    public void ApplySettings()
    {
        var settings = _main.Settings;
        if (!settings.VoiceEnabled)
        {
            _service.Stop();
            IsRunning = false;
            StatusText = "Voice is off";
            Hide();
            return;
        }

        IsRunning = _service.Start(settings.WakePhrase, settings.WakeSensitivity, settings.MicrophoneDevice);
        StatusText = IsRunning ? $"Say “{settings.WakePhrase.Trim()}”" : _service.Problem;
    }

    /// <summary>Push to talk, or the Esc key: start listening, or stop if already listening.</summary>
    public void Toggle()
    {
        if (!IsRunning)
        {
            Show(OverlayState.Message, StatusText.Length > 0 ? StatusText : "Voice is off. Turn it on in Settings.");
            return;
        }
        if (State == OverlayState.Listening) _service.Cancel();
        else _service.StartListening();
    }

    [RelayCommand]
    public void Dismiss()
    {
        if (State == OverlayState.Listening) _service.Cancel();
        Hide();
    }

    [RelayCommand]
    private void OpenHark()
    {
        Hide();
        _main.ShowChat();
        _main.RequestAttention();
    }

    private void OnWoke()
    {
        _hideTimer.Stop();
        Heard = "";
        Answer = "";
        Message = "";
        State = OverlayState.Listening;
        EscapeWanted?.Invoke(true);
    }

    private void OnTranscribed(string text)
    {
        EscapeWanted?.Invoke(false);
        text = TranscriptFixes.Apply(text, _main.Settings.VoiceCorrections);
        Heard = text;

        if (_main.Chat.IsBusy)
        {
            Show(OverlayState.Message, "Still working on your last request. Try again when it has finished.");
            return;
        }
        if (!_main.Copilot.IsConnected)
        {
            Show(OverlayState.Message, "Copilot is not connected. Add your GitHub PAT in Hark first.");
            return;
        }

        _voiceTurn = true;
        State = OverlayState.Working;
        _main.Chat.Input = text;
        if (_main.Chat.SendCommand.CanExecute(null)) _main.Chat.SendCommand.Execute(null);
        else
        {
            _voiceTurn = false;
            Show(OverlayState.Message, "That could not be sent. Open Hark to see why.");
        }
    }

    private void OnTurnEnded(string? error)
    {
        if (!_voiceTurn) return;
        _voiceTurn = false;
        if (error is not null) Show(OverlayState.Message, error);
        else if (Answer.Length == 0) Show(OverlayState.Message, "Done. Open Hark to see the details.");
        else
        {
            State = OverlayState.Answer;
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    private void Show(OverlayState state, string message)
    {
        Message = message;
        State = state;
        EscapeWanted?.Invoke(false);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Hide()
    {
        _hideTimer.Stop();
        State = OverlayState.Hidden;
        Level = 0;
        EscapeWanted?.Invoke(false);
    }

    public void LoadDemo(string kind)
    {
        Heard = "What is still open on the ingestion pipeline repo this sprint?";
        switch (kind)
        {
            case "listening":
                Heard = "";
                State = OverlayState.Listening;
                Level = 0.55f;
                break;
            case "working":
                State = OverlayState.Working;
                break;
            default:
                Answer = "Three items are still open in **Sprint 42**:\n\n- #4812 Null schema crashes the bronze loader (Bug)\n- #4790 Add retry policy to the blob trigger (Task)\n- #4755 Backfill job for March partitions (Task)\n\nThe bug is the urgent one.";
                State = OverlayState.Answer;
                break;
        }
    }

    public void Shutdown() => _service.Dispose();

    private static void OnUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal) =>
        Application.Current?.Dispatcher.BeginInvoke(priority, action);
}
