using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;

namespace Hark.App.ViewModels;

public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>Day headings (ChatGroupHeader) interleaved with chats (ChatRowVm).</summary>
    public ObservableCollection<object> Items { get; } = new();

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyText = "No chats yet. Chats you have are saved on this PC and listed here.";

    /// <summary>Raised when the search box should take the keyboard focus.</summary>
    public event Action? SearchFocusRequested;

    public void FocusSearch() => SearchFocusRequested?.Invoke();

    partial void OnSearchChanged(string value) => Refresh();

    public void Refresh()
    {
        try
        {
            Items.Clear();
            foreach (var item in ChatGroups.Build(main.Store.ListConversations(Search), main.Chat.CurrentConversationId)) Items.Add(item);
        }
        catch (Exception ex)
        {
            Log.Error("Loading history failed", ex);
        }
        IsEmpty = Items.Count == 0;
        EmptyText = Search.Trim().Length > 0
            ? "No chats match that search."
            : "No chats yet. Chats you have are saved on this PC and listed here.";
    }

    public void LoadDemo()
    {
        var now = DateTime.Now;
        Conversation[] sample =
        [
            new(1, "Ingestion pipeline: sprint status", "auto", null, now, now.AddMinutes(-12), "Three items are still open in Sprint 42…"),
            new(2, "Lineage for customer_orders", "auto", null, now, now.AddHours(-3), "The asset is fed by two Synapse pipelines and lands in the curated zone."),
            new(3, "Where is the retry logic for the blob trigger?", "auto", null, now, now.AddDays(-1), "It lives in src/triggers/blob.py, lines 40 to 88."),
            new(4, "Bugs from the standup notes", "auto", null, now, now.AddDays(-1).AddHours(-4), "Created 4 bugs and linked them to feature #4701."),
            new(5, "Sensitivity labels on the finance lake", "auto", null, now, now.AddDays(-9), "12 assets carry the Confidential label."),
        ];
        Items.Clear();
        foreach (var item in ChatGroups.Build(sample, 1)) Items.Add(item);
        IsEmpty = false;
    }

    [RelayCommand]
    private async Task OpenAsync(ChatRowVm? row)
    {
        if (row is null) return;
        await main.Chat.OpenConversationAsync(row.Conversation);
        main.ShowChat();
        main.RefreshRecents();
    }

    [RelayCommand]
    private async Task DeleteAsync(ChatRowVm? row)
    {
        if (row is null) return;
        if (main.Chat.CurrentConversationId == row.Conversation.Id) await main.Chat.NewChatCommand.ExecuteAsync(null);
        main.Store.DeleteConversation(row.Conversation.Id);
        Refresh();
        main.RefreshRecents();
        main.SelectedNavIndex = 1;
    }
}
