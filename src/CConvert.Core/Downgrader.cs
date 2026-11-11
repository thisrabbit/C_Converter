using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CConvert.Core;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(LogLevel Level, string Message)
{
    public override string ToString() => $"[{Level switch { LogLevel.Info => "INFO", LogLevel.Warning => "WARNING", _ => "ERROR" }}] {Message}";
}

public sealed class DowngradeResult
{
    public List<LogEntry> Log { get; } = new();
    public bool Success => Log.All(l => l.Level != LogLevel.Error);
    public bool HasWarnings => Log.Any(l => l.Level == LogLevel.Warning);

    /// <summary>Source newer than 2024, use experimental mode (inference; not verified against samples).</summary>
    public bool Experimental { get; internal set; }

    /// <summary>Strict mode: condition not previously encountered in sample is error (not write file) instead of a warning.</summary>
    internal bool Strict { get; set; }

    /// <summary>Key JSON unusual removed in experimental mode.</summary>
    internal SortedSet<string> UnknownKeys { get; } = new(StringComparer.Ordinal);

    internal void Info(string m) => Log.Add(new(LogLevel.Info, m));
    internal void Warn(string m) => Log.Add(new(LogLevel.Warning, m));
    internal void Error(string m) => Log.Add(new(LogLevel.Error, m));

    /// <summary>Condition not present in the comparison samples: error at mode strict, warning at mode often.</summary>
    internal void Problem(string m)
    {
        if (Strict) Error(m + " (experimental mode does not write a file when it encounters an unknown condition)");
        else Warn(m);
    }
}

public sealed record DowngradeOptions
{
    /// <summary>Only use for check test: process reason all source already verified with experimental logic.</summary>
    public bool ForceExperimental { get; init; }
}

/// <summary>
/// Downgrade ZIP-format .cdr files (root.dat) from 2020–2024 to 2019.
///
/// This NOT must fix version number: each become part differ between the version is convert according to
/// rules derived by comparing files saved by CorelDRAW itself "Save As" (docs/PhatHien-01-T00-T01.md).
/// Scope vi verify currently very narrow (page empty + rectangle), should every chunk/type object
/// unknown records produce warnings instead of being copied silently.
/// </summary>
public static class Downgrader
{
    // Chunk leaf, LIST and type record loda already encounter in sample and already exact accepts identical between 2019 and 2020–2024.
    private static readonly HashSet<string> VerifiedLeaves =
    [
        "fver", "vrsn", "mcfg", "pref", "ptrt", "fild", "outl", "stlt", "flgs", "spid", "bbox", "obbx", "loda", "usdn",
        "ftil", "trfd", "ttil", "arrx", "font", "txsm", "txtj", "spnd", "bmp ", "digk", "lnkt", "envd", "sblk", "symi", "syms",
        "udfe", "udta", "udid", // user data (Object Data) — verify with real file
    ];
    private static readonly HashSet<string> VerifiedLists =
    [
        "doc", "filt", "filc", "otlt", "page", "gobj", "layr", "lgob", "obj", "trfl", "grp", "clpt", "lnkg", "arrt", "fntt",
        "bmpt", "symb", "syse", "syst", "sytr", "udaf", "udat", "udte",
    ];
    // 0 group/page/layer, 1 rectangle, 2 ellipse, 3 path/curve, 4 artistic text, 5 bitmap, 6 paragraph text,
    // 8 extrude, 11/12/17 layer record, 15 contour, 20 polygon/star, 23 shadows.
    private static readonly HashSet<uint> VerifiedLodaTypes = [0, 1, 2, 3, 4, 5, 6, 8, 11, 12, 15, 17, 20, 23];


