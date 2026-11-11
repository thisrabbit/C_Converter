using System.Buffers.Binary;
using System.IO.Compression;

namespace CConvert.Core;

/// <summary>Quickly read information from file .cdr (without conversion).</summary>
public static class CdrInfo
{
    /// <summary>Version version CorelDRAW of file, or reason description that could not be read.</summary>
    public static (CorelVersion? Version, string Description) Probe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            if (fs.Read(sig) < 4) return (null, "File empty");
            fs.Position = 0;
            if (sig[0] == 'R' && sig[1] == 'I' && sig[2] == 'F' && sig[3] == 'F')
                return (null, "CorelDRAW X3 or earlier (does not support)");
            if (sig[0] != 'P' || sig[1] != 'K')
                return (null, "Not must file CorelDRAW");

            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var rootEntry = zip.GetEntry("content/root.dat");
            if (rootEntry is null)
                return (null, "CorelDRAW X4–X6 (does not support)");
            byte[] root;
            using (var s = rootEntry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                root = ms.ToArray();
            }
            var rd = new RootDat(root);
            if (rd.Find("vrsn") is { Inline: true, Length: 2 } v)
            {
                ushort vrsn = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(v.PointerOffset + 8));
                var ver = CorelVersion.Recognize(vrsn);
                return (ver, ver?.ToString() ?? $"Version version unusual (vrsn {vrsn})");
            }
            return (null, "Could not find version number");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "Read error file: " + ex.Message);
        }
    }

    /// <summary>A path that does not yet exist: add " (1)", " (2)"… if needed. Never returns an existing file.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Output file name: &lt;name&gt;_&lt;version&gt;.cdr in the specified folder (default beside the source file).</summary>
    public static string OutputPathFor(string input, CorelVersion target, string? outDir)
    {
        string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(input))!;
        return UniquePath(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(input)}_{target.Name}.cdr"));
    }
}
