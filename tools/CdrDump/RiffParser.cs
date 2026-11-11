using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CdrDump;

/// <summary>A node in the chunk tree RIFF.</summary>
public sealed class Chunk
{
    public required string FourCC { get; init; }
    public string? ListType { get; init; }
    /// <summary>Chunk header offset, measured in the <see cref="Source"/>.</summary>
    public long Offset { get; init; }
    public uint Size { get; init; }
    /// <summary>"file", "zip:&lt;entry&gt;", or "cmpr@0x..." for data that is already decompressed.</summary>
    public required string Source { get; init; }
    public List<Chunk> Children { get; } = new();
    public List<string> Notes { get; } = new();
    public byte[] Preview { get; init; } = Array.Empty<byte>();
    /// <summary>Actual leaf-chunk content (after following its child pointer, if present).</summary>
    public byte[] Content { get; init; } = Array.Empty<byte>();

    public string Label => ListType is null ? FourCC : $"{FourCC} '{ListType}'";
}

/// <summary>
/// CorelDRAW RIFF tree reader. Understands structure only (chunk/list/cmpr),
/// Does not interpret object content. The details below are inferred from libcdr and marked "ASSUMED".
/// </summary>
public sealed class RiffParser
{
    private readonly int _previewBytes;
    private readonly IReadOnlyList<(string Name, byte[] Data)>? _dataFiles;

    /// <param name="dataFiles">
    /// Files are listed in content/dataFileList.dat (inside the ZIP with root.dat). Each chunk leaf has a
    /// 16-byte pointer in root.dat (verified on samples X7–2023):
    ///   u32 fileIndex (0-based according to dataFileList; 0xFFFFFFFF = data stored inline in the pointer),
    ///   u32 length, u32 offset (or an inline 8-byte payload), u32 (always 0 in the samples).
    /// </param>
    public RiffParser(int previewBytes, IReadOnlyList<(string Name, byte[] Data)>? dataFiles = null)
    {
        _previewBytes = previewBytes;
        _dataFiles = dataFiles;
    }

    /// <summary>Read actual content of chunk leaf (go according to con pointer if has).</summary>
    private (byte[] Data, string? Note) ResolveLeaf(byte[] buf, int dataStart, int dataEnd)
    {
        byte[] raw = buf.AsSpan(dataStart, dataEnd - dataStart).ToArray();
        if (_dataFiles is null || raw.Length != 16)
            return (raw, null);

        uint index = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        uint extra = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        string extraNote = extra == 0 ? "" : $" (u32 last = 0x{extra:X8}, different 0!)";

        if (index == 0xFFFFFFFF)
        {
            if (length > 8)
                return (raw, $"WARNING: con pointer inline but length={length} > 8");
            return (raw.AsSpan(8, (int)length).ToArray(), $"→ inline {length} byte");
        }
        if (index >= _dataFiles.Count)
            return (raw, $"WARNING: fileIndex {index} exceed dataFileList ({_dataFiles.Count} file)");

        var (name, data) = _dataFiles[(int)index];
        if ((long)offset + length > data.Length)
            return (raw, $"WARNING: {name}[0x{offset:X}+{length}] exceeds the file size ({data.Length})");
        return (data.AsSpan((int)offset, (int)length).ToArray(), $"→ {name} @0x{offset:X} len={length}{extraNote}");
    }

    public Chunk ParseRoot(byte[] buf, string source)
    {
        if (buf.Length < 12 || Ascii(buf, 0, 4) != "RIFF")
            throw new InvalidDataException("Not must RIFF (missing text character 'RIFF').");

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        string form = Ascii(buf, 8, 4);
        var root = new Chunk { FourCC = "RIFF", ListType = form, Offset = 0, Size = size, Source = source };

        if (!form.StartsWith("CDR", StringComparison.Ordinal))
            root.Notes.Add($"Form type '{form}' does not begin with 'CDR' — is not a CorelDRAW file.");
        else
            root.Notes.Add($"Version marker in form type: '{form[3]}' (the character-to-version mapping is ASSUMED; the 'vrsn' chunk takes priority).");

        long end = 8L + size;
        if (end > buf.Length)
        {
            root.Notes.Add($"WARNING: RIFF size field ({size}) exceeds the available uint32 data ({buf.Length - 8}).");
            end = buf.Length;
        }
        else if (end < buf.Length)
        {
            root.Notes.Add($"Has {buf.Length - end} byte remain after chunk RIFF source.");
        }

        ParseChildren(buf, 12, (int)end, root, source, blockSizes: null);
        return root;
    }

