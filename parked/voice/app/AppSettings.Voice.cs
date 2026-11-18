// Parked: the voice settings from src/Hark.Core/AppSettings.cs, kept for when voice comes back. Not compiled.
// To restore, put these properties back into AppSettings.

namespace Hark.Core;

public sealed partial class AppSettings
{
    // ---- voice ----

    /// <summary>Listen for the wake phrase while Hark is running.</summary>
    public bool VoiceEnabled { get; set; } = true;

    public string WakePhrase { get; set; } = "Hey Chat";

    /// <summary>1 (strict, fewer false wake-ups) to 10 (loose, wakes more easily).</summary>
    public int WakeSensitivity { get; set; } = 5;

    /// <summary>Also wake on what was understood, not only on the sound of the phrase. Better with accents.</summary>
    public bool WakeOnRecognisedWords { get; set; } = true;

    /// <summary>Extra phrases that wake Hark, for example how the recognizer hears your wake phrase.</summary>
    public List<string> AlsoWakeOn { get; set; } = new();

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
}
