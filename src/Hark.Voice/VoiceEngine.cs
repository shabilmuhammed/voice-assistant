using SherpaOnnx;

namespace Hark.Voice;

public enum VoiceState { Idle, Listening, Transcribing }

/// <summary>
/// Offline voice pipeline. Feed it 16 kHz mono audio and it:
///   1. waits for the wake phrase,
///   2. records what is said next until the speaker stops,
///   3. turns that into text.
/// Nothing leaves the PC. Not thread-safe: call Feed from one thread.
/// </summary>
public sealed class VoiceEngine : IDisposable
{
    public const int SampleRate = 16000;
    private const int VadWindow = 512;

    /// <summary>Give up if nobody speaks this long after the wake phrase.</summary>
    public double NoSpeechTimeoutSeconds { get; set; } = 6;

    /// <summary>Longest single request.</summary>
    public double MaxRequestSeconds { get; set; } = 45;

    private readonly VoiceModels _models;
    private readonly OfflineRecognizer _recognizer;
    private readonly VoiceActivityDetector _vad;
    private KeywordSpotter _spotter;
    private OnlineStream _spotterStream;

    // The spotter reports the wake phrase roughly half a second after it was said, by which time the
    // speaker is already into the request. Recent audio is kept so that part can be replayed.
    private readonly float[] _recent = new float[SampleRate * 5];
    private int _recentPos;
    private long _fedToSpotter;
    // Tuned on test clips: the phrase is reported 0.4 to 0.5 s after its last sound. Replaying 0.55 s keeps
    // the first words of the request; any tail of the wake phrase that comes with it is stripped from the text.
    // (This .NET binding does not expose the spotter's exact timestamps.)
    private const double ReplaySeconds = 0.55;
    private const long RefreshSpotterAfter = SampleRate * 600L;

    private readonly List<float> _pending = new();   // audio not yet handed to the VAD in whole windows
    private readonly List<float> _speech = new();    // speech collected for the current request
    private int _listenedSamples;
    private bool _heardSpeech;
    private bool _wokeByPhrase;
    private string[] _wakeWords = [];

    public VoiceEngine(VoiceModels models, string wakePhrase, int sensitivity)
    {
        _models = models;

        var stt = new OfflineRecognizerConfig();
        stt.FeatConfig.SampleRate = SampleRate;
        stt.FeatConfig.FeatureDim = 80;
        stt.ModelConfig.Moonshine.Preprocessor = models.SttPreprocessor;
        stt.ModelConfig.Moonshine.Encoder = models.SttEncoder;
        stt.ModelConfig.Moonshine.UncachedDecoder = models.SttUncachedDecoder;
        stt.ModelConfig.Moonshine.CachedDecoder = models.SttCachedDecoder;
        stt.ModelConfig.Tokens = models.SttTokens;
        stt.ModelConfig.NumThreads = 2;
        stt.ModelConfig.Provider = "cpu";
        _recognizer = new OfflineRecognizer(stt);

        var vad = new VadModelConfig();
        vad.SileroVad.Model = models.Vad;
        vad.SileroVad.Threshold = 0.5f;
        vad.SileroVad.MinSilenceDuration = 0.9f;
        vad.SileroVad.MinSpeechDuration = 0.2f;
        vad.SileroVad.MaxSpeechDuration = (float)MaxRequestSeconds;
        vad.SileroVad.WindowSize = VadWindow;
        vad.SampleRate = SampleRate;
        vad.NumThreads = 1;
        _vad = new VoiceActivityDetector(vad, 60);

        (_spotter, _spotterStream) = CreateSpotter(wakePhrase, sensitivity);
    }

    public VoiceState State { get; private set; } = VoiceState.Idle;

    /// <summary>The wake phrase was heard, or StartListening was called.</summary>
    public event Action? Woke;

    /// <summary>Listening ended with nothing said.</summary>
    public event Action? Cancelled;

    /// <summary>The request was understood. Raised on the thread that called Feed.</summary>
    public event Action<string>? Transcribed;

    /// <summary>Loudness of the latest audio, 0 to 1, for the on-screen indicator.</summary>
    public event Action<float>? Level;

    /// <summary>Changes the wake phrase or its sensitivity (1 strict to 10 loose).</summary>
    public void SetWakePhrase(string wakePhrase, int sensitivity)
    {
        var (spotter, stream) = CreateSpotter(wakePhrase, sensitivity);
        _spotterStream.Dispose();
        _spotter.Dispose();
        _spotter = spotter;
        _spotterStream = stream;
        _fedToSpotter = 0;
    }

    private (KeywordSpotter, OnlineStream) CreateSpotter(string wakePhrase, int sensitivity)
    {
        _wakeWords = WakePhrase.Normalize(wakePhrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new WakePhrase(_models).KeywordLine(wakePhrase, WakePhrase.ThresholdFor(sensitivity));
        if (line.Length == 0) throw new ArgumentException("The wake phrase needs at least one letter.", nameof(wakePhrase));

        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = _models.KwsEncoder;
        config.ModelConfig.Transducer.Decoder = _models.KwsDecoder;
        config.ModelConfig.Transducer.Joiner = _models.KwsJoiner;
        config.ModelConfig.Tokens = _models.KwsTokens;
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.KeywordsBuf = line;
        config.KeywordsBufSize = System.Text.Encoding.UTF8.GetByteCount(line);
        var spotter = new KeywordSpotter(config);
        return (spotter, spotter.CreateStream());
    }

    /// <summary>Skips the wake phrase (push to talk, or the mic button).</summary>
    public void StartListening()
    {
        if (State != VoiceState.Idle) return;
        BeginListening();
    }

    /// <summary>Stops listening without sending anything.</summary>
    public void Cancel()
    {
        if (State != VoiceState.Listening) return;
        State = VoiceState.Idle;
        Cancelled?.Invoke();
    }

    /// <summary>Audio in, 16 kHz mono, values between -1 and 1.</summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        Level?.Invoke(Loudness(samples));

        switch (State)
        {
            case VoiceState.Idle:
                FeedSpotter(samples);
                break;
            case VoiceState.Listening:
                FeedListener(samples);
                break;
        }
    }

