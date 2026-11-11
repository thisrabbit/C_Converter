# Findings from samples T00 (blank page) and T01 (one red rectangle)

Samples: `samples/T00_*.cdr`, `samples/T01_*.cdr`, versions 17, 18, 19, 20, 21, 22, 23, 24, 24.3, and 25.
Tools: `tools/CdrDump` (chunk tree) and `tools/analysis/cdrtree.py` (chunk-content comparison).

All statements below were verified on 20 sample files, except where marked [Phase]. The samples contain only one object, so the conclusions are *not yet applicable to text, bitmaps, or effects.

## 1. Container (17 → 25 are identical)
- ZIP, first entry is `mimetype` not compressed = `application/x-vnd.corel.zcf.draw.document+zip` (type ODF).
- `META-INF/container.xml` pointer to `content/root.dat`.
- `content/root.dat` is RIFF `CDR?`; the fourth character of the form type changes by version (see the table in section 3).
- `content/dataFileList.dat`: list of file names separated by `\n`: `data1.dat`, `masterPage.dat`, `page1.dat`.
- Also included: `previews/*.png`, `color/*.xml`, an ICC profile (T01 embeds `uswebcoatedswop.icc`, 557 KB, which explains the file’s roughly 400 KB size), `styles/document.cdss` (JSON), `META-INF/metadata.xml`, `textinfo.xml`, and `links.xml`.

## 2. Chunk leaves in root.dat and their 16-byte pointers
```
u32 fileIndex   // only number in dataFileList (0-based); 0xFFFFFFFF = data inline
u32 length
u32 offset      // or: inline 8-byte data (bytes 8..15)
u32 reserved    // = 0 on every sample
```
Verified: the offset and length of each linked chunk match the correct size in every `.dat` file.
Structure: `doc ` → `data1.dat`; master page (`page[0]`) → `masterPage.dat`; page 1 → `page1.dat`.

## 3. Version version
| Product | Sample | `vrsn` | Form type | `fver` (8 bytes) | `DocFormatterId` |
|---|---|---|---|---|---|
| X7 | 17 | 1700 | `CDRH` | `A4 06 01 00 00 00 11 00` | 1700 |
| X8 | 18 | 1800 | `CDRJ` | `08 07 01 00 00 00 12 00` | 1800 |
| 2017 | 19 | 1900 | `CDRK` | `6C 07 01 00 00 00 13 00` | 1800 |
| 2018 | 20 | 2000 | `CDRL` | `D0 07 01 00 00 00 14 00` | 1800 |
| **2019** | 21 | 2100 | `CDRM` | `34 08 01 00 00 00 15 00` | **1700** |
| 2020 | 22 | 2200 | `CDRN` | `98 08 01 00 00 00 16 00` | 1800 |
| 2021 | 23 | 2300 | `CDRO` | `FC 08 01 00 00 00 17 00` | 1800 |
| 2022 | 24 | 2400 | `CDRP` | `60 09 01 00 00 00 18 00` | 1800 |
| 2023 | 24.3 | 2430 | `CDRQ` | `7E 09 01 00 00 03 18 00` | 1800 |
| 2024 | 25 | 2500 | `CDRR` | `C4 09 01 00 00 00 19 00` | 1800 |

Name product product do user use exact accepts (machine has 2024 and 2019; sample create with 2024 then Save As).
`fver` = `u16 vrsn, u16 1, u8 0, u8 update version, u8 major version, u8 0` [field name Phase].
`metadata.xml`: `CoreVersion` = vrsn; `Revision` is the save counter (not a version field).

## 4. Differences between versions — very few
Tree chunk **identical** 17→25. Chunk contents are byte-for-byte identical, except:

