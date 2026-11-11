# C_Converter — Phase 0: Survey of the .cdr format

Confidence labels:
- **[Certain]**: independently confirmed by multiple sources (libcdr, Inkscape/LibreOffice import, or direct inspection of ZIP files).
- **[libcdr]**: inferred from how libcdr reads files. Reliable for *reading*, but not necessarily sufficient for *writing*.
- **[Phase]**: an assumption requiring verification with sample files.

---

## 1. Version groups

| Group | Versions (estimated) | Container | Notes |
|---|---|---|---|
| A | CorelDRAW 1–2 | Not RIFF (different signature) [Phase] | Skipped; there is no practical need. |
| B | 3 → X3 | Bare RIFF: `RIFF <u32 size> "CDR?"` [Certain] | The fourth form-type character changes by version [Certain]; the character-to-version table is [Phase]. From about v8, a `cmpr` chunk contains zlib-compressed data [libcdr]; the first version using `cmpr` is [Phase]. |
| C | X4 → present | ZIP containing the main RIFF stream [Certain] | Stream names according to libcdr: `content/riffData.cdr`, or `content/root.dat` + `content/dataFileList.dat` + `content/data/*.dat` [libcdr]. Which versions use which layout is [Phase]. The archive also contains `META-INF/`, metadata XML, and thumbnail images [exact names are Phase]. |

Important consequence: **XML inside the ZIP is primarily metadata, not the drawing**. Geometry remains in binary RIFF data for every version from 3 onward [libcdr]. Therefore, downgrading from 2020 to X7 is a binary RIFF-writing problem, not an XML-editing problem.

### Common chunks [libcdr — names are known; field meanings require verification]
- `vrsn`: u16 version number, for example 1700 [libcdr]; divide by 100 to map it to the product name (17 = X7…) [Phase].
- `LIST doc `: document-level data — `mcfg` (page structure), shared color/fill/outline/style tables, fonts (`font`), bitmaps (`bmp `), and arrow names (`arrw`)…
- `LIST page` → `LIST layr` → `LIST obj ` / `LIST grp `: page/layer/object/group tree.
- Inside objects: `loda` (geometry data: rectangle/ellipse/path/text/bitmap…), `trfd` (transformation matrix), `lgob`, `flgs`, `bbox`, `usdn`…
- `fild`/`outl`: fill/outline data and object references by ID [libcdr]. The style-save format changes around X6 (`stlt`) [Phase].
- `txsm`/`stlt`: text and style; the text format changes substantially by version (Unicode from about X4) [Phase].
- `DISP`: preview image [libcdr].
- Coordinate scales and field numbers differ between old and new versions; libcdr has separate branches for each version milestone [libcdr; exact values are Phase].

## 2. Feasibility

| Area | Assessment |
|---|---|
| Reading chunk structure (RIFF tree, ZIP, `cmpr`) | **High feasibility.** The `cdrdump` utility already does this. |
| Reading basic geometry, fill, outline, and color | **Feasible.** libcdr implements this, and Inkscape/LibreOffice use it for import. |
| Reading PowerClip, blend, mesh, lens, contour, and effects | **Difficult/incomplete.** libcdr skips some of these or handles only limited cases. |
| **Writing .cdr** | **The project’s greatest risk.** There is no known open-source writer implementation. libcdr only needs fields required for reading; CorelDRAW may require additional fields to open a file (IDs, default styles, `mcfg`, font tables, and chunks libcdr skips). These must be reverse-engineered through binary comparison and testing in real CorelDRAW. |
| Lossless downgrading | **Not generally feasible.** CorelDRAW’s “Save As” to an older version can also lose features. The practical goal is to preserve the rendered result, avoid writing unsupported features, and report limitations clearly. |

### Template-based writer strategy
1. For each target version, create one empty file in that CorelDRAW version as a template.
2. Keep every not-yet-understood chunk byte-for-byte unchanged (`doc `, `mcfg`, default styles…).
3. Insert or transform only the `page/layr/obj` tree and fill/outline tables already understood through binary comparison.
4. Recalculate every RIFF/LIST, `cmpr`, and ZIP size field.

This reduces what must be understood before CorelDRAW can open the first generated file. The template is a file created by the user in the target CorelDRAW version, not a coordinate-only approximation.

### Choosing a target version
The older RIFF format (group B) is simpler. In practice, meaningful support requires X7/X6/X5 (group C). Choose based on real-world needs: the ZIP wrapper is only a container, while the difficult part is the RIFF stream inside it.

### Alternative export formats
- **PDF / AI (PDF format):** valid in CorelDRAW X3 and later [import quality by version is Phase].
- **EPS / old AI (PostScript):** valid for very old versions.
- **SVG:** import quality is poor in older CorelDRAW versions; use only as a last resort.
- **CMX:** Corel’s exchange format may be useful because all Corel versions can open it, but its data model is not fully understood [Phase].