    private void ParseChildren(byte[] buf, int start, int end, Chunk parent, string source, uint[]? blockSizes)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            string fourcc = Ascii(buf, pos, 4);
            if (!IsPlausibleFourCC(fourcc))
            {
                parent.Notes.Add($"Stop at 0x{pos:X8}: '{Escape(buf, pos, 4)}' an unrecognized FourCC ({end - pos} bytes remain uninterpreted).");
                return;
            }

            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4));
            uint size = rawSize;
            var notes = new List<string>();

            // ASSUMED (based on how libcdr processes cmpr): the stream is already decompressed, and the "size"
            // field is only an index into the block-size table, not the actual size.
            if (blockSizes is not null)
            {
                if (rawSize < blockSizes.Length)
                {
                    size = blockSizes[rawSize];
                    notes.Add($"size is only number #{rawSize} in table block → {size}");
                }
                else
                {
                    notes.Add($"WARNING: block index {rawSize} exceeds the table ({blockSizes.Length} elements); using it as the actual size.");
                }
            }

            int dataStart = pos + 8;
            long dataEndL = (long)dataStart + size;
            bool overflow = dataEndL > end;
            int dataEnd = overflow ? end : (int)dataEndL;
            if (overflow)
                notes.Add($"WARNING: chunk khai report {size} byte but exceed edge chunk cha ({end - dataStart} byte ability use).");

            bool isList = fourcc is "LIST" or "RIFF";
            string? listType = isList && dataEnd - dataStart >= 4 ? Ascii(buf, dataStart, 4) : null;

            byte[] content = Array.Empty<byte>();
            if (!isList)
            {
                (content, string? resolveNote) = ResolveLeaf(buf, dataStart, dataEnd);
                if (resolveNote is not null) notes.Add(resolveNote);
            }

            var chunk = new Chunk
            {
                FourCC = fourcc,
                ListType = listType,
                Offset = pos,
                Size = size,
                Source = source,
                Content = content,
                Preview = content.AsSpan(0, Math.Min(_previewBytes, content.Length)).ToArray(),
            };
            chunk.Notes.AddRange(notes);
            parent.Children.Add(chunk);

            if (isList)
                ParseChildren(buf, dataStart + 4, dataEnd, chunk, source, blockSizes);
            else if (fourcc == "cmpr")
                ParseCmpr(buf, dataStart, dataEnd, chunk);
            else if (fourcc == "vrsn" && content.Length >= 2)
                chunk.Notes.Add(DescribeVersion(BinaryPrimitives.ReadUInt16LittleEndian(content)));

            if (overflow)
                return;

            pos = dataEnd;
            // RIFF padding: an odd chunk size is followed by one padding byte. It is not known whether every CDR file follows this.
            // should only jump via byte padding when check whether the next FourCC is valid after padding.
            if ((size & 1) == 1 && (pos + 1 == end
                || (pos + 1 + 4 <= end && !IsPlausibleFourCC(Ascii(buf, pos, 4)) && IsPlausibleFourCC(Ascii(buf, pos + 1, 4)))))
            {
                pos++;
            }
        }

        if (pos < end)
            parent.Notes.Add($"{end - pos} byte extra at last (0x{pos:X8}).");
    }

    /// <summary>
    /// Chunk 'cmpr' (CDR around v8–X3). The layout below is ASSUMED based on libcdr's reader and requires sample verification:
    ///   u32 compressedSize, u32 uncompressedSize, u32 blockSizesCompressedSize, u32 blockSizesUncompressedSize,
    ///   "CPng" u16 u16, [zlib data chunk], "CPng" u16 u16, [zlib block-size table (array u32)].
    /// </summary>
    private void ParseCmpr(byte[] buf, int start, int end, Chunk chunk)
    {
        if (end - start < 24)
        {
            chunk.Notes.Add("cmpr chunk is too short to contain a header.");
            return;
        }

        var s = buf.AsSpan(start);
        uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(s);
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        uint bsCompressed = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        uint bsUncompressed = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        chunk.Notes.Add($"cmpr header: compressed={compressed}, uncompressed={uncompressed}, blockSizesCompressed={bsCompressed}, blockSizesUncompressed={bsUncompressed}");

        int sig1 = IndexOf(buf, "CPng"u8, start, end);
        if (sig1 < 0)
        {
            chunk.Notes.Add("Could not find the 'CPng' signature — the cmpr layout differs from the assumption.");
            return;
        }
        if (sig1 != start + 16)
            chunk.Notes.Add($"'CPng' first first at +{sig1 - start}, not +16 as assume defined.");

        byte[]? data = TryInflate(buf, sig1 + 8, end, out string how1);
        if (data is null)
        {
            chunk.Notes.Add($"Data decompression failed: {how1}");
            return;
        }
        chunk.Notes.Add($"Decompressed data: {data.Length} byte ({how1}){(data.Length == uncompressed ? "" : $" — DIFFERENT uncompressed={uncompressed}")}");

        // Position position block item second: test according to compressedSize before, if not match then find 'CPng' next according to.
        int sig2 = sig1 + (int)Math.Min(compressed, (uint)(end - sig1));
        if (sig2 + 4 > end || Ascii(buf, sig2, 4) != "CPng")
            sig2 = IndexOf(buf, "CPng"u8, sig1 + 8, end);

        uint[]? blockSizes = null;
        if (sig2 >= 0)
        {
            byte[]? bs = TryInflate(buf, sig2 + 8, end, out string how2);
            if (bs is not null)
            {
                blockSizes = new uint[bs.Length / 4];
                for (int i = 0; i < blockSizes.Length; i++)
                    blockSizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(bs.AsSpan(i * 4));
                chunk.Notes.Add($"Block-size table: {blockSizes.Length} elements ({how2}).");
            }
            else
            {
                chunk.Notes.Add($"Failed to parse the compressed block-size table: {how2}");
            }
        }
        else
        {
            chunk.Notes.Add("Could not find the second 'CPng' block (the block-size table).");
        }

        string src = $"cmpr@0x{chunk.Offset:X8}";
        ParseChildren(data, 0, data.Length, chunk, src, blockSizes);
    }

    private static byte[]? TryInflate(byte[] buf, int start, int end, out string how)
    {
        if (start >= end)
        {
            how = "no data remains";
            return null;
        }
        foreach (bool zlib in new[] { true, false })
        {
            try
            {
                using var input = new MemoryStream(buf, start, end - start, writable: false);
                using Stream z = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                if (output.Length > 0)
                {
                    how = zlib ? "zlib" : "deflate raw";
                    return output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
            }
        }
        how = "not valid zlib/raw deflate";
        return null;
    }

    /// <summary>vrsn = version*100 + update version (vd 1700, 2430). 17→2024 already verified with sample.</summary>
    public static string DescribeVersion(ushort v)
    {
        int major = v / 100;
        string name = (major, v % 100) switch
        {
            (24, 30) => "CorelDRAW 2023",
            (24, _) => "CorelDRAW 2022",
            (25, _) => "CorelDRAW 2024",
            _ => major switch
        {
            >= 1 and <= 12 => $"CorelDRAW {major}",
            13 => "CorelDRAW X3",
            14 => "CorelDRAW X4",
            15 => "CorelDRAW X5",
            16 => "CorelDRAW X6",
            17 => "CorelDRAW X7",
            18 => "CorelDRAW X8",
            19 => "CorelDRAW 2017",
            20 => "CorelDRAW 2018",
            21 => "CorelDRAW 2019",
            22 => "CorelDRAW 2020",
            23 => "CorelDRAW 2021",
            _ => "unknown",
        },
        };
        return $"vrsn = {v} → {name}";
    }

    public static string Ascii(byte[] buf, int pos, int len) => Encoding.Latin1.GetString(buf, pos, len);

    public static string Escape(byte[] buf, int pos, int len)
    {
        var sb = new StringBuilder();
        for (int i = pos; i < Math.Min(buf.Length, pos + len); i++)
            sb.Append(buf[i] is >= 0x20 and < 0x7F ? (char)buf[i] : '.');
        return sb.ToString();
    }

    private static bool IsPlausibleFourCC(string s)
    {
        foreach (char c in s)
            if (c is < ' ' or > '~')
                return false;
        return s.Trim().Length > 0;
    }

    private static int IndexOf(byte[] buf, ReadOnlySpan<byte> needle, int start, int end)
    {
        if (start >= end) return -1;
        int i = buf.AsSpan(start, end - start).IndexOf(needle);
        return i < 0 ? -1 : start + i;
    }
}
