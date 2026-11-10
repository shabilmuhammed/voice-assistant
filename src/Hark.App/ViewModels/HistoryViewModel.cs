using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;

namespace Hark.App.ViewModels;

public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<Conversation> Items { get; } = new();

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    partial void OnSearchChanged(string value) => Refresh();

    public void Refresh()
    {
        try
        {
            Items.Clear();
            foreach (var c in main.Store.ListConversations(Search)) Items.Add(c);
        }
        catch (Exception ex)
        {
            Log.Error("Loading history failed", ex);
        }
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task OpenAsync(Conversation? conversation)
    {
        if (conversation is null) return;
        await main.Chat.OpenConversationAsync(conversation);
        main.ShowChat();
    }

    [RelayCommand]
    private async Task DeleteAsync(Conversation? conversation)
    {
        if (conversation is null) return;
        if (main.Chat.CurrentConversationId == conversation.Id) await main.Chat.NewChatCommand.ExecuteAsync(null);
        main.Store.DeleteConversation(conversation.Id);
        Refresh();
        main.SelectedNavIndex = 1;
    }
}
