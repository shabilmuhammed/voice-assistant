using Hark.Voice;
using Xunit;
using Xunit.Abstractions;

namespace Hark.Voice.Tests;

/// <summary>
/// Runs the real offline models on recorded clips (synthesized speech, one voice).
/// The clips say:
///   hey-chat-question     "Hey chat, what is still open this sprint?"
///   hey-chat-midsentence  "Okay so, hey chat, list the Purview collections."
///   hey-chad              "Hey Chad, how are you doing today?"
///   no-wake               a sentence with no wake phrase
///   question-only         "Create a bug in Azure DevOps for the null schema failure and assign it to me."
/// </summary>
public class VoiceTests(ITestOutputHelper output)
{
    private static readonly VoiceModels? Models = VoiceModels.Find();

    private sealed record Run(int Wakes, int Cancels, List<string> Texts);

    private static float[] ReadWav(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", name + ".wav"));
        var data = 12;
        while (!(bytes[data] == 'd' && bytes[data + 1] == 'a' && bytes[data + 2] == 't' && bytes[data + 3] == 'a'))
            data += 8 + BitConverter.ToInt32(bytes, data + 4);
        var length = BitConverter.ToInt32(bytes, data + 4);
        var samples = new float[length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + 8 + i * 2) / 32768f;
        return samples;
    }

    /// <summary>Feeds the clip the way a microphone would: 100 ms at a time, then some silence.</summary>
    private static Run Play(VoiceEngine engine, string clip, double trailingSilenceSeconds = 3)
    {
        var wakes = 0;
        var cancels = 0;
        var texts = new List<string>();
        engine.Woke += () => wakes++;
        engine.Cancelled += () => cancels++;
        engine.Transcribed += texts.Add;

        var audio = ReadWav(clip).Concat(new float[(int)(VoiceEngine.SampleRate * trailingSilenceSeconds)]).ToArray();
        const int chunk = VoiceEngine.SampleRate / 10;
        for (var i = 0; i < audio.Length; i += chunk)
            engine.Feed(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
        return new Run(wakes, cancels, texts);
    }

    // Expected values come from the model's own SentencePiece tokenizer.
    [Theory]
    [InlineData("Hey Chat", "▁HE Y ▁CHA T")]
    [InlineData("hey, siri!", "▁HE Y ▁S I RI")]
    [InlineData("Okay computer", "▁OKAY ▁COMP U TER")]
    [InlineData("Hey Hark", "▁HE Y ▁HA R K")]
    [InlineData("Hey Copilot", "▁HE Y ▁CO P IL O T")]
    [InlineData("Hello world", "▁HE LL O ▁WORLD")]
    [InlineData("Wake up assistant", "▁WA KE ▁UP ▁AS S IST ANT")]
    [InlineData("Good morning Jarvis", "▁GOOD ▁MO R N ING ▁JA R VI S")]
    [InlineData("Listen up", "▁LI S T EN ▁UP")]
    [InlineData("Hey buddy", "▁HE Y ▁BU D D Y")]
    [InlineData("Don't panic", "▁DON ' T ▁PA N IC")]
    [InlineData("Excuse me", "▁EX C U SE ▁ME")]
    [InlineData("Xylophone quiz", "▁ X Y LO PH ONE ▁QU I Z")]
    [InlineData("Hey Martech", "▁HE Y ▁MAR TE CH")]
    [InlineData("123 !!", "")]
    public void Tokenizes_wake_phrases_like_the_model_expects(string phrase, string expected)
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        Assert.Equal(expected, new WakePhrase(Models).Tokens(phrase));
    }

