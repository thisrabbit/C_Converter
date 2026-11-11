using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CConvert.Core;

/// <summary>
/// Convert chunk text 'txsm' from form 14 (CorelDRAW 2020+) about form 13 (2019).
/// Rules derived by comparing with files CorelDRAW saved using Save As (docs/PhatHien-02-loda.md):
///  - u32 at 0x14: 14 → 13.
///  - The block JSON style (uint32 length + JSON has indent margin): skip key 2020+, update log uint32.
///    Block "paragraph" missing bulletFirstCharIndent is add value default 2019.
///  - End record (after the text string and byte 0): version 14 adds a trailing record list
///    u32 n, n × (u32 c, c × u32) — absent in version 13, so it is skipped.
/// </summary>
public static class TxsmConverter
{
    // Value value Corel 2019 write when style does not have (bullet default '•' Arial 12pt).
    private const string DefaultBulletFirstCharIndent = "48676";

    /// <param name="problem">Called when an unknown text form is encountered (experimental mode variable become error).</param>
    /// <param name="extraStrip">Filter JSON add sung (experimental mode: according to schema diagram 2019); return about number key removed.</param>
    public static byte[]? Convert(byte[] d, Action<string> warn, Action<string>? problem = null, Func<JsonNode, int>? extraStrip = null)
    {
        problem ??= warn;
        if (d.Length < 0x18) return null;
        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x14));
        if (ver == 13) return null;
        if (ver != 14)
        {
            problem($"Text form {ver} not previously encountered in sample — copies it unchanged.");
            return null;
        }

        int tail = FindListTail(d);
        if (tail < 0)
        {
            problem("Not exact defined is structure last of block text — copies it unchanged (CorelDRAW 2019 still opens successfully form this in experimental).");
            return null;
        }

        using var ms = new MemoryStream(d.Length);
        int pos = 0;
        foreach (var (start, length) in FindJsonBlocks(d, tail))
        {
            ms.Write(d, pos, start - 4 - pos);
            byte[] block = d.AsSpan(start, length).ToArray();
            byte[] converted = ConvertJson(block, warn, extraStrip) ?? block;
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)converted.Length);
            ms.Write(len);
            ms.Write(converted);
            pos = start + length;
        }
        ms.Write(d, pos, tail - pos);

        byte[] result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x14), 13);
        return result;
    }

    /// <summary>Start position of the final record list (immediately after "string text + 0x00"), or -1.</summary>
    private static int FindListTail(byte[] d)
    {
        // Test from last about: text string = uint32 length + bytes, followed by one zero byte; the remaining bytes form the record list.
        for (int p = d.Length - 9; p >= 0x18; p--)
        {
            uint l = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            long q = p + 4L + l;
            if (q + 5 > d.Length || d[q] != 0) continue;
            if (ListEndsExactly(d, (int)q + 1))
                return (int)q + 1;
        }
        return -1;
    }

    private static bool ListEndsExactly(byte[] d, int pos)
    {
        if (pos + 4 > d.Length) return false;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
        if (n > 10000) return false;
        pos += 4;
        for (uint i = 0; i < n; i++)
        {
            if (pos + 4 > d.Length) return false;
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
            if (c > 100000) return false;
            pos += 4 + (int)(4 * c);
        }
        return pos == d.Length;
    }

    /// <summary>The block JSON: uint32 length immediately before '{', JSON valid ratio long correct uint32 that.</summary>
    private static IEnumerable<(int Start, int Length)> FindJsonBlocks(byte[] d, int end)
    {
        int i = 4;
        while (i < end)
        {
            if (d[i] == (byte)'{')
            {
                uint l = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i - 4));
                if (l >= 2 && i + l <= end && d[i + l - 1] == (byte)'}' && IsJson(d.AsSpan(i, (int)l)))
                {
                    yield return (i, (int)l);
                    i += (int)l;
                    continue;
                }
            }
            i++;
        }
    }

    private static bool IsJson(ReadOnlySpan<byte> s)
    {
        try
        {
            var reader = new Utf8JsonReader(s);
            while (reader.Read()) { }
            return reader.BytesConsumed == s.Length;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static byte[]? ConvertJson(byte[] block, Action<string> warn, Func<JsonNode, int>? extraStrip)
    {
        var node = JsonNode.Parse(block);
        if (node is not JsonObject root) return null;
        int changes = CorelJson.StripPost2019Keys(root, warn);
        if (extraStrip is not null) changes += extraStrip(root);

        if (root["paragraph"] is JsonObject para)
        {
            // 2020+ measures the first-line indentation before calculating all bullets; 2019 measures after the bullet. With the default bullet
            // ('•' Arial 12pt, indent 6,35 mm) value 2019 already known main exact; field different requires measure font.
            bool known = false;
            if (Str(para, "bulletFirstCharIndent") == "63500" && Str(para, "bulletChar") == "22200000"
                && para["bulletFont"] is JsonObject bf && Str(bf, "font") == "Arial" && Str(bf, "size") == "42333")
            {
                para["bulletFirstCharIndent"] = DefaultBulletFirstCharIndent;
                known = true;
                changes++;
            }
            if (Str(para, "bulletOn") == "1" && !known)
                warn("Text contains a bullet list: CorelDRAW 2019 calculates text–bullet spacing differently from 2020+, text may be off by a few mm so relative to the source version.");
            if (para.Count > 0 && !para.ContainsKey("bulletFirstCharIndent"))
            {
                CorelJson.InsertSorted(para, "bulletFirstCharIndent", JsonValue.Create(DefaultBulletFirstCharIndent)!);
                changes++;
            }
        }
        if (changes == 0) return null;
        string newline = Encoding.UTF8.GetString(block).Contains("\r\n") ? "\r\n" : "\n";
        return CorelJson.Utf8(CorelJson.ToIndented(root, newline));
    }
}
