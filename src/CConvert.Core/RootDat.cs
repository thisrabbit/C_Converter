using System.Buffers.Binary;
using System.Text;

namespace CConvert.Core;

/// <summary>
/// content/root.dat: RIFF "CDR?" that each chunk leaf is con pointer 16 byte
/// (u32 fileIndex | 0xFFFFFFFF = inline, u32 length, u32 offset or ≤8 byte inline, u32 0).
/// </summary>
public sealed class RootDat
{
    /// <param name="Path">Path lead LIST, vd "/page/gobj/layr/obj/lgob/loda".</param>
    /// <param name="Parent">Number item automatically of LIST cha (to group the chunk plus one object).</param>
    public sealed record Leaf(string FourCC, string Path, int Parent, int PointerOffset, uint FileIndex, uint Length, uint DataOffset)
    {
        public bool Inline => FileIndex == 0xFFFFFFFF;
    }

    public byte[] Bytes { get; }
    public List<Leaf> Leaves { get; } = new();
    public List<string> ListTypes { get; } = new();

    public RootDat(byte[] bytes)
    {
        Bytes = bytes;
        if (bytes.Length < 12 || Ascii(0) != "RIFF" || Ascii(8, 3) != "CDR")
            throw new InvalidDataException("content/root.dat not RIFF 'CDR?'.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (8L + size != bytes.Length)
            throw new InvalidDataException($"Size size RIFF ({size}) not match uint32 root.dat ({bytes.Length}).");
        Walk(12, bytes.Length, "");
    }

    public char FormChar
    {
        get => (char)Bytes[11];
        set => Bytes[11] = (byte)value;
    }

    public Leaf? Find(string fourcc) => Leaves.FirstOrDefault(l => l.FourCC == fourcc);

    /// <summary>Read content chunk leaf; dataFiles according to item automatically in dataFileList.</summary>
    public byte[] Read(Leaf leaf, IReadOnlyList<byte[]> dataFiles)
    {
        if (leaf.Inline)
            return Bytes.AsSpan(leaf.PointerOffset + 8, (int)leaf.Length).ToArray();
        byte[] file = dataFiles[(int)leaf.FileIndex];
        return file.AsSpan((int)leaf.DataOffset, (int)leaf.Length).ToArray();
    }

    /// <summary>
    /// Create root.dat new no longer the chunk is choose: <paramref name="removeList"/> accepts only number in
    /// <see cref="Lists"/>, <paramref name="removeLeaf"/> accepts only number in <see cref="Leaves"/>.
    /// Recalculate LIST and RIFF sizes. Data in .dat files is not deleted; it is only no longer referenced.
    /// Call before every <see cref="Replace"/> because only the changed chunk.
    /// </summary>
    public RootDat Without(Func<int, bool> removeList, Func<int, bool> removeLeaf)
    {
        if (_replacements.Count > 0)
            throw new InvalidOperationException("Delete the chunk before replacing its content.");
        int listIndex = 0, leafIndex = 0;
        using var ms = new MemoryStream(Bytes.Length);
        ms.Write(Bytes, 0, 12);
        Copy(12, Bytes.Length);
        byte[] result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return new RootDat(result);

        // iterate according to correct behavior automatically Walk() to only number LIST/leaf match with Lists/Leaves.
        void Copy(int pos, int end)
        {
            while (pos + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(pos + 4));
                int ds = pos + 8, next = ds + (int)size + (int)(size & 1);
                if (Ascii(pos) == "LIST")
                {
                    int me = listIndex++;
                    if (removeList(me))
                    {
                        SkipLists(ds + 4, ds + (int)size);
                    }
                    else
                    {
                        long header = ms.Position;
                        ms.Write(Bytes, pos, 12);
                        Copy(ds + 4, ds + (int)size);
                        long after = ms.Position;
                        uint newSize = (uint)(after - header - 8);
                        ms.Position = header + 4;
                        Span<byte> b = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(b, newSize);
                        ms.Write(b);
                        ms.Position = after;
                        if ((newSize & 1) == 1) ms.WriteByte(0);
                    }
                }
                else if (!removeLeaf(leafIndex++))
                {
                    ms.Write(Bytes, pos, next - pos);
                }
                pos = next;
            }
        }

        // Skip all child nodes, but still count LIST/leaf nodes to preserve correct offsets.
        void SkipLists(int pos, int end)
        {
            while (pos + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(pos + 4));
                int ds = pos + 8;
                if (Ascii(pos) == "LIST") { listIndex++; SkipLists(ds + 4, ds + (int)size); }
                else leafIndex++;
                pos = ds + (int)size + (int)(size & 1);
            }
        }
    }

    private readonly Dictionary<int, byte[]> _replacements = new();

    /// <summary>Set content new for chunk leaf (is apply use when call <see cref="Rebuild"/>).</summary>
    public void Replace(Leaf leaf, byte[] content)
    {
        int i = Leaves.IndexOf(leaf);
        if (i < 0) throw new ArgumentException("Chunk does not belong to this root.dat.", nameof(leaf));
        if (leaf.Inline && content.Length > 8)
            throw new InvalidOperationException($"Chunk inline {leaf.FourCC} cannot long more 8 byte.");
        _replacements[i] = content;
    }

    public bool HasReplacements => _replacements.Count > 0;

    /// <summary>
    /// Rebuild file data from the updated content and update every child pointer in root.dat.
    /// Bytes not belonging to any chunk (usually padding) remain unchanged, as do the corresponding positions.
    /// From reject if second con pointer overlap up nhau (not known way translate an entire).
    /// </summary>
    public List<byte[]> Rebuild(IReadOnlyList<byte[]> dataFiles)
    {
        var result = new List<byte[]>(dataFiles.Count);
        for (int f = 0; f < dataFiles.Count; f++)
        {
            byte[] src = dataFiles[f];
            var refs = Leaves.Select((l, i) => (Leaf: l, Index: i))
                .Where(x => !x.Leaf.Inline && x.Leaf.FileIndex == f)
                .OrderBy(x => x.Leaf.DataOffset)
                .ToList();
            if (!refs.Any(x => _replacements.ContainsKey(x.Index)))
            {
                result.Add(src);
                continue;
            }

            using var ms = new MemoryStream(src.Length + 1024);
            long cursor = 0;
            foreach (var (leaf, index) in refs)
            {
                if (leaf.DataOffset < cursor)
                    throw new InvalidDataException($"Hai chunk overlap up nhau in file data #{f} at 0x{leaf.DataOffset:X}.");
                ms.Write(src, (int)cursor, (int)(leaf.DataOffset - cursor));
                byte[] content = _replacements.TryGetValue(index, out var r) ? r : src.AsSpan((int)leaf.DataOffset, (int)leaf.Length).ToArray();
                var p = Bytes.AsSpan(leaf.PointerOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(p[4..], (uint)content.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(p[8..], (uint)ms.Position);
                ms.Write(content);
                cursor = leaf.DataOffset + leaf.Length;
            }
            ms.Write(src, (int)cursor, (int)(src.Length - cursor));
            result.Add(ms.ToArray());
        }

        foreach (var (index, content) in _replacements)
        {
            var leaf = Leaves[index];
            if (!leaf.Inline) continue;
            var p = Bytes.AsSpan(leaf.PointerOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(p[4..], (uint)content.Length);
            p.Slice(8, 8).Clear();
            content.CopyTo(p[8..]);
        }
        _replacements.Clear();
        return result;
    }

    /// <summary>Record overwrite data inline (not change uint32).</summary>
    public void WriteInline(Leaf leaf, ReadOnlySpan<byte> data)
    {
        if (!leaf.Inline || data.Length != leaf.Length)
            throw new InvalidOperationException($"Chunk {leaf.FourCC} not inline {data.Length} byte.");
        data.CopyTo(Bytes.AsSpan(leaf.PointerOffset + 8));
    }

    /// <summary>The LIST according to item automatically encounter: type and only number LIST cha (-1 = source).</summary>
    public List<(string Type, int Parent)> Lists { get; } = new();

    private void Walk(int pos, int end, string prefix, int parent = -1)
    {
        while (pos + 8 <= end)
        {
            string cc = Ascii(pos);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(pos + 4));
            int ds = pos + 8;
            if (ds + size > end)
                throw new InvalidDataException($"Chunk '{cc}' at 0x{pos:X} exceed edge.");
            if (cc == "LIST")
            {
                string type = Ascii(ds).TrimEnd();
                ListTypes.Add(type);
                Lists.Add((type, parent));
                Walk(ds + 4, ds + (int)size, prefix + "/" + type, Lists.Count - 1);
            }
            else
            {
                if (size != 16)
                    throw new InvalidDataException($"Chunk leaf '{cc}' at 0x{pos:X} long {size}, not con pointer 16 byte.");
                var s = Bytes.AsSpan(ds);
                Leaves.Add(new Leaf(cc, prefix + "/" + cc, parent, ds,
                    BinaryPrimitives.ReadUInt32LittleEndian(s),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[8..])));
            }
            pos = ds + (int)size + (int)(size & 1);
        }
    }

    private string Ascii(int pos, int len = 4) => Encoding.Latin1.GetString(Bytes, pos, len);
}