    [Fact]
    public void Builds_the_keyword_line_with_threshold_and_label()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        Assert.Equal("▁HE Y ▁CHA T #0.44 @HEY_CHAT", new WakePhrase(Models).KeywordLine("Hey Chat", WakePhrase.ThresholdFor(5)));
    }

    [Fact]
    public void Sensitivity_maps_to_a_sensible_threshold_range()
    {
        Assert.Equal(0.60, WakePhrase.ThresholdFor(1));
        Assert.Equal(0.25, WakePhrase.ThresholdFor(10));
        Assert.True(WakePhrase.ThresholdFor(3) > WakePhrase.ThresholdFor(7));
    }

    [Theory]
    [InlineData("hey-chat-question", "what is still open", "sprint")]
    [InlineData("hey-chat-midsentence", "list the", "collections")]
    public void Wakes_on_the_phrase_and_transcribes_what_follows(string clip, string word1, string word2)
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Hey Chat", sensitivity: 5);

        var run = Play(engine, clip);
        output.WriteLine(string.Join(" | ", run.Texts));

        Assert.Equal(1, run.Wakes);
        var text = Assert.Single(run.Texts).ToLowerInvariant();
        Assert.Contains(word1, text);
        Assert.Contains(word2, text);
        Assert.DoesNotContain("hey chat", text); // the wake phrase itself is not part of the request
        Assert.Equal(VoiceState.Idle, engine.State);
    }

    [Theory]
    [InlineData("Chat, what is open?", "What is open?")]
    [InlineData("Hey chat what is open?", "What is open?")]
    [InlineData("hey Chat. list the bugs", "List the bugs")]
    [InlineData("What is open?", "What is open?")]
    [InlineData("Chatter about the release", "Chatter about the release")]
    [InlineData("Chat", "Chat")]
    public void Strips_a_leading_wake_phrase_from_the_transcript(string heard, string expected) =>
        Assert.Equal(expected, VoiceEngine.StripWakePhrase(heard, ["HEY", "CHAT"]));

    [Theory]
    [InlineData("List the per view collections.", "List the Purview collections.")]
    [InlineData("Search Perview for customer orders", "Search Purview for customer orders")]
    [InlineData("Open azure  dev ops", "Open Azure DevOps")]
    [InlineData("An overview of the sprint", "An overview of the sprint")]
    [InlineData("Supervisor review", "Supervisor review")]
    public void Corrects_commonly_misheard_words(string heard, string expected)
    {
        var fixes = new Dictionary<string, string> { ["per view"] = "Purview", ["perview"] = "Purview", ["azure dev ops"] = "Azure DevOps" };
        Assert.Equal(expected, TranscriptFixes.Apply(heard, fixes));
    }

    [Fact]
    public void Stays_asleep_without_the_wake_phrase()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Hey Chat", sensitivity: 5);
        var run = Play(engine, "no-wake");
        Assert.Equal(0, run.Wakes);
        Assert.Empty(run.Texts);
    }

    [Fact]
    public void A_strict_setting_ignores_a_similar_sounding_name()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Hey Chat", sensitivity: 3);
        Assert.Equal(0, Play(engine, "hey-chad").Wakes);
    }

    [Fact]
    public void Push_to_talk_skips_the_wake_phrase()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Hey Chat", sensitivity: 5);
        engine.StartListening();
        var run = Play(engine, "question-only");
        output.WriteLine(string.Join(" | ", run.Texts));
        var text = Assert.Single(run.Texts).ToLowerInvariant();
        Assert.Contains("azure", text);
        Assert.Contains("assign", text);
    }

    [Fact]
    public void Gives_up_quietly_when_nothing_is_said_after_waking()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Hey Chat", sensitivity: 5) { NoSpeechTimeoutSeconds = 2 };
        var cancels = 0;
        engine.Cancelled += () => cancels++;
        engine.StartListening();
        var silence = new float[VoiceEngine.SampleRate / 10];
        for (var i = 0; i < 40; i++) engine.Feed(silence);
        Assert.Equal(1, cancels);
        Assert.Equal(VoiceState.Idle, engine.State);
    }

    [Fact]
    public void The_wake_phrase_can_be_changed_while_running()
    {
        if (Models is null) { output.WriteLine("Models not downloaded; skipped."); return; }
        using var engine = new VoiceEngine(Models, "Okay Computer", sensitivity: 5);
        Assert.Equal(0, Play(engine, "hey-chat-question").Wakes);
        engine.SetWakePhrase("Hey Chat", 5);
        var wakes = 0;
        engine.Woke += () => wakes++;
        var audio = ReadWav("hey-chat-question");
        engine.Feed(audio);
        Assert.Equal(1, wakes);
    }
}
