using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CConvert.Core;

namespace CConvert.Tests;

/// <summary>
/// Experimental mode for source newer than 2024 (2025/2026 has no real samples).
/// File "2025" is simulated from sample 2024: change vrsn/fver/form type and insert related unusual.
/// </summary>
public class ExperimentalTests
{
    /// <summary>
    /// Schema 2019 must enough: experimental logic runs on sample 2024 produces identical results logic already verified
    /// (if schema diagram missing one key valid ratio of 2019, key that will be skip wrong and test this broken).
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleFiles.Version25), MemberType = typeof(SampleFiles))]
    public void Experimental_logic_equals_verified_logic_on_2024_samples(string file)
    {
        string input = Path.Combine(SampleFiles.Root, file);
        string a = TempPath(), b = TempPath();
        try
        {
            var verified = Downgrader.Run(input, a, CorelVersion.V2019);
            var experimental = Downgrader.Run(input, b, CorelVersion.V2019, new DowngradeOptions { ForceExperimental = true });
            Assert.True(verified.Success, string.Join("\n", verified.Log));
            Assert.True(experimental.Success, string.Join("\n", experimental.Log));
            Assert.True(experimental.Experimental);
            AssertSameDrawing(a, b);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void Simulated_2025_file_converts_and_strips_unknown_keys()
    {
        string original = Path.Combine(SampleFiles.Root, "T40_mixed_25.cdr");
        if (!File.Exists(original)) return;
        string fake = Simulate2025(original, cdssExtraKey: true);
        string outFake = TempPath(), outOrig = TempPath();
        try
        {
            var r = Downgrader.Run(fake, outFake, CorelVersion.V2019);
            Assert.True(r.Success, string.Join("\n", r.Log));
            Assert.True(r.Experimental);
            Assert.Contains(r.Log, l => l.Level == LogLevel.Warning && l.Message.Contains("newIn2025"));
            Assert.Contains("2025?", r.Log[0].Message);

            Assert.True(Downgrader.Run(original, outOrig, CorelVersion.V2019).Success);
            AssertSameDrawing(outOrig, outFake);
            Assert.DoesNotContain("newIn2025", ReadEntry(outFake, "styles/document.cdss"));
        }
        finally
        {
            File.Delete(fake);
            File.Delete(outFake);
            File.Delete(outOrig);
        }
    }

    [Fact]
    public void Simulated_2025_with_unknown_text_format_is_refused()
    {
        string original = Path.Combine(SampleFiles.Root, "T14_text_25.cdr");
        if (!File.Exists(original)) return;
        string fake = Simulate2025(original, txsmVersion: 15);
        string output = TempPath();
        try
        {
            var r = Downgrader.Run(fake, output, CorelVersion.V2019);
            Assert.False(r.Success);
            Assert.False(File.Exists(output));
            Assert.Contains(r.Log, l => l.Level == LogLevel.Error && l.Message.Contains("Text form 15"));
        }
        finally
        {
            File.Delete(fake);
            File.Delete(output);
        }
    }

    [Fact]
    public void Simulated_2025_with_unknown_object_property_is_refused()
    {
        string original = Path.Combine(SampleFiles.Root, "T01_rect_25.cdr");
        if (!File.Exists(original)) return;
        string fake = Simulate2025(original, extraLodaArg: 0x7777);
        string output = TempPath();
        try
        {
            var r = Downgrader.Run(fake, output, CorelVersion.V2019);
            Assert.False(r.Success);
            Assert.False(File.Exists(output));
            Assert.Contains(r.Log, l => l.Level == LogLevel.Error && l.Message.Contains("0x7777"));
        }
        finally
        {
            File.Delete(fake);
            File.Delete(output);
        }
    }

    [Fact]
    public void Probe_acceptss_newer_versions_as_experimental()
    {
        Assert.True(CorelVersion.Recognize(2600)!.IsExperimental);
        Assert.Equal("2025?", CorelVersion.Recognize(2600)!.Name);
        Assert.Equal("2026?", CorelVersion.Recognize(2700)!.Name);
        Assert.False(CorelVersion.Recognize(2500)!.IsExperimental);
        Assert.Null(CorelVersion.Recognize(1600));
    }

    // ---------- utility benefit ----------

    /// <summary>Create a copy "CorelDRAW 2025" (vrsn 2600) of one sample 2024, optionally with unusual changes.</summary>
    private static string Simulate2025(string source, bool cdssExtraKey = false, uint? txsmVersion = null, uint? extraLodaArg = null)
    {
        var pkg = CdrPackage.Load(source);
        var rootEntry = pkg.Get("content/root.dat");
        var root = new RootDat(rootEntry.Data);
        var dataEntries = Encoding.UTF8.GetString(pkg.Get("content/dataFileList.dat").Data)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => pkg.Get("content/data/" + n)).ToList();
        var data = dataEntries.Select(e => e.Data).ToList();

        root.FormChar = 'S';
        byte[] fver = [0x28, 0x0A, 1, 0, 0, 0, 0x1A, 0]; // 2600
        root.WriteInline(root.Find("fver")!, fver);
        root.WriteInline(root.Find("vrsn")!, [0x28, 0x0A]);

        if (txsmVersion is { } tv)
        {
            var leaf = root.Leaves.First(l => l.FourCC == "txsm");
            byte[] t = root.Read(leaf, data);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(0x14), tv);
            root.Replace(leaf, t);
        }
        if (extraLodaArg is { } arg)
        {
            var leaf = root.Leaves.First(l => l.FourCC == "loda" && LodaRecord.TryParse(root.Read(l, data), out var rr) && rr!.ObjType == 1);
            LodaRecord.TryParse(root.Read(leaf, data), out var rec);
            rec!.Args[arg] = [1, 0, 0, 0];
            root.Replace(leaf, rec.ToBytes());
        }
        if (root.HasReplacements)
        {
            var rebuilt = root.Rebuild(data);
            for (int i = 0; i < dataEntries.Count; i++) dataEntries[i].Data = rebuilt[i];
        }
        rootEntry.Data = root.Bytes;

        var meta = pkg.Get("META-INF/metadata.xml");
        meta.Data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(meta.Data)
            .Replace("<cdr:CoreVersion>2500</cdr:CoreVersion>", "<cdr:CoreVersion>2600</cdr:CoreVersion>"));
        if (cdssExtraKey)
        {
            var cdss = pkg.Get("styles/document.cdss");
            cdss.Data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(cdss.Data)
                .Replace("\"afterParaSpacing\":", "\"newIn2025\": \"1\",\n        \"afterParaSpacing\":"));
        }

        string path = TempPath();
        pkg.Save(path);
        return path;
    }

    private static void AssertSameDrawing(string a, string b)
    {
        using var za = ZipFile.OpenRead(a);
        using var zb = ZipFile.OpenRead(b);
        foreach (var e in za.Entries.Where(e => e.FullName.StartsWith("content/") || e.FullName == "styles/document.cdss"))
            Assert.True(Read(e).SequenceEqual(Read(zb.GetEntry(e.FullName)!)), $"{e.FullName} differ");
    }

    private static string ReadEntry(string zip, string name)
    {
        using var z = ZipFile.OpenRead(zip);
        return Encoding.UTF8.GetString(Read(z.GetEntry(name)!));
    }

    private static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"cconvert_exp_{Guid.NewGuid():N}.cdr");
}
