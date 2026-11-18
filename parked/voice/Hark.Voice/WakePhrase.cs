using System.Text;

namespace Hark.Voice;

/// <summary>Turns a typed wake phrase such as "Hey Chat" into the token line the keyword spotter needs.</summary>
public sealed class WakePhrase
{
    private const char WordStart = '\u2581'; // "▁", how the model marks the start of a word

    // Piece -> score (log probability) from the model's SentencePiece unigram vocabulary.
    private readonly Dictionary<string, float> _score = new(StringComparer.Ordinal);
    private readonly int _longest;

    public WakePhrase(VoiceModels models)
    {
        ReadPieces(File.ReadAllBytes(models.KwsBpeModel));
        _longest = _score.Keys.Max(k => k.Length);
    }

    /// <summary>
    /// Reads the piece list out of a SentencePiece .model file. The file is a protobuf message whose
    /// field 1 repeats { 1: piece text, 2: score (float), 3: type }; type 1 (or absent) is a normal piece.
    /// </summary>
    private void ReadPieces(byte[] data)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (field == 1 && wire == 2)
            {
                var length = (int)ReadVarint(data, ref pos);
                ReadPiece(data.AsSpan(pos, length));
                pos += length;
            }
            else Skip(data, ref pos, wire);
        }
    }

    private void ReadPiece(ReadOnlySpan<byte> message)
    {
        var data = message.ToArray();
        var pos = 0;
        string? piece = null;
        var score = 0f;
        var type = 1;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (field == 1 && wire == 2)
            {
                var length = (int)ReadVarint(data, ref pos);
                piece = Encoding.UTF8.GetString(data, pos, length);
                pos += length;
            }
            else if (field == 2 && wire == 5)
            {
                score = BitConverter.ToSingle(data, pos);
                pos += 4;
            }
            else if (field == 3 && wire == 0) type = (int)ReadVarint(data, ref pos);
            else Skip(data, ref pos, wire);
        }
        if (piece is not null && type == 1) _score[piece] = score;
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
    }

    private static void Skip(byte[] data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(data, ref pos); break;
            case 1: pos += 8; break;
            case 2:
                var length = (int)ReadVarint(data, ref pos); // read first: it moves pos past the length itself
                pos += length;
                break;
            case 5: pos += 4; break;
            default: throw new InvalidDataException("Unexpected data in the tokenizer model file.");
        }
    }

    /// <summary>Letters, spaces and apostrophes only, upper case, single spaces.</summary>
    public static string Normalize(string phrase)
    {
        var sb = new StringBuilder();
        foreach (var ch in phrase.ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or '\'') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>For "Hey Chat": "▁HE Y ▁CHA T". Empty when the phrase cannot be spelled with the model's pieces.</summary>
    public string Tokens(string phrase)
    {
        var normalized = Normalize(phrase);
        if (normalized.Length == 0) return "";
        var text = WordStart + normalized.Replace(' ', WordStart);

        // Unigram segmentation: the split into known pieces with the highest total score (Viterbi).
        var best = new float[text.Length + 1];
        var from = new int[text.Length + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (var end = 1; end <= text.Length; end++)
            for (var start = Math.Max(0, end - _longest); start < end; start++)
            {
                if (float.IsNegativeInfinity(best[start])) continue;
                if (!_score.TryGetValue(text[start..end], out var score)) continue;
                if (best[start] + score > best[end]) (best[end], from[end]) = (best[start] + score, start);
            }
        if (float.IsNegativeInfinity(best[^1])) return "";

        var pieces = new List<string>();
        for (var end = text.Length; end > 0; end = from[end]) pieces.Add(text[from[end]..end]);
        pieces.Reverse();
        return string.Join(' ', pieces);
    }

    /// <summary>
    /// The keywords-file line. Threshold is the spotter's trigger probability:
    /// lower wakes more easily, higher mis-fires less.
    /// </summary>
    public string KeywordLine(string phrase, double threshold)
    {
        var tokens = Tokens(phrase);
        return tokens.Length == 0
            ? ""
            : $"{tokens} #{threshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} @{Normalize(phrase).Replace(' ', '_')}";
    }

    /// <summary>Maps the 1 (strict) to 10 (loose) slider onto a threshold between 0.60 and 0.25.</summary>
    public static double ThresholdFor(int sensitivity)
    {
        var s = Math.Clamp(sensitivity, 1, 10);
        return Math.Round(0.60 - (s - 1) * (0.35 / 9.0), 2);
    }
}