    private void FeedSpotter(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _recent[_recentPos] = sample;
            _recentPos = (_recentPos + 1) % _recent.Length;
        }
        _fedToSpotter += samples.Length;

        _spotterStream.AcceptWaveform(SampleRate, samples.ToArray());
        while (_spotter.IsReady(_spotterStream))
        {
            _spotter.Decode(_spotterStream);
            var result = _spotter.GetResult(_spotterStream);
            if (string.IsNullOrEmpty(result.Keyword)) continue;

            // The audio just before this moment is already the start of the request.
            var replay = (int)Math.Min(ReplaySeconds * SampleRate, Math.Min(_fedToSpotter, _recent.Length));
            var head = new float[replay];
            for (var i = 0; i < replay; i++)
                head[i] = _recent[((_recentPos - replay + i) % _recent.Length + _recent.Length) % _recent.Length];

            RestartSpotterStream();
            BeginListening();
            _wokeByPhrase = true;
            if (replay > 0) FeedListener(head);
            return;
        }

        // A stream that has been idle for a long time is replaced so its clock and state stay small.
        if (_fedToSpotter > RefreshSpotterAfter) RestartSpotterStream();
    }

    private void RestartSpotterStream()
    {
        _spotterStream.Dispose();
        _spotterStream = _spotter.CreateStream();
        _fedToSpotter = 0;
    }

    private void BeginListening()
    {
        _pending.Clear();
        _speech.Clear();
        _listenedSamples = 0;
        _heardSpeech = false;
        _wokeByPhrase = false;
        _vad.Clear();
        State = VoiceState.Listening;
        Woke?.Invoke();
    }

    private void FeedListener(ReadOnlySpan<float> samples)
    {
        _listenedSamples += samples.Length;
        _pending.AddRange(samples);
        _speech.AddRange(samples); // the request is transcribed from the raw audio, not the VAD's trimmed segments

        var offset = 0;
        var buffer = new float[VadWindow];
        while (_pending.Count - offset >= VadWindow)
        {
            _pending.CopyTo(offset, buffer, 0, VadWindow);
            offset += VadWindow;
            _vad.AcceptWaveform(buffer);
            if (_vad.IsSpeechDetected()) _heardSpeech = true;

            // A segment appears once the speaker has been quiet for MinSilenceDuration.
            if (!_vad.IsEmpty())
            {
                DrainSegments();
                _pending.Clear();
                Finish();
                return;
            }
        }
        _pending.RemoveRange(0, offset);

        var seconds = _listenedSamples / (double)SampleRate;
        if (!_heardSpeech && seconds >= NoSpeechTimeoutSeconds)
        {
            State = VoiceState.Idle;
            Cancelled?.Invoke();
        }
        else if (seconds >= MaxRequestSeconds)
        {
            _vad.Flush();
            DrainSegments();
            Finish();
        }
    }

    private void DrainSegments()
    {
        while (!_vad.IsEmpty()) _vad.Pop();
    }

    private void Finish()
    {
        if (!_heardSpeech || _speech.Count < SampleRate / 5)
        {
            State = VoiceState.Idle;
            Cancelled?.Invoke();
            return;
        }

        State = VoiceState.Transcribing;
        string text;
        try
        {
            text = Transcribe(_speech.ToArray());
            if (_wokeByPhrase) text = StripWakePhrase(text, _wakeWords);
        }
        finally
        {
            _speech.Clear();
            State = VoiceState.Idle;
        }

        if (text.Length == 0) Cancelled?.Invoke();
        else Transcribed?.Invoke(text);
    }

    /// <summary>Speech to text for one clip of 16 kHz mono audio.</summary>
    public string Transcribe(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    /// <summary>
    /// Removes the wake phrase, or the tail end of it, from the start of a transcript:
    /// "Chat, what is open?" and "Hey chat what is open?" both become "What is open?".
    /// </summary>
    public static string StripWakePhrase(string text, IReadOnlyList<string> wakeWords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        static string Bare(string w) => new string(w.Where(char.IsLetter).ToArray()).ToUpperInvariant();

        // Try the longest tail of the phrase first: "HEY CHAT", then "CHAT".
        for (var skip = 0; skip < wakeWords.Count; skip++)
        {
            var tail = wakeWords.Skip(skip).ToList();
            if (words.Count <= tail.Count) continue;
            if (!tail.Select((w, i) => Bare(words[i]) == w).All(match => match)) continue;
            words.RemoveRange(0, tail.Count);
            var rest = string.Join(' ', words).TrimStart(',', '.', '!', '?', ' ');
            return rest.Length == 0 ? "" : char.ToUpperInvariant(rest[0]) + rest[1..];
        }
        return text;
    }

    private static float Loudness(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return (float)Math.Clamp(rms * 6, 0, 1);
    }

    public void Dispose()
    {
        _spotterStream.Dispose();
        _spotter.Dispose();
        _vad.Dispose();
        _recognizer.Dispose();
    }
}
