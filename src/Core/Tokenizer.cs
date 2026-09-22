// Turns model output ids back into text.
//
// The model speaks in SentencePiece word pieces. To assemble text we only
// need the list of pieces, which lives in v3_e2e_rnnt_tokenizer.model. The
// file is a protobuf, but we need exactly one field, so we parse it by hand
// instead of pulling in a native library:
//
//   ModelProto    { repeated SentencePiece pieces = 1 }
//   SentencePiece { optional string piece = 1 }

using System.Text;

namespace GigaPisar.Core;

public sealed class Tokenizer
{
    public IReadOnlyList<string> Pieces { get; }

    /// <summary>Id of the "blank" symbol: the model emits it when it has nothing to say.</summary>
    public int BlankId => Pieces.Count;

    public Tokenizer(string path)
    {
        var data = File.ReadAllBytes(path);
        var pieces = new List<string>(1100);
        int i = 0;
        while (i < data.Length)
        {
            if (!Varint(data, ref i, out ulong tag)) break;
            ulong field = tag >> 3, wire = tag & 7;
            if (field == 1 && wire == 2)
            {
                if (!Varint(data, ref i, out ulong len)) break;
                int end = i + (int)len;
                if (end > data.Length) break;
                pieces.Add(Piece(data, i, end));
                i = end;
            }
            else if (!Skip(data, ref i, wire)) break;
        }
        if (pieces.Count == 0)
            throw new InvalidDataException($"Could not parse tokenizer: {path}");
        Pieces = pieces;
    }

    private static string Piece(byte[] d, int from, int to)
    {
        int i = from;
        while (i < to)
        {
            if (!Varint(d, ref i, out ulong tag)) break;
            ulong field = tag >> 3, wire = tag & 7;
            if (field == 1 && wire == 2)
            {
                if (!Varint(d, ref i, out ulong len)) break;
                int end = Math.Min(i + (int)len, to);
                return Encoding.UTF8.GetString(d, i, end - i);
            }
            if (!Skip(d, ref i, wire)) break;
        }
        return "";
    }

    private static bool Varint(byte[] d, ref int i, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (i < d.Length)
        {
            byte b = d[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    private static bool Skip(byte[] d, ref int i, ulong wire)
    {
        switch (wire)
        {
            case 0: return Varint(d, ref i, out _);
            case 1: i += 8; return i <= d.Length;
            case 2:
                if (!Varint(d, ref i, out ulong len)) return false;
                i += (int)len; return i <= d.Length;
            case 5: i += 4; return i <= d.Length;
            default: return false;
        }
    }

    /// <summary>Joins pieces; the ▁ marker means "space before this word".</summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
            if (id >= 0 && id < Pieces.Count) sb.Append(Pieces[id]);
        return sb.Replace('▁', ' ').ToString().Trim();
    }
}
