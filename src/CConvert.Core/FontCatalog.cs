using System.Buffers.Binary;
using Microsoft.Win32;

namespace CConvert.Core;

/// <summary>Tra save font already install on Windows (only to warning, not start force).</summary>
public static class FontCatalog
{
    private static readonly Lazy<Dictionary<string, string>> Files = new(LoadRegistry);

    /// <summary>Whether a font is variable (OpenType has table 'fvar')? null if font was not found.</summary>
    public static bool? IsVariable(string family)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!Files.Value.TryGetValue(family, out string? path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) != 12) return null;
            ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
            if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
                return null; // set collect set TTC: skipped for simple
            var dir = new byte[16 * numTables];
            if (fs.Read(dir) != dir.Length) return null;
            for (int i = 0; i < numTables; i++)
                if (dir[16 * i] == 'f' && dir[16 * i + 1] == 'v' && dir[16 * i + 2] == 'a' && dir[16 * i + 3] == 'r')
                    return true;
            return false;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> LoadRegistry()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return map;
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string file) continue;
                string path = Path.IsPathRooted(file) ? file : Path.Combine(fontsDir, file);
                // "Arial Bold (TrueType)" → "Arial Bold"; "Bahnschrift (TrueType)" → "Bahnschrift"
                string family = name.Split(" (")[0].Trim();
                map.TryAdd(family, path);
            }
        }
        return map;
    }
}