    public static DowngradeResult Run(string inputPath, string outputPath, CorelVersion target, DowngradeOptions? options = null)
    {
        var r = new DowngradeResult();
        options ??= new DowngradeOptions();
        try
        {
            if (target != CorelVersion.V2019)
            {
                r.Error($"Not yet support target version {target}. Currently only support {CorelVersion.V2019}.");
                return r;
            }

            var pkg = CdrPackage.Load(inputPath);
            var mime = pkg.Find("mimetype");
            if (mime is null || Encoding.ASCII.GetString(mime.Data) != CdrPackage.MimeType)
            {
                r.Error("Not must file CorelDRAW ZIP format (mimetype not match). Version X6 or earlier is not supported assist.");
                return r;
            }

            var rootEntry = pkg.Get("content/root.dat");
            var root = new RootDat(rootEntry.Data);
            var dataEntries = Encoding.UTF8.GetString(pkg.Get("content/dataFileList.dat").Data)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => pkg.Get("content/data/" + n))
                .ToList();
            var dataFiles = dataEntries.Select(e => e.Data).ToList();

            ushort vrsn = BinaryPrimitives.ReadUInt16LittleEndian(
                root.Read(root.Find("vrsn") ?? throw new InvalidDataException("Missing chunk vrsn."), dataFiles));
            var source = CorelVersion.Recognize(vrsn);
            if (source is null)
            {
                r.Error($"Version source version not accepts out (vrsn={vrsn}).");
                return r;
            }
            r.Info($"Source: {source}; target: {target}.");
            if (source.IsExperimental || options.ForceExperimental)
            {
                r.Experimental = r.Strict = true;
                r.Warn($"Source {source} does not have sample for verification — MODE LEVEL EXPERIMENTAL TEST: use shared rules of 2020–2024, "
                     + "filter JSON using the 2019 schema, and stop without writing if an unseen record is encountered. "
                     + "Open and check with CorelDRAW 2019 before when use.");
                if (!CheckExperimentalStructure(pkg, root, dataFiles, vrsn, r))
                    return r;
            }
            if (source.Vrsn <= target.Vrsn)
            {
                r.Error($"File already at {source}, does not require downgrade down {target}.");
                return r;
            }
            if (source.Vrsn < CorelVersion.V2020.Vrsn)
            {
                r.Error($"Not yet support source {source}.");
                return r;
            }

            Audit(root, dataFiles, r);
            root = RemoveInnerShadows(root, dataFiles, r);
            ConvertObjects(root, dataFiles, r);
            PatchRoot(root, dataFiles, target, r);
            if (root.HasReplacements)
            {
                var rebuilt = root.Rebuild(dataFiles);
                for (int i = 0; i < dataEntries.Count; i++)
                    dataEntries[i].Data = rebuilt[i];
            }
            rootEntry.Data = root.Bytes;
            PatchMetadata(pkg.Get("META-INF/metadata.xml"), target, r);
            PatchStyles(pkg.Get("styles/document.cdss"), target, r);
            if (r.UnknownKeys.Count > 0)
                r.Warn($"Experimental mode: removed {r.UnknownKeys.Count} type key JSON does not have in CorelDRAW 2019: "
                     + string.Join(", ", r.UnknownKeys.Take(20)) + (r.UnknownKeys.Count > 20 ? ", …" : "")
                     + ". Belongs calculate similar application (if has use) will lose in version 2019.");

            if (!r.Success)
                return r;
            pkg.Save(outputPath);
            r.Info($"Wrote {outputPath}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            r.Error(ex.Message);
        }
        catch (Exception ex)
        {
            // Unexpected data (for example, an out-of-range index) — report an error for this file without stopping the batch.
            r.Error($"Unexpected error ({ex.GetType().Name}): {ex.Message}");
        }
        return r;
    }

