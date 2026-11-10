using System.Collections.Concurrent;
using Hark.Core;
using Hark.Voice;
using NAudio.Wave;

namespace Hark.App.Voice;

public sealed record Microphone(int Device, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Connects the microphone to the offline voice engine. Audio is captured at 16 kHz mono and handled
/// on one background thread; it never leaves the PC. All events are raised on that background thread.
/// </summary>
public sealed class VoiceService : IDisposable
{
    private BlockingCollection<object>? _queue; // one queue per run, so an old worker can never read new audio
    private Thread? _worker;
    private VoiceEngine? _engine;
    private WaveIn? _mic;
    private volatile bool _running;

    public event Action? Woke;
    public event Action? Cancelled;
    public event Action<string>? Transcribed;
    public event Action<float>? Level;

    /// <summary>Why voice is not running; empty when it is.</summary>
    public string Problem { get; private set; } = "";

    public bool IsRunning => _running;

    public static IReadOnlyList<Microphone> Microphones()
    {
        var list = new List<Microphone> { new(-1, "Windows default") };
        try
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++)
                list.Add(new Microphone(i, WaveIn.GetCapabilities(i).ProductName));
        }
        catch (Exception ex)
        {
            Log.Error("Listing microphones failed", ex);
        }
        return list;
    }

    /// <summary>Checks a wake phrase can be used. Returns an explanation when it cannot.</summary>
    public static string? CheckWakePhrase(string phrase)
    {
        var models = VoiceModels.Find();
        if (models is null) return null; // cannot check without the models
        var words = WakePhrase.Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Use letters only, for example \"Hey Chat\".";
        if (new WakePhrase(models).Tokens(phrase).Length == 0) return "This phrase cannot be recognised. Try plain English words.";
        if (WakePhrase.Normalize(phrase).Replace(" ", "").Length < 5) return "Too short to be reliable. Use two words, for example \"Hey Chat\".";
        return null;
    }

    public bool Start(string wakePhrase, int sensitivity, int device)
    {
        Stop();
        Problem = "";
        try
        {
            var models = VoiceModels.Find();
            if (models is null)
            {
                Problem = "The voice models are missing from this copy of Hark.";
                return false;
            }
            if (WaveIn.DeviceCount == 0)
            {
                Problem = "No microphone was found.";
                return false;
            }

            _engine = new VoiceEngine(models, wakePhrase, sensitivity);
            _engine.Woke += () => Woke?.Invoke();
            _engine.Cancelled += () => Cancelled?.Invoke();
            _engine.Transcribed += text => Transcribed?.Invoke(text);
            _engine.Level += level => Level?.Invoke(level);

            var queue = new BlockingCollection<object>(new ConcurrentQueue<object>());
            var engine = _engine;
            _queue = queue;
            _running = true;
            _worker = new Thread(() => Work(queue, engine)) { IsBackground = true, Name = "Hark voice" };
            _worker.Start();

            _mic = new WaveIn
            {
                DeviceNumber = device >= 0 && device < WaveIn.DeviceCount ? device : -1,
                WaveFormat = new WaveFormat(VoiceEngine.SampleRate, 16, 1),
                BufferMilliseconds = 60,
            };
            _mic.DataAvailable += OnAudio;
            _mic.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Error("The microphone stopped", e.Exception);
            };
            _mic.StartRecording();
            Log.Info($"Voice started: wake phrase \"{wakePhrase}\", sensitivity {sensitivity}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Starting voice failed", ex);
            Problem = $"Voice could not start: {ex.Message}";
            Stop();
            return false;
        }
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        if (!_running || e.BytesRecorded < 2) return;
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        // If transcription falls far behind, drop audio rather than grow without limit.
        var queue = _queue;
        if (queue is { IsAddingCompleted: false, Count: < 400 })
        {
            try { queue.Add(samples); } catch (InvalidOperationException) { /* stopped meanwhile */ }
        }
    }

    /// <summary>Runs until its queue is closed, then releases the engine it owns.</summary>
    private static void Work(BlockingCollection<object> queue, VoiceEngine engine)
    {
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (item)
                    {
                        case float[] samples: engine.Feed(samples); break;
                        case Action<VoiceEngine> command: command(engine); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Voice processing failed", ex);
                }
            }
        }
        finally
        {
            try { engine.Dispose(); } catch (Exception ex) { Log.Error("Releasing the voice engine failed", ex); }
            queue.Dispose();
        }
    }

    /// <summary>Push to talk: start listening without the wake phrase.</summary>
    public void StartListening() => Post(engine => engine.StartListening());

    /// <summary>Stop listening and discard what was heard.</summary>
    public void Cancel() => Post(engine => engine.Cancel());

    private void Post(Action<VoiceEngine> command)
    {
        var queue = _queue;
        if (!_running || queue is null || queue.IsAddingCompleted) return;
        try { queue.Add(command); } catch (InvalidOperationException) { /* stopped meanwhile */ }
    }

    public void Stop()
    {
        _running = false;
        try
        {
            if (_mic is not null)
            {
                _mic.DataAvailable -= OnAudio;
                _mic.StopRecording();
                _mic.Dispose();
                _mic = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the microphone failed", ex);
        }

        // Closing the queue ends the worker, which then releases the engine.
        var queue = _queue;
        _queue = null;
        if (_worker is null) { try { _engine?.Dispose(); } catch { /* never started */ } }
        else try { queue?.CompleteAdding(); } catch (ObjectDisposedException) { }
        _worker = null;
        _engine = null;
    }

    public void Dispose() => Stop();
}