## 3. Risks

1. CorelDRAW may open a malformed file incorrectly or silently (missing content, a crash, or an unexplained corruption report). Repeated trial-and-error testing is slow; change one chunk at a time and test across versions.
2. Auxiliary fields linking fill, outline, style, and font tables can invalidate the entire file when one ID is wrong.
3. A checksum or hash may exist somewhere [Phase — not yet observed; sample comparison should reveal it].
4. Text encoding, fonts, kerning, and paragraphs are expensive to reproduce. Converting text to curves is reliable, but requires the fonts and a glyph engine on the C_Converter machine (for example DirectWrite or FreeType).
5. Bitmap formats, compression, palettes, and alpha channels differ by version.
6. Incorrect CMYK/Pantone/ICC profiles can shift printed colors.
7. Service packs and hotfixes may introduce small format differences [Phase]; record the exact build used for each sample.
8. Reverse engineering may be restricted by the CorelDRAW EULA. This project analyzes file data, not CorelDRAW’s implementation. libcdr is available under MPL-2.0; derivative code must retain the license.

## 4. Development comparison tools

- **Inkscape** (imports `.cdr` through libcdr): render a reference for comparison.
- **libcdr `cdr2raw` / `cdr2xhtml`:** useful for seeing what libcdr understands; installable through MSYS2 [Phase].
- **CorelDRAW on a test machine:** the authoritative source for writer behavior.

## 5. Sample-file standard

**Original rule:** for each required case, create the sample in the newest available CorelDRAW version, then use *File → Save As → choose Version* to save every supported older version. These files are test data; the Corel-saved versions are the expected answers.

Use names such as `Txx_<description>_<version>.cdr`, for example `T01_rect_X7.cdr`. Record the exact build from Help → About in `samples/VERSIONS.txt`.

| Code | Content | Goal |
|---|---|---|
| T00 | Blank A4 page | Writer template |
| T01 | One 100×50 mm rectangle at (10,10) mm, red RGB(255,0,0), no outline | Minimum object case |
| T02 | T01 moved by exactly 1 mm | Locate coordinate fields and units |
| T03 | T01 with CMYK(0,100,100,0) fill | Understand CMYK color encoding |
| T04 | Two T01 rectangles touching | IDs and repeated structure |
| T05 | 50×30 mm ellipse | Ellipse |
| T06 | Two-point straight path, 1 mm black outline | Outline |
| T07 | Four-node Bézier curve | Path |
| T08 | T06 with dashed blue outline and arrowheads | Advanced outline |
| T09 | Rounded rectangle | Rectangle geometry |
| T10 | Rectangle rotated 30° | Transformation matrix |
| T11 | Group containing T01 + T05 | Group |
| T12 | Two layers, one object per layer | Layers |
| T13 | Two pages with different sizes (A4 portrait, A5 landscape) | Multiple pages |
| T14 | Artistic text “Xin hello PATH”, Arial 24 pt, plus a converted-to-curves copy | Text and Vietnamese language |
| T15 | Several lines of paragraph text | Text frames |
| T16 | Small transparent-background PNG and one JPG | Bitmaps |
| T17 | Linear red-to-blue gradient | Gradient |
| T18 | Radial gradient | Gradient |
| T19 | PowerClip | Unsupported feature |
| T20 | Blend between two shapes | Unsupported feature |
| T21 | Mesh fill | Unsupported feature |
| T22 | Transparency/lens | Unsupported feature |
| T99 | 3–5 real work files that may be shared | Real-world testing |

Save each case in the newest available version and every target version to support (for example 2020, X8, X7, X6, X5, X4, X3). If a save has options such as compression, embedded fonts, or CMX, keep the defaults and record them.

## 6. Phase 0 completion criteria
- [x] `cdrdump` reads ZIP, RIFF, and `cmpr` (already run on simulated files; **not yet** run on real `.cdr` files).
- [ ] `cdrdump` runs on every sample without stopping at “unrecognized FourCC”; if it stops, revise the `cmpr`/padding assumptions.
- [ ] Experimental table: version ↔ container ↔ form-type character ↔ `vrsn` ↔ presence of `cmpr` ↔ RIFF stream name in ZIP.
- [ ] Compare T01/T02/T03/T04 in one version to locate coordinate, color, and ID chunks/offsets.

## Using cdrdump
```
dotnet run --project tools/CdrDump -c Release -- samples/T01_rect_X7.cdr --stats
dotnet run --project tools/CdrDump -c Release -- samples/*.cdr --stats --out dumps
```
Then compare `dumps/T01_rect_X7.cdr.tree.txt` with `dumps/T01_rect_2020.cdr.tree.txt` using any diff utility.
