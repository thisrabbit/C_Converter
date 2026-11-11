using CConvert.Core;

namespace CConvert.Tests;

public class FormatTests
{
    [Theory]
    [MemberData(nameof(SampleFiles.Version25), MemberType = typeof(SampleFiles))]
    public void Loda_roundtrips_byte_for_byte(string file)
    {
        var (root, data) = SampleFiles.Load(file);
        foreach (var leaf in root.Leaves.Where(l => l.FourCC == "loda"))
        {
            byte[] raw = root.Read(leaf, data);
            Assert.True(LodaRecord.TryParse(raw, out var rec), $"{file}: could not be read {leaf.Path}");
            Assert.Equal(raw, rec!.ToBytes());
        }
    }

    [Theory]
    [MemberData(nameof(SampleFiles.Version25), MemberType = typeof(SampleFiles))]
    public void Rebuild_with_same_content_is_identity(string file)
    {
        var (root, data) = SampleFiles.Load(file);
        byte[] before = (byte[])root.Bytes.Clone();
        foreach (var leaf in root.Leaves.ToList())
            root.Replace(leaf, root.Read(leaf, data));
        var rebuilt = root.Rebuild(data);
        Assert.Equal(before, root.Bytes);
        for (int i = 0; i < data.Count; i++)
            Assert.Equal(data[i], rebuilt[i]);
    }

    [Fact]
    public void Loda_layout_matches_documented_example()
    {
        var rec = new LodaRecord { ObjType = 0 };
        rec.Args[0x4ABA] = [1, 2, 3, 4];
        rec.Args[LodaRecord.StyleArg] = LodaRecord.MakeStyle("{}");
        byte[] b = rec.ToBytes();
        // header 0x14 + 3 offset + 2 type = 0x28; data according to type decrease gradually: 0x4ABA before, then 0xC9.
        Assert.Equal(0x28u, BitConverter.ToUInt32(b, 0x14));
        Assert.Equal(0x2Cu, BitConverter.ToUInt32(b, 0x18));
        Assert.Equal(0xC9u, BitConverter.ToUInt32(b, 0x20));
        Assert.Equal(0x4ABAu, BitConverter.ToUInt32(b, 0x24));
        Assert.Equal([1, 2, 3, 4], b[0x28..0x2C]);
    }
}
