using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hark.App.ViewModels;

public enum QuickPromptState { Hidden, Typing, Working, Answer, Message }

/// <summary>
/// The quick prompt: Ctrl + Alt + Space opens a small box above other windows. What you type is sent to the
/// current chat, and the answer shows in the same panel. The chat is saved like any other.
/// </summary>
public sealed partial class QuickPromptViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _hideTimer;
    private bool _quickTurn;
    private bool _held;

    public QuickPromptViewModel(MainViewModel main)
    {
        _main = main;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _hideTimer.Tick += (_, _) => Hide();

        main.Chat.AssistantTextChanged += text =>
        {
            if (!_quickTurn) return;
            Answer = text;
            if (State == QuickPromptState.Working) State = QuickPromptState.Answer;
        };
        main.Chat.TurnEnded += OnTurnEnded;
        main.Chat.AttentionNeeded += () =>
        {
            if (_quickTurn && State != QuickPromptState.Typing)
                Show(QuickPromptState.Message, "MarTechChat needs your approval to continue. Its window is open.");
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsTyping), nameof(IsWorking), nameof(ShowAnswer), nameof(ShowAsked), nameof(Caption))]
    private QuickPromptState _state = QuickPromptState.Hidden;

    /// <summary>What is being typed. Kept when the box closes without sending.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _prompt = "";

    /// <summary>The request that was sent, shown above the answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAsked))]
    private string _asked = "";

    [ObservableProperty]
    private string _answer = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _message = "";

    public bool IsVisible => State != QuickPromptState.Hidden;
    public bool IsTyping => State == QuickPromptState.Typing;
    public bool IsWorking => State == QuickPromptState.Working;
    public bool ShowAnswer => State == QuickPromptState.Answer;
    public bool ShowAsked => Asked.Length > 0 && State is QuickPromptState.Working or QuickPromptState.Answer;

    public string Caption => State switch
    {
        QuickPromptState.Working => "Working…",
        QuickPromptState.Message => Message,
        _ => "",
    };

    /// <summary>Ctrl + Alt + Space: open the box, or close it if it is already open.</summary>
    public void Toggle()
    {
        if (State == QuickPromptState.Typing) Hide();
        else Open();
    }

    /// <summary>Opens the box, with any text that was not sent last time.</summary>
    public void Open()
    {
        _hideTimer.Stop();
        Message = "";
        State = QuickPromptState.Typing;
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        var text = Prompt.Trim();
        if (text.Length == 0) return;
        if (_main.Chat.IsBusy)
        {
            Show(QuickPromptState.Message, "Still working on your last request. Try again when it has finished.");
            return;
        }
        if (!_main.Copilot.IsConnected)
        {
            Show(QuickPromptState.Message, "Copilot is not connected. Add your GitHub PAT in MarTechChat first.");
            return;
        }

        Asked = text;
        Answer = "";
        _quickTurn = true;
        State = QuickPromptState.Working;

        // Send through the chat, keeping anything half-typed in the main window's message box.
        var draft = _main.Chat.Input;
        _main.Chat.Input = text;
        if (_main.Chat.SendCommand.CanExecute(null)) _main.Chat.SendCommand.Execute(null);
        var started = _main.Chat.IsBusy;
        if (_main.Chat.Input != text) _main.Chat.Input = draft;
        if (started)
        {
            Prompt = "";
            return;
        }
        _quickTurn = false;
        Show(QuickPromptState.Message, "That could not be sent. Open MarTechChat to see why.");
    }

    [RelayCommand]
    public void Dismiss() => Hide();

    [RelayCommand]
    private void OpenApp()
    {
        Hide();
        _main.ShowChat();
        _main.RequestAttention();
    }

    /// <summary>Keeps the answer up while the pointer is over the panel.</summary>
    public void HoldOpen(bool held)
    {
        _held = held;
        if (held) _hideTimer.Stop();
        else if (State is QuickPromptState.Answer or QuickPromptState.Message) _hideTimer.Start();
    }

    private void OnTurnEnded(string? error)
    {
        if (!_quickTurn) return;
        _quickTurn = false;
        if (State == QuickPromptState.Typing) return; // a new request is being typed; the answer is in the chat
        if (error is not null) Show(QuickPromptState.Message, error);
        else if (Answer.Length == 0) Show(QuickPromptState.Message, "Done. Open MarTechChat to see the details.");
        else
        {
            State = QuickPromptState.Answer;
            RestartHideTimer();
        }
    }

    private void Show(QuickPromptState state, string message)
    {
        Message = message;
        State = state;
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (!_held) _hideTimer.Start();
    }

    private void Hide()
    {
        _hideTimer.Stop();
        State = QuickPromptState.Hidden;
    }

    /// <summary>Sample content for screenshots. Nothing is sent.</summary>
    public void LoadDemo(string kind)
    {
        Asked = "What is still open on the ingestion pipeline repo this sprint?";
        switch (kind)
        {
            case "typing":
                Prompt = "What is still open on the ingestion pipeline repo this sprint?";
                State = QuickPromptState.Typing;
                break;
            case "working":
                State = QuickPromptState.Working;
                break;
            default:
                Answer = "Three items are still open in **Sprint 42**:\n\n- #4812 Null schema crashes the bronze loader (Bug)\n- #4790 Add retry policy to the blob trigger (Task)\n- #4755 Backfill job for March partitions (Task)\n\nThe bug is the urgent one.";
                State = QuickPromptState.Answer;
                break;
        }
    }
}
