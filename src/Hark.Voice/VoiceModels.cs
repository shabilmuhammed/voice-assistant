namespace Hark.Voice;

/// <summary>Locations of the offline voice model files.</summary>
public sealed class VoiceModels
{
    private const string KwsFolder = "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01";
    private const string SttFolder = "sherpa-onnx-moonshine-base-en-int8";

    private VoiceModels(string root) => Root = root;

    public string Root { get; }

    public string KwsEncoder => Path.Combine(Root, KwsFolder, "encoder-epoch-12-avg-2-chunk-16-left-64.onnx");
    public string KwsDecoder => Path.Combine(Root, KwsFolder, "decoder-epoch-12-avg-2-chunk-16-left-64.onnx");
    public string KwsJoiner => Path.Combine(Root, KwsFolder, "joiner-epoch-12-avg-2-chunk-16-left-64.onnx");
    public string KwsTokens => Path.Combine(Root, KwsFolder, "tokens.txt");
    public string KwsBpeModel => Path.Combine(Root, KwsFolder, "bpe.model");

    public string SttPreprocessor => Path.Combine(Root, SttFolder, "preprocess.onnx");
    public string SttEncoder => Path.Combine(Root, SttFolder, "encode.int8.onnx");
    public string SttUncachedDecoder => Path.Combine(Root, SttFolder, "uncached_decode.int8.onnx");
    public string SttCachedDecoder => Path.Combine(Root, SttFolder, "cached_decode.int8.onnx");
    public string SttTokens => Path.Combine(Root, SttFolder, "tokens.txt");

    public string Vad => Path.Combine(Root, "silero_vad.onnx");

    private IEnumerable<string> All =>
    [
        KwsEncoder, KwsDecoder, KwsJoiner, KwsTokens, KwsBpeModel,
        SttPreprocessor, SttEncoder, SttUncachedDecoder, SttCachedDecoder, SttTokens, Vad,
    ];

    /// <summary>The first missing file, or null when everything is present.</summary>
    public string? FirstMissing() => All.FirstOrDefault(f => !File.Exists(f));

    /// <summary>
    /// Looks for a "models" folder next to the app, then in each parent folder
    /// (so a build run from the source tree finds the repo's models folder).
    /// </summary>
    public static VoiceModels? Find(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = new VoiceModels(Path.Combine(dir.FullName, "models"));
            if (Directory.Exists(candidate.Root) && candidate.FirstMissing() is null) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