    /// <summary>
    /// Source newer than 2024: only next continue when file has correct structure already see at 2019–2024
    /// (ZIP + root.dat con pointer + dataFileList + document.cdss + metadata.xml, fver/vrsn inline, mcfg form 2100).
    /// Different structure meaning is Corel already change defined form — inference no longer value.
    /// </summary>
    private static bool CheckExperimentalStructure(CdrPackage pkg, RootDat root, List<byte[]> dataFiles, ushort vrsn, DowngradeResult r)
    {
        var missing = new[] { "styles/document.cdss", "META-INF/metadata.xml", "META-INF/container.xml" }
            .Where(n => pkg.Find(n) is null).ToList();
        if (missing.Count > 0)
            r.Error("Structure structure file different 2019–2024 (missing " + string.Join(", ", missing) + ") — cannot inference.");

        if (pkg.Find("META-INF/container.xml") is { } container
            && !Encoding.UTF8.GetString(container.Data).Contains("full-path=\"content/root.dat\""))
            r.Error("container.xml not pointer to content/root.dat as 2019–2024 — defined form already change.");

        if (root.Find("fver") is not { Inline: true, Length: 8 } fver
            || BinaryPrimitives.ReadUInt16LittleEndian(root.Read(fver, dataFiles)) != vrsn)
            r.Error("Chunk fver different form 2019–2024 — defined form already change.");

        if (root.Find("mcfg") is { } mcfg && BinaryPrimitives.ReadUInt16LittleEndian(root.Read(mcfg, dataFiles)) != 2100)
            r.Error("Structure shape file data (mcfg) contains a newer record version — conversion is not yet defined.");

        // Object number loda not previously encountered = related object new of newer version.
        var unknownArgs = root.Leaves.Where(l => l.FourCC == "loda")
            .Select(l => LodaRecord.TryParse(root.Read(l, dataFiles), out var rec) ? rec : null)
            .Where(rec => rec is not null)
            .SelectMany(rec => rec!.Args.Keys)
            .Where(k => !KnownLodaArgs.Contains(k))
            .Distinct().ToList();
        if (unknownArgs.Count > 0)
            r.Error("Object has previously unseen attributes (argument type " + string.Join(", ", unknownArgs.Select(k => $"0x{k:X}"))
                    + ") — may contain features from the source version whose conversion is not yet defined.");
        return r.Success;
    }

    // Type object number loda already encounter in every sample 2020–2024 and real file (tools/analysis: statistics list 67 file).
    private static readonly HashSet<uint> KnownLodaArgs =
    [
        0x0A, 0x14, 0x1E, 0x64, 0xC9, 0x3E8, 0x7D0, 0xBB8, 0xDAC, 0x1F40, 0x1F45, 0x2AF8, 0x2EE0, 0x36B1, 0x3E81, 0x3E8F,
        0x4651, 0x4ABA, 0x4B01, 0x9C40, 0x9C41, 0x9C42, 0x9C68, 0x9C72, 0x9C90, 0x9CEA, 0x9D08,
    ];

    // The form record effect application 0xBB8 already known: (type object, version record, uint32).
    private static readonly HashSet<(uint Obj, uint Version, int Length)> KnownEffectRecords =
    [
        (23, 2200, 127), // shadows 2020+ (convert sang 1300)
        (23, 1300, 119), // shadows form 2019
        (15, 1300, 185), // contour
    ];

