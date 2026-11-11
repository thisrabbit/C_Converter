using System.Buffers.Binary;

namespace CConvert.Core;

/// <summary>
/// Data model for the 'loda' object (verified by byte-for-byte read/write round-trips across all samples):
/// <code>
///   u32 length, u32 n, u32 offOffsets (=0x14), u32 offTypes, u32 objType,
///   u32 offsets[n+1]   (offset last = length),
///   u32 types[n]       (in ascending order; offsets[i] corresponds to types[n-1-i] — data ordered by descending type),
///   data the object number.
/// </code>
/// </summary>
public sealed class LodaRecord
{
    public const uint StyleArg = 0xC9;

    public uint ObjType { get; set; }

    /// <summary>Object number according to type, always keep in ascending order according to type.</summary>
    public SortedDictionary<uint, byte[]> Args { get; } = new();

    public static bool TryParse(byte[] d, out LodaRecord? rec)
    {
        rec = null;
        if (d.Length < 20) return false;
        var s = d.AsSpan();
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(s);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        uint oo = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        uint ot = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        if (len != d.Length || n > 4096 || oo != 0x14 || ot != oo + 4 * (n + 1) || ot + 4 * n > len)
            return false;

        var r = new LodaRecord { ObjType = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]) };
        for (int i = 0; i < n; i++)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(s[(int)(ot + 4 * i)..]);
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(s[(int)(oo + 4 * (n - 1 - i))..]);
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(s[(int)(oo + 4 * (n - i))..]);
            if (start > end || end > len || !r.Args.TryAdd(type, d[(int)start..(int)end]))
                return false;
        }
        rec = r;
        return true;
    }

    public byte[] ToBytes()
    {
        int n = Args.Count;
        int header = 0x14 + 4 * (n + 1) + 4 * n;
        int total = header + Args.Values.Sum(a => a.Length);
        var buf = new byte[total];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x14);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)(0x14 + 4 * (n + 1)));
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], ObjType);

        var types = Args.Keys.ToArray();
        int pos = header;
        // Data data arrange according to type decrease gradually: offsets[i] ↔ types[n-1-i].
        for (int i = 0; i < n; i++)
        {
            byte[] data = Args[types[n - 1 - i]];
            BinaryPrimitives.WriteUInt32LittleEndian(s[(0x14 + 4 * i)..], (uint)pos);
            data.CopyTo(s[pos..]);
            pos += data.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[(0x14 + 4 * n)..], (uint)pos);
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(s[(0x14 + 4 * (n + 1) + 4 * i)..], types[i]);
        return buf;
    }

    /// <summary>Object number style: u32 1, uint32 length, JSON UTF-8.</summary>
    public static byte[] MakeStyle(string json)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(json);
        var b = new byte[8 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)text.Length);
        text.CopyTo(b, 8);
        return b;
    }

    public static bool TryReadStyle(byte[] arg, out string json)
    {
        json = "";
        if (arg.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(arg) != 1) return false;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(arg.AsSpan(4));
        if (8 + len != arg.Length) return false;
        json = System.Text.Encoding.UTF8.GetString(arg, 8, (int)len);
        return true;
    }
}
