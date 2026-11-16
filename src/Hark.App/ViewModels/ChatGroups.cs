using CommunityToolkit.Mvvm.ComponentModel;
using Hark.Core;

namespace Hark.App.ViewModels;

/// <summary>A heading in a list of chats: Today, Yesterday, Earlier.</summary>
public sealed record ChatGroupHeader(string Text);

/// <summary>One saved chat in the sidebar or on the History page.</summary>
public sealed partial class ChatRowVm(Conversation conversation) : ObservableObject
{
    public Conversation Conversation { get; } = conversation;
    public string Title => Conversation.Title;
    public string Preview => Conversation.Preview;
    public bool HasPreview => Preview.Length > 0;
    public string When => Conversation.Updated.Date == DateTime.Today
        ? Conversation.Updated.ToString("HH:mm")
        : Conversation.Updated.ToString("d MMM");

    [ObservableProperty]
    private bool _isCurrent;
}

public static class ChatGroups
{
    /// <summary>Interleaves day headings with chats, newest first.</summary>
    public static List<object> Build(IEnumerable<Conversation> conversations, long? currentId)
    {
        var items = new List<object>();
        string? last = null;
        foreach (var c in conversations)
        {
            var day = c.Updated.Date;
            var heading = day == DateTime.Today ? "Today"
                : day == DateTime.Today.AddDays(-1) ? "Yesterday"
                : day > DateTime.Today.AddDays(-7) ? "This week"
                : "Earlier";
            if (heading != last)
            {
                items.Add(new ChatGroupHeader(heading));
                last = heading;
            }
            items.Add(new ChatRowVm(c) { IsCurrent = c.Id == currentId });
        }
        return items;
    }
}