    /// <summary>
    /// Inner shadow (inner shadow, introduced in 2023) not exist at in 2019 and make 2019 crash when open.
    /// Corel 2024 automatically raster ize when downgrade; at here simple ize with way remove group effect application (lnkg),
    /// entry link result similar application in 'lnkt' and 'spnd' no longer is use — object source unchanged.
    /// </summary>
    private static RootDat RemoveInnerShadows(RootDat root, List<byte[]> dataFiles, DowngradeResult r)
    {
        var groups = new HashSet<int>();
        var linkIds = new HashSet<uint>();
        for (int i = 0; i < root.Lists.Count; i++)
        {
            if (root.Lists[i].Type != "lnkg") continue;
            var loda = root.Leaves.FirstOrDefault(l => l.FourCC == "loda" && l.Parent >= 0
                && root.Lists[l.Parent].Type == "lgob" && root.Lists[l.Parent].Parent == i);
            if (loda is null || !LodaRecord.TryParse(root.Read(loda, dataFiles), out var rec) || rec is null) continue;
            if (rec.ObjType != 23 || !rec.Args.TryGetValue(0xBB8, out byte[]? s) || s.Length < 0x1C
                || BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0x18)) != 4)
                continue;
            groups.Add(i);
            var spnd = root.Leaves.FirstOrDefault(l => l.FourCC == "spnd" && l.Parent == i);
            if (spnd is not null && root.Read(spnd, dataFiles) is { Length: 4 } id)
                linkIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(id));
        }
        if (groups.Count == 0) return root;

        // Skip entry link result export develop from the group already remove; button target no longer link result any then skip 'spnd' of it.
        var lnkt = root.Find("lnkt");
        LinkTable? table = null;
        var orphanIds = new HashSet<uint>();
        if (lnkt is not null && LinkTable.TryParse(root.Read(lnkt, dataFiles), out table) && table is not null)
        {
            var removed = table.Entries.Where(e => linkIds.Contains(LinkTable.From(e))).ToList();
            table.Entries.RemoveAll(removed.Contains);
            foreach (uint to in removed.Select(LinkTable.To))
                if (!table.Entries.Any(e => LinkTable.From(e) == to || LinkTable.To(e) == to))
                    orphanIds.Add(to);
        }
        else if (lnkt is not null)
        {
            r.Error("Not reads table link result effect application (lnkt) — cannot remove shadow in an entire.");
            return root;
        }

        var leafIndex = root.Leaves.Select((l, idx) => (l, idx)).ToDictionary(x => x.l, x => x.idx);
        var dropLeaves = new HashSet<int>();
        foreach (var l in root.Leaves.Where(l => l.FourCC == "spnd"))
            if (root.Read(l, dataFiles) is { Length: 4 } v && orphanIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(v)))
                dropLeaves.Add(leafIndex[l]);
        if (lnkt is not null && table!.Entries.Count == 0)
            dropLeaves.Add(leafIndex[lnkt]);

        var result = root.Without(groups.Contains, dropLeaves.Contains);
        if (table is { Entries.Count: > 0 } && result.Find("lnkt") is { } newLnkt)
            result.Replace(newLnkt, table.ToBytes());
        r.Warn($"{groups.Count} shadow in (inner shadow — calculate ability 2023+) does not have in CorelDRAW 2019: already removes the effect, object main unchanged. If requires keep shadow, please Convert to Bitmap in 2024 before downgrading.");
        return result;
    }

    private static void Audit(RootDat root, List<byte[]> dataFiles, DowngradeResult r)
    {
        foreach (string t in root.ListTypes.Distinct().Where(t => !VerifiedLists.Contains(t)))
            r.Problem($"Group chunk LIST '{t}' not present in the comparison samples — copies it unchanged, CorelDRAW 2019 may not open correctly.");

        foreach (var g in root.Leaves.Where(l => !VerifiedLeaves.Contains(l.FourCC)).GroupBy(l => l.FourCC))
            r.Problem($"Chunk '{g.Key}' (x{g.Count()}) not present in the comparison samples — copies it unchanged, CorelDRAW 2019 may not open correctly.");

        var unknownLoda = root.Leaves.Where(l => l.FourCC == "loda")
            .Select(l => root.Read(l, dataFiles))
            .Where(d => d.Length >= 20)
            .Select(d => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)))
            .Where(t => !VerifiedLodaTypes.Contains(t))
            .GroupBy(t => t);
        foreach (var g in unknownLoda)
            r.Problem($"Object type {g.Key} (x{g.Count()}) is not present in the comparison samples — copying it unchanged.");

        // Variable fonts (OpenType 'fvar'): 2019 does not support variable axes. Corel 2024 automatically converts text to curves
        // when downgrade; C_Converter keeps the text and warns (display in 2019 can different: default weight/width).
        var fonts = root.Leaves.Where(l => l.FourCC == "txsm")
            .SelectMany(l => Regex.Matches(Encoding.UTF8.GetString(root.Read(l, dataFiles)), "\"font\": \"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string font in fonts)
            if (FontCatalog.IsVariable(font) == true)
                r.Warn($"Text uses variable font '{font}' — CorelDRAW 2019 unsupported, text may display different from the source version. Convert to Curves in 2024 before downgrading.");

        // mcfg: record form 2019+ has header u16 = 2100 (2018 become down is 1300 and long more 379 byte).
        if (root.Find("mcfg") is { } mcfg)
        {
            ushort recVer = BinaryPrimitives.ReadUInt16LittleEndian(root.Read(mcfg, dataFiles));
            if (recVer != 2100)
                r.Warn($"mcfg has version record {recVer}, different 2100 as every sample 2019–2024.");
        }
    }

    /// <summary>
    /// Convert the record object (loda) according to rule extract out from comparing with files Corel saved automatically with Save As 2019
    /// (docs/PhatHien-02-loda.md):
    ///  1. 2019 always has object number style (0xC9); 2020+ skipped when empty → add "{}".
    ///     With record of page, same time clear bit 0x40 in the first of 'flgs' page.
    ///  2. JSON style: skip the key only has from 2020 (font_axes, bullet many level).
    ///  3. Drop shadow (type 23), object number 0xBB8: record version 2200 → 1300 (skip 2 field u32).
    /// </summary>
    private static void ConvertObjects(RootDat root, List<byte[]> dataFiles, DowngradeResult r)
    {
        int addedStyle = 0, strippedStyle = 0, shadows = 0;
        foreach (var leaf in root.Leaves.Where(l => l.FourCC == "loda").ToList())
        {
            byte[] raw = root.Read(leaf, dataFiles);
            if (!LodaRecord.TryParse(raw, out var rec) || rec is null)
            {
                r.Warn($"Not reads record object at {leaf.Path} — copies it unchanged.");
                continue;
            }

            bool changed = false;
            bool hasStyle = rec.Args.TryGetValue(LodaRecord.StyleArg, out byte[]? style);
            if (!hasStyle && ContainersNeedingStyle.Contains(ContainerOf(root, leaf)))
            {
                rec.Args[LodaRecord.StyleArg] = LodaRecord.MakeStyle("{}");
                addedStyle++;
                changed = true;
                ClearPageFlag(root, leaf, dataFiles);
            }
            else if (hasStyle && LodaRecord.TryReadStyle(style!, out string json) && json.Length > 2)
            {
                var node = JsonNode.Parse(json);
                if (StripJson(node, r) > 0)
                {
                    rec.Args[LodaRecord.StyleArg] = LodaRecord.MakeStyle(CorelJson.ToCompact(node!));
                    strippedStyle++;
                    changed = true;
                }
            }

            if (r.Strict && rec.Args.TryGetValue(0xBB8, out byte[]? effect) && effect.Length >= 4
                && !KnownEffectRecords.Contains((rec.ObjType, BinaryPrimitives.ReadUInt32LittleEndian(effect), effect.Length)))
                r.Error($"Effect (object type {rec.ObjType}) has record form {BinaryPrimitives.ReadUInt32LittleEndian(effect)}/{effect.Length} byte not previously encountered — conversion is not yet defined.");

            if (rec.ObjType == 23 && rec.Args.TryGetValue(0xBB8, out byte[]? shadow)
                && BinaryPrimitives.ReadUInt32LittleEndian(shadow) == 2200)
            {
                if (shadow.Length == 127)
                {
                    var s = new List<byte>(shadow);
                    s.RemoveRange(0x7B, 4);
                    s.RemoveRange(0x1D, 4);
                    byte[] converted = s.ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(converted, 1300);
                    rec.Args[0xBB8] = converted;
                    shadows++;
                    changed = true;
                }
                else
                {
                    r.Error($"Drop shadow has record long {shadow.Length} byte (sample only has 127) — conversion is not yet defined; CorelDRAW 2019 may not open the file.");
                }
            }

            if (changed)
                root.Replace(leaf, rec.ToBytes());
        }
        int texts = 0;
        foreach (var leaf in root.Leaves.Where(l => l.FourCC == "txsm").ToList())
        {
            var converted = TxsmConverter.Convert(root.Read(leaf, dataFiles), r.Warn, r.Problem,
                r.Strict ? node => CorelJson.StripUnknownKeys(node, r.UnknownKeys) : null);
            if (converted is null) continue;
            root.Replace(leaf, converted);
            texts++;
        }
        if (texts > 0)
            r.Info($"Text: convert {texts} block text sang form 2019.");
        var parts = new List<string>();
        if (addedStyle > 0) parts.Add($"add style empty for {addedStyle} record");
        if (strippedStyle > 0) parts.Add($"skip key 2020+ without style of {strippedStyle} object");
        if (shadows > 0) parts.Add($"convert {shadows} shadows");
        if (parts.Count > 0)
            r.Info("Object attributes: " + string.Join(", ", parts) + ".");
    }

    /// <summary>Rule 2020–2024 already verified; at experimental mode add filter according to schema diagram 2019.</summary>
    private static int StripJson(JsonNode? node, DowngradeResult r)
    {
        int n = CorelJson.StripPost2019Keys(node, r.Warn);
        if (r.Strict) n += CorelJson.StripUnknownKeys(node, r.UnknownKeys);
        return n;
    }

    // Type LIST contain (cha of lgob) that Corel 2019 add style empty when missing. Layer ('layr') then not.
    private static readonly HashSet<string> ContainersNeedingStyle = ["page", "grp", "lnkg"];

    /// <summary>Type LIST contain object of one chunk in lgob (vd 'page', 'obj', 'layr').</summary>
    private static string ContainerOf(RootDat root, RootDat.Leaf leaf)
    {
        if (leaf.Parent < 0 || root.Lists[leaf.Parent].Type != "lgob") return "";
        int c = root.Lists[leaf.Parent].Parent;
        return c < 0 ? "" : root.Lists[c].Type;
    }

    /// <summary>Version write page (page/lgob/loda) just is add style → delete bit 0x40 in flgs of page.</summary>
    private static void ClearPageFlag(RootDat root, RootDat.Leaf loda, List<byte[]> dataFiles)
    {
        if (loda.Parent < 0) return;
        int page = root.Lists[loda.Parent].Parent;
        if (root.Lists[loda.Parent].Type != "lgob" || page < 0 || root.Lists[page].Type != "page") return;
        var flgs = root.Leaves.FirstOrDefault(l => l.FourCC == "flgs" && l.Parent == page);
        if (flgs is null) return;
        byte[] f = root.Read(flgs, dataFiles);
        if (f.Length == 4 && (f[0] & 0x40) != 0)
        {
            f[0] &= 0xBF;
            root.Replace(flgs, f);
        }
    }


    private static void PatchRoot(RootDat root, List<byte[]> dataFiles, CorelVersion target, DowngradeResult r)
    {
        root.FormChar = target.FormChar;

        var fver = root.Find("fver");
        var vrsn = root.Find("vrsn");
        if (fver is not { Inline: true, Length: 8 } || vrsn is not { Inline: true, Length: 2 })
        {
            r.Error("fver/vrsn not at form inline as sample.");
            return;
        }
        root.WriteInline(fver, target.FverBytes());
        Span<byte> v = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(v, target.Vrsn);
        root.WriteInline(vrsn, v);
        r.Info($"root.dat: form type 'CDR{target.FormChar}', fver/vrsn = {target.Vrsn}.");
    }

    private static void PatchMetadata(CdrPackage.Entry entry, CorelVersion target, DowngradeResult r)
    {
        string xml = Encoding.UTF8.GetString(entry.Data);
        xml = ReplaceOnce(xml, @"(?<=<cdr:CoreVersion>)\d+(?=</cdr:CoreVersion>)", target.Vrsn.ToString(), "CoreVersion", r);
        xml = ReplaceOnce(xml, @"(?<=<inTxt:DocFormatterId>)\d+(?=</inTxt:DocFormatterId>)", target.DocFormatterId.ToString(), "DocFormatterId", r);
        entry.Data = Encoding.UTF8.GetBytes(xml);
    }

    private static string ReplaceOnce(string text, string pattern, string value, string what, DowngradeResult r)
    {
        int n = Regex.Matches(text, pattern).Count;
        if (n != 1)
            r.Warn($"metadata.xml: find see {n} time '{what}' (mong wait 1).");
        return Regex.Replace(text, pattern, value);
    }

    private static void PatchStyles(CdrPackage.Entry entry, CorelVersion target, DowngradeResult r)
    {
        string text = Encoding.UTF8.GetString(entry.Data);
        var doc = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("document.cdss empty.");
        doc["version"] = (int)target.Vrsn;

        int removed = StripJson(doc, r);

        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string json = CorelJson.ToIndented(doc, newline);
        if (text.EndsWith(newline) && !json.EndsWith(newline))
            json += newline;
        entry.Data = Encoding.UTF8.GetBytes(json);
        r.Info($"document.cdss: version = {target.Vrsn}, skip {removed} key only has from 2020 (font_axes, bullet).");
    }
}