| Component | Differences |
|---|---|
| `fver`, `vrsn` | version number |
| `doc/mcfg` | Two forms: 17–20 (8323 bytes, header `u16=1300`) and 21–25 (7944 bytes, header `u16=2100`). Version 21+ omits 8 bytes at 0x365 and 371 bytes at 0x37A (the block contains the UTF‑16 string `CorelDefaultID_10900` …). Header: `u16 record version, u16 0, uint32 remaining length`. |
| `trfd` | 17: field @0x18 = `0x0514` (1300), @0x1C = 0; 18+: `0x06CC` (1740), @0x1C = `0x38` [meaning is Phase] |
| `metadata.xml` | `CoreVersion`, `Revision`, `DocFormatterId` |
| `document.cdss` | `version`; GUID; from 22 (2020) add key `"font_axes"` → 17 KB become 25 KB |
| `mcfg` of 24.3 | different 24 at few byte (Phase: install set machine, not defined form) |

**System result:** across X7–2023, even for this simple file, downgrading means rewriting the target-version fields (`fver`, `vrsn`, `mcfg`, `trfd`, `metadata.xml`, and `document.cdss`) exactly as Corel does with Save As—not merely changing the version number. The converter should process each chunk type: copy unchanged chunks and transform only chunks with known rules, rather than rewriting the entire file. This is a practical Reader → Model → Writer design for this range; broader structural knowledge is still needed for the project plan and versions older than X7.

## 5. Rectangle object (page[1]/…/layr[3]/obj[0])
- Single position: **0,1 µm** (254 000 unit/inch). `bbox` = (-1479500, -1047000, -479500, -1547000) → 100 × 50 mm ✓.
- Root coordinate system / Y-axis direction: [Phase] — requires T02 (move 1 mm) to define precisely.
- `loda` (558 bytes), record type = 1 (rectangle, matching libcdr's identification). Header: `u32 length, u32 numArgs=6, u32 offsetTableOffset=0x14, u32 offsetTableType=0x30, u32 chunkType=1`; argument types: 0x0A, 0x14, 0x1E, 0x64, 0xC9, 0x9C41.
- Has **string JSON** in `loda`: `{"fill":{"primaryColor":"RGB255,USER,255,0,0,100,…"},"outline":{…"width":"0"},"transparency":{}}`.
- Geometry: the rectangle’s source size (double) is 6,705,941 × −4,582 …; applying the transformation matrix `trfd` (six doubles at @0x28: a=0.14912, b=0, tx=−1479500, c=0, d=0.10912, ty=−1047000) produces 100 × 50 mm. The shape was drawn and then scaled; Corel stores the source size and transformation matrix.
- `fild` (121 bytes) and `outl` (237 bytes) in `doc ` contain `0x0960` = 2400 **even in the X7 file** → Corel copies this value unchanged during downgrade (it is not the file version).

## 6. Open questions
1. Does a file downgraded from Corel 2022 to X7 open successfully in real X7 (including all JSON strings in `loda`)? → requires testing.
2. What is the meaning of the 379-byte block in `mcfg` that versions 21+ remove? When downgrading 21+ → ≤20, can it be copied from the target version’s T00 template?
3. Does a sample created natively on X7 differ from one downgraded from 2022?

## 7. Downgrade 2020–2024 → 2019: experimental implementation (src/CConvert.Core)
Comparing 2024 with 2019 on T00/T01: the chunk tree is identical; `mcfg`, `trfd`, and every other chunk are **byte-for-byte identical**. The differences requiring conversion are:
1. `root.dat`: character automatically form type → `M`; `fver`, `vrsn` → 2100.
2. `metadata.xml`: `CoreVersion` → 2100, `DocFormatterId` → 1700.
3. `document.cdss`: `version` → 2100; skip every `font_axes` key; in the `paragraph` object, skip `levels`, `bulletFormat`, `bulletInheritFont`, `bulletNumberStyle`, `bulletFirstCharIndentIncludesBullet`, and `numberInheritFont`. The rule is the same for every 2020–2024 source. Corel writes an empty object as `{` + newline + `}`.

Result: converting T00/T01 from 2020–2024 to 2019 produces output **byte-for-byte identical** to files saved by CorelDRAW 2019 (apart from `Revision` and naturally generated GUIDs) in 9/10 fields. The remaining T00_24.3 differences are already present in the source file (`mcfg`, palette), not conversion errors.

Unknown chunks, LISTs, or `loda` types cause the converter to log a clear warning instead of silently copying them.
