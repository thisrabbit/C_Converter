using System.IO.Compression;
using System.Text;
using CdrDump;

// cdrdump — utility for inspecting .cdr structure (Phase 0). Read-only; never writes to source files.
//
//   cdrdump <file.cdr> [file2.cdr ...] [--hex N] [--stats] [--out <output folder>]
//
//   --hex N    number of bytes to preview for each leaf chunk (default 16, 0 to disable)
//   --stats    add statistics grouped by the leading chunk path (for easy comparison between versions)
//   --out DIR  write results to DIR/<filename>.tree.txt instead of the console

Console.OutputEncoding = Encoding.UTF8;

var files = new List<string>();
int hex = 16;
bool stats = false;
string? outDir = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--hex": hex = int.Parse(args[++i]); break;
        case "--stats": stats = true; break;
        case "--out": outDir = args[++i]; break;
        default: files.Add(args[i]); break;
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("Usage: cdrdump <file.cdr> [...] [--hex N] [--stats] [--out <output folder>]");
    return 2;
}

int failures = 0;
foreach (string file in files)
{
    var sb = new StringBuilder();
    try
    {
        Dump(file, sb, hex, stats);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        sb.AppendLine($"ERROR: {ex.Message}");
        failures++;
    }

    if (outDir is null)
    {
        Console.Write(sb);
    }
    else
    {
        Directory.CreateDirectory(outDir);
        string target = Path.Combine(outDir, Path.GetFileName(file) + ".tree.txt");
        File.WriteAllText(target, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"{file} → {target}");
    }
}
return failures == 0 ? 0 : 1;

static void Dump(string file, StringBuilder sb, int hex, bool stats)
{
    byte[] buf = File.ReadAllBytes(file);
    sb.AppendLine($"=== {file} ({buf.Length:N0} byte)");

    if (buf.Length >= 4 && buf[0] == 'P' && buf[1] == 'K' && buf[2] == 3 && buf[3] == 4)
    {
        sb.AppendLine("Container: ZIP (newer-version format, approximately X4 and later — ASSUMED, requires verification)");
        DumpZip(buf, sb, hex, stats);
    }
    else if (buf.Length >= 4 && RiffParser.Ascii(buf, 0, 4) == "RIFF")
    {
        sb.AppendLine("Container: RIFF bare (older-version group)");
        DumpRiff(buf, "file", sb, new RiffParser(hex), stats);
    }
    else
    {
        sb.AppendLine($"Container: UNRECOGNIZED. 16 byte first: {Hex(buf.AsSpan(0, Math.Min(16, buf.Length)))}  |{RiffParser.Escape(buf, 0, 16)}|");
        sb.AppendLine("(Very old CorelDRAW file or not .cdr — send this sample for separate analysis.)");
    }
    sb.AppendLine();
}

static void DumpZip(byte[] buf, StringBuilder sb, int hex, bool stats)
{
    using var zip = new ZipArchive(new MemoryStream(buf, writable: false), ZipArchiveMode.Read);
    sb.AppendLine($"{zip.Entries.Count} entries in ZIP:");
    var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in zip.Entries)
    {
        sb.AppendLine($"  {e.FullName,-48} {e.Length,12:N0} byte (compressed {e.CompressedLength:N0})");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        entries[e.FullName] = ms.ToArray();
    }
    sb.AppendLine();

    // content/dataFileList.dat: newline-separated file names located under content/data/.
    List<(string, byte[])>? dataFiles = null;
    if (entries.TryGetValue("content/dataFileList.dat", out byte[]? list))
    {
        dataFiles = new();
        foreach (string name in Encoding.UTF8.GetString(list).Split('\n',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entries.TryGetValue("content/data/" + name, out byte[]? d))
                dataFiles.Add((name, d));
            else
            {
                sb.AppendLine($"WARNING: dataFileList references '{name}' but is missing content/data/{name}");
                dataFiles.Add((name, Array.Empty<byte>()));
            }
        }
        sb.AppendLine($"dataFileList: {string.Join(", ", dataFiles.Select((f, i) => $"#{i}={f.Item1}"))}");
        sb.AppendLine();
    }
    var parser = new RiffParser(hex, dataFiles);

    foreach (var (name, data) in entries)
    {
        if (data.Length == 0) continue;
        var e = (FullName: name, Length: data.Length);

        if (data.Length >= 12 && RiffParser.Ascii(data, 0, 4) == "RIFF")
        {
            sb.AppendLine($"--- {e.FullName}: RIFF");
            DumpRiff(data, "zip:" + e.FullName, sb, parser, stats);
        }
        else if (e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            string text = Encoding.UTF8.GetString(data);
            sb.AppendLine($"--- {e.FullName}: XML, 300 character automatically first:");
            sb.AppendLine("    " + text[..Math.Min(300, text.Length)].ReplaceLineEndings(" "));
        }
        else
        {
            sb.AppendLine($"--- {e.FullName}: {Hex(data.AsSpan(0, Math.Min(16, data.Length)))}  |{RiffParser.Escape(data, 0, 16)}|");
        }
    }
}

static void DumpRiff(byte[] buf, string source, StringBuilder sb, RiffParser parser, bool stats)
{
    Chunk root = parser.ParseRoot(buf, source);
    PrintTree(root, sb, 0);
    if (stats)
    {
        var agg = new SortedDictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        Aggregate(root, "", agg);
        sb.AppendLine();
        sb.AppendLine("Statistics by path (count | total bytes | path lead):");
        foreach (var (path, (count, bytes)) in agg)
            sb.AppendLine($"  {count,6} | {bytes,12:N0} | {path}");
    }
}

static void PrintTree(Chunk c, StringBuilder sb, int depth)
{
    string indent = new(' ', depth * 2);
    string where = c.Source == "file" || c.Source.StartsWith("zip:", StringComparison.Ordinal) ? "" : $" [{c.Source}]";
    sb.Append($"{indent}[0x{c.Offset:X8}]{where} {c.Label} size={c.Size}");
    if (c.ListType is null && c.Content.Length != c.Size)
        sb.Append($" (content {c.Content.Length})");
    if (c.Preview.Length > 0)
        sb.Append($"  {Hex(c.Preview)} |{RiffParser.Escape(c.Preview, 0, c.Preview.Length)}|");
    sb.AppendLine();
    foreach (string n in c.Notes)
        sb.AppendLine($"{indent}    # {n}");
    foreach (var child in c.Children)
        PrintTree(child, sb, depth + 1);
}

static void Aggregate(Chunk c, string prefix, SortedDictionary<string, (int, long)> agg)
{
    string path = prefix.Length == 0 ? c.Label : prefix + "/" + c.Label;
    agg[path] = agg.TryGetValue(path, out var v) ? (v.Item1 + 1, v.Item2 + c.Size) : (1, c.Size);
    foreach (var child in c.Children)
        Aggregate(child, path, agg);
}

static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
