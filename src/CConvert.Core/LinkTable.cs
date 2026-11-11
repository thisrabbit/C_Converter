using System.Buffers.Binary;

namespace CConvert.Core;

/// <summary>
/// Table link result effect application 'lnkt' in doc (shadows, contour, extrude… join group effect application with object source):
/// <code>
///   u32 length, u32 n, u32 offOffsets (=0x0C), u32 offsets[n+1], the entry.
///   Entry (32 byte in sample): u32 type (0xE0 shadow, 0x80 contour…), u32 button source (spnd of lnkg),
///                            u32 button target (spnd of object), …
/// </code>
/// </summary>
public sealed class LinkTable
{
    public List<byte[]> Entries { get; } = new();

    public static bool TryParse(byte[] d, out LinkTable? table)
    {
        table = null;
        if (d.Length < 12) return false;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(d);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        uint oo = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
        if (len != d.Length || oo != 0x0C || oo + 4 * (n + 1) > len) return false;
        var t = new LinkTable();
        for (int i = 0; i < n; i++)
        {
            uint s = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)oo + 4 * i));
            uint e = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)oo + 4 * (i + 1)));
            if (s > e || e > len || e - s < 12) return false;
            t.Entries.Add(d[(int)s..(int)e]);
        }
        table = t;
        return true;
    }

    public static uint From(byte[] entry) => BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4));
    public static uint To(byte[] entry) => BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8));

    public byte[] ToBytes()
    {
        int n = Entries.Count;
        int header = 0x0C + 4 * (n + 1);
        var buf = new byte[header + Entries.Sum(e => e.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)buf.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), 0x0C);
        int pos = header;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x0C + 4 * i), (uint)pos);
            Entries[i].CopyTo(buf, pos);
            pos += Entries[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x0C + 4 * n), (uint)pos);
        return buf;
    }
}
