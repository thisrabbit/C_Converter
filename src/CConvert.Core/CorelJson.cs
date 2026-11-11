using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CConvert.Core;

/// <summary>Read/write JSON according to correct defined form CorelDRAW (to file export out same file Corel saved automatically).</summary>
public static class CorelJson
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToCompact(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>Indent 2 mark way, "key": value, '\n', object/array empty write "{" + newline + "}".</summary>
    public static string ToIndented(JsonNode node, string newline = "\n")
    {
        string json = node.ToJsonString(Indented).ReplaceLineEndings("\n");
        json = Regex.Replace(json, @"^( *)(.*?)(\{\}|\[\])", m =>
            $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value[0]}\n{m.Groups[1].Value}{m.Groups[3].Value[1]}",
            RegexOptions.Multiline);
        return newline == "\n" ? json : json.ReplaceLineEndings(newline);
    }

    /// <summary>
    /// Skip the key JSON only has from CorelDRAW 2020 become up (font_axes; bullet many level in "paragraph").
    /// Return about number key removed.
    /// </summary>
    public static int StripPost2019Keys(JsonNode? root, Action<string> warn)
    {
        int removed = 0;
        void Walk(JsonNode? node, string? name)
        {
            if (node is JsonObject obj)
            {
                if (obj["font_axes"] is JsonValue axes)
                {
                    if (axes.TryGetValue(out string? s) && s.Length > 0)
                        warn($"Variable font used (font_axes = \"{s}\") — CorelDRAW 2019 unsupported, will use default font style.");
                    obj.Remove("font_axes");
                    removed++;
                }
                if (obj["bulletChar"] is JsonValue bc && bc.TryGetValue(out string? hex) && MapBulletChar(hex) is { } mapped)
                {
                    obj["bulletChar"] = mapped;
                    removed++;
                }
                if (name == "paragraph")
                {
                    foreach (string k in ParagraphKeysAfter2019)
                        if (obj.Remove(k)) removed++;
                }
                foreach (var (k, v) in obj.ToList())
                    Walk(v, k);
            }
            else if (node is JsonArray arr)
            {
                foreach (var v in arr) Walk(v, null);
            }
        }
        Walk(root, null);
        return removed;
    }

    /// <summary>
    /// Experimental mode (source newer than 2024): skip keys absent from the CorelDRAW 2019 schema
    /// (<see cref="Schema2019"/>, derived from file 2019 real). Only filter in object has name already known; con of object
    /// dynamic (style names, level names…) and object unusual is unchanged. Return about number key removed; name key write into <paramref name="removedNames"/>.
    /// </summary>
    public static int StripUnknownKeys(JsonNode? root, ISet<string> removedNames)
    {
        int removed = 0;
        void Walk(JsonNode? node, string name)
        {
            if (node is JsonObject obj)
            {
                bool dynamic = Schema2019.Dynamic.Contains(name);
                if (!dynamic && Schema2019.Keys.TryGetValue(name, out var allowed))
                {
                    foreach (string k in obj.Select(p => p.Key).Where(k => !allowed.Contains(k)).ToList())
                    {
                        obj.Remove(k);
                        removedNames.Add(name == "<root>" ? k : $"{name}.{k}");
                        removed++;
                    }
                }
                foreach (var (k, v) in obj.ToList())
                    Walk(v, dynamic ? "<item>" : k);
            }
            else if (node is JsonArray arr)
            {
                foreach (var v in arr) Walk(v, name);
            }
        }
        Walk(root, "<root>");
        return removed;
    }

    // Windows-1252 0x80–0x9F → Unicode (0 = Do not use).
    private static readonly ushort[] Cp1252High =
    [
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
    ];

    /// <summary>
    /// bulletChar is u32 little-endian form hex. 2020+ keep code byte 0x80–0x9F of font; 2019 save Unicode
    /// (real sample: "9f000000" → "78010000" = 'Ÿ'). Returns null when no change is required.
    /// </summary>
    public static string? MapBulletChar(string hex)
    {
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _)) return null;
        byte[] b = System.Convert.FromHexString(hex);
        uint code = BitConverter.ToUInt32(b);
        if (code < 0x80 || code > 0x9F || Cp1252High[code - 0x80] == 0) return null;
        return System.Convert.ToHexString(BitConverter.GetBytes((uint)Cp1252High[code - 0x80])).ToLowerInvariant();
    }

    // Key in "paragraph" has from 2020 become up that 2019 does not have.
    public static readonly string[] ParagraphKeysAfter2019 =
        ["bulletFirstCharIndentIncludesBullet", "bulletFormat", "bulletInheritFont", "bulletNumberStyle", "levels", "numberInheritFont"];

    /// <summary>Insert a key into an object according to the containing text object (Corel writes this key in "paragraph" objects).</summary>
    public static void InsertSorted(JsonObject obj, string key, JsonNode value)
    {
        var items = obj.ToList();
        obj.Clear();
        bool done = false;
        foreach (var (k, v) in items)
        {
            if (!done && string.CompareOrdinal(k, key) > 0)
            {
                obj.Add(key, value);
                done = true;
            }
            obj.Add(k, v);
        }
        if (!done) obj.Add(key, value);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
