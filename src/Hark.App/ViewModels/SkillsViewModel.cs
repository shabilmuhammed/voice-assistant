using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hark.Core;

namespace Hark.App.ViewModels;

/// <summary>Copilot skills: the list behind the "/" menu, and the editor that creates and updates them.</summary>
public sealed partial class SkillsViewModel(MainViewModel main) : ObservableObject
{
    private SkillInfo? _editing;
    private bool _demo;

    /// <summary>Every skill, sorted by name. The chat's "/" menu reads this list.</summary>
    public ObservableCollection<SkillInfo> Items { get; } = new();

    public string Folder => SkillLibrary.PersonalFolder;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _note = "";

    // ---- editor ----

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(SaveLabel))]
    private bool _isNew;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editDescription = "";

    [ObservableProperty]
    private string _editArgumentHint = "";

    [ObservableProperty]
    private string _editInstructions = "";

    [ObservableProperty]
    private string _error = "";

    public string EditorTitle => IsNew ? "New skill" : $"Edit /{EditName}";
    public string SaveLabel => IsNew ? "Create skill" : "Save changes";

    /// <summary>Raised after a skill is created or changed, so a running chat can reload its skills.</summary>
    public event Action? Changed;

    partial void OnEditNameChanged(string value)
    {
        // Typing a name with capitals or spaces turns it into a valid one as you go.
        if (!IsNew) return;
        var slug = SkillFile.Slug(value);
        if (slug != value && value.Length > 0 && !value.EndsWith(' ') && !value.EndsWith('-')) EditName = slug;
    }

    /// <summary>Reloads the list. Asks the Copilot runtime when connected (it also knows built-in and plugin skills), and always reads the personal folder.</summary>
    public async Task RefreshAsync()
    {
        if (_demo) return;
        var found = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        if (await main.Copilot.DiscoverSkillsAsync([Folder]) is { } fromRuntime)
            foreach (var skill in fromRuntime) found.TryAdd(skill.Name, skill);
        foreach (var skill in SkillLibrary.Scan(Folder)) found[skill.Name] = skill; // the file on disk is the newest truth
        Replace(found.Values);
    }

    private void Replace(IEnumerable<SkillInfo> skills)
    {
        Items.Clear();
        foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) Items.Add(skill);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    public void New()
    {
        _editing = null;
        IsNew = true;
        EditName = "";
        EditDescription = "";
        EditArgumentHint = "";
        EditInstructions = "";
        Error = "";
        Note = "";
        IsEditing = true;
    }

    [RelayCommand]
    private void Edit(SkillInfo? skill)
    {
        if (skill is not { Editable: true }) return;
        try
        {
            var text = SkillLibrary.Load(skill);
            var parsed = SkillFile.Parse(text);
            _editing = skill;
            IsNew = false;
            EditName = skill.Name;
            EditDescription = parsed.Fields.TryGetValue("description", out var d) ? d : skill.Description;
            EditArgumentHint = parsed.Fields.TryGetValue("argument-hint", out var h) ? h : skill.ArgumentHint ?? "";
            EditInstructions = parsed.Body;
            Error = "";
            Note = "";
            IsEditing = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Opening the skill {skill.Name} failed", ex);
            Note = $"Could not open this skill: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        Error = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = "";
        var draft = new SkillDraft(EditName.Trim(), EditDescription, string.IsNullOrWhiteSpace(EditArgumentHint) ? null : EditArgumentHint, EditInstructions);
        try
        {
            if (_demo) throw new ArgumentException("Saving is turned off in the sample view.");
            SkillLibrary.Save(draft, IsNew ? null : _editing?.Path);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Saving a skill failed", ex);
            Error = $"Could not save the skill: {ex.Message}";
            return;
        }

        var created = IsNew;
        IsEditing = false;
        await RefreshAsync();
        Changed?.Invoke();
        Note = created
            ? $"Created. Type /{draft.Name} in a chat to use it."
            : $"Saved. Chats use the new version of /{draft.Name} from now on.";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the skills folder failed", ex);
            Note = $"Could not open the folder: {ex.Message}";
        }
    }

    public void LoadDemo(bool editor)
    {
        _demo = true;
        Replace(
        [
            new("release-notes", "Writes release notes from merged pull requests, grouped by feature, in the team's format.", "<version>", "Personal", @"C:\x\SKILL.md", true),
            new("sonarqube-coverage-boost", "Raises unit test coverage for a .NET project until the SonarQube quality gate passes.", "<project>", "Personal", @"C:\x\SKILL.md", true),
            new("standup-bugs", "Turns standup notes into Azure DevOps bugs linked to the current sprint.", null, "Personal", @"C:\x\SKILL.md", true),
            new("customizing-copilot", "Explains how to customise Copilot with instructions, skills and agents.", null, "Built-in", null, false),
        ]);
        if (!editor) return;
        IsNew = false;
        _editing = Items[0];
        EditName = "release-notes";
        EditDescription = "Writes release notes from merged pull requests, grouped by feature, in the team's format.";
        EditArgumentHint = "<version>";
        EditInstructions = "# Release notes\n\n1. List the pull requests merged since the last tag.\n2. Group them by feature area.\n3. Write one plain sentence per change.\n4. Save the notes as release-notes-<version>.md in the generated files folder.";
        IsEditing = true;
    }
}
