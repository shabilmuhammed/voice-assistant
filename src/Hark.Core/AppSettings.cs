using System.Text.Json;

namespace Hark.Core;

/// <summary>User settings, stored as plain JSON on this PC (by the owner's choice, including the PAT).</summary>
public sealed class AppSettings
{
    public string? GitHubPat { get; set; }
    public string? DefaultModel { get; set; }
    public string? McpConfigPath { get; set; }

    /// <summary>Folder that holds the user's cloned repositories, one per subfolder.</summary>
    public string? ReposRoot { get; set; }

    /// <summary>Where files Copilot generates for the user are saved. Empty means Documents\Hark.</summary>
    public string? OutputFolder { get; set; }

    // ---- voice ----

    /// <summary>Listen for the wake phrase while Hark is running.</summary>
    public bool VoiceEnabled { get; set; } = true;

    public string WakePhrase { get; set; } = "Hey Chat";

    /// <summary>1 (strict, fewer false wake-ups) to 10 (loose, wakes more easily).</summary>
    public int WakeSensitivity { get; set; } = 5;

    /// <summary>Microphone to use; -1 is the Windows default device.</summary>
    public int MicrophoneDevice { get; set; } = -1;

    /// <summary>Words the speech recognizer tends to get wrong, and what they should be.</summary>
    public Dictionary<string, string> VoiceCorrections { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["per view"] = "Purview",
        ["perview"] = "Purview",
        ["pure view"] = "Purview",
        ["azure dev ops"] = "Azure DevOps",
    };

    /// <summary>True once the first-run default for ReposRoot has been considered.</summary>
    public bool ReposRootSuggested { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutoApproveReadOnlyTools { get; set; } = true;
    public List<string> DisabledServers { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string EffectiveMcpConfigPath =>
        string.IsNullOrWhiteSpace(McpConfigPath) ? McpConfig.DefaultPath : McpConfigPath;

    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Hark");

    /// <summary>The output folder in use; created if it does not exist yet.</summary>
    public string EffectiveOutputFolder
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder : OutputFolder;
            try { Directory.CreateDirectory(path); } catch (Exception ex) { Log.Error($"Could not create {path}", ex); }
            return path;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings.json; starting with defaults.", ex);
        }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}
