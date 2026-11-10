using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;

namespace Hark.App.ViewModels;

public abstract partial class MessageVm : ObservableObject;

public sealed partial class UserMessageVm(string text) : MessageVm
{
    public string Text { get; } = text;
}

public sealed partial class AssistantMessageVm(string messageId, string text) : MessageVm
{
    public string MessageId { get; } = messageId;

    [ObservableProperty]
    private string _text = text;
}

public sealed partial class NoteVm(string text) : MessageVm
{
    public string Text { get; } = text;
}

public sealed partial class ErrorVm(string text) : MessageVm
{
    public string Text { get; } = text;
}

public sealed partial class ToolCallVm(string callId, string name) : MessageVm
{
    public string CallId { get; } = callId;
    public string Name { get; } = name;

    [ObservableProperty]
    private string _status = "Running";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private bool _isRunning = true;
}

/// <summary>An inline "may this tool run?" card. The task completes when the user picks an answer.</summary>
public sealed partial class ApprovalVm : MessageVm
{
    private readonly TaskCompletionSource<ToolApproval> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _alwaysAllow;

    public ApprovalVm(ToolApprovalRequest request, Action<string> alwaysAllow)
    {
        Request = request;
        _alwaysAllow = alwaysAllow;
    }

    public ToolApprovalRequest Request { get; }
    public string Title => $"Allow {Request.Title}?";
    public string Detail => Request.Detail;
    public Task<ToolApproval> Answer => _answer.Task;

    [ObservableProperty]
    private bool _isPending = true;

    [ObservableProperty]
    private string _outcome = "";

    [RelayCommand]
    private void AllowOnce() => Resolve(ToolApproval.AllowOnce, "Allowed once");

    [RelayCommand]
    private void AlwaysAllow()
    {
        _alwaysAllow(Request.Key);
        Resolve(ToolApproval.AllowOnce, "Allowed for the rest of this session");
    }

    [RelayCommand]
    private void Deny() => Resolve(ToolApproval.Deny, "Denied");

    public void Cancel() => Resolve(ToolApproval.Deny, "Cancelled");

    private void Resolve(ToolApproval answer, string outcome)
    {
        if (!IsPending) return;
        IsPending = false;
        Outcome = outcome;
        _answer.TrySetResult(answer);
    }
}
