# C_Converter — Downgrade CorelDRAW files

*Downgrade CorelDraw Version*

Convert `.cdr` files from **CorelDRAW 2020, 2021, 2022, 2023, and 2024** to **CorelDRAW 2019**, without installing CorelDRAW (no COM and no calls to Corel software). **2025 and 2026 are experimental** (see below).

This is a real version downgrade: records are rewritten in the 2019 format. It is not a header-only version-number change or a conversion to another format.

## Usage

**Interface:** run `dist/C_Converter.exe` (a single self-contained file; .NET installation is not required).
1. Drag `.cdr` files or folders into the window, or click *Add files… / Add folder…*.
2. Choose an output location: beside the source file (with the `_2019` suffix) or in a separate folder.
3. Click **Start**. Each file has its own status and log; use *Save log…* to export all logs.

**Command line:**
```
dotnet src/CConvert.Cli/bin/Release/net8.0/CConvert.Cli.dll <file.cdr> [...] [--out <folder>]
```

Safety guarantees:
- **Never overwrite** a source or existing file. A numeric suffix such as ` (1)` or ` (2)` is added when needed; output is written to a temporary file before it is renamed.
- A failed file does not stop the batch.
- Files already at 2019 or earlier, and non-CorelDRAW files, are skipped with an explanation.

## Confidence level

Tested with real CorelDRAW 2019 by opening files, comparing structure, and comparing rendered images pixel by pixel with files Corel saved using “Save As 2019”. The 45 samples cover basic shapes, RGB/CMYK colors, strokes/arrows, groups, layers, multiple pages, Vietnamese text, paragraphs, bullets, bitmaps, gradients, PowerClip, lenses, transparency, shadows, contours, envelopes, extrusions, symbols, and textures, plus five real-world files. Details: [docs/PhatHien-02-loda.md](docs/PhatHien-02-loda.md).

When an object or chunk is not present in the comparison samples, C_Converter copies it unchanged but logs a specific warning.

Known limitations (all produce warnings):
- **Inner shadow** (2023+): CorelDRAW 2019 does not support it, so C_Converter removes the effect while keeping the object. To preserve the appearance, use *Convert to Bitmap* in 2024 before downgrading.
- **Variable fonts** (for example, Bahnschrift): CorelDRAW 2019 renders them using the font’s default style, so *Convert to Curves* first.
- **Bullets using a non-default bullet font:** text-to-bullet spacing may differ by a few millimeters.
- Versions below 2019 (X7, X8, 2017, and 2018) are not supported, and X6 or earlier sources are not read.

## Experimental mode: 2025 / 2026 sources

There are no 2025/2026 samples for verification, so files newer than 2024 are handled by inference:
- Recognition is based on **structure** (ZIP + `root.dat` + `fver`/`vrsn` + `mcfg` in the 2100 format used by 2019–2024), not on a version-name table. Different structure is rejected.
- The 2020–2024 rules are reused, with **JSON filtering against the 2019 schema** (`Schema2019.g.cs`, derived from 54 real 2019 files by `tools/analysis/json2019.py`). Keys absent from 2019 are removed and listed in the log.
- An object attribute, text form, effect form, or chunk never seen before causes an **error and no output file** instead of risking a file that could crash CorelDRAW 2019.
- Results are labeled **“Done (experimental)”** and should be opened and checked in CorelDRAW 2019 before delivery.

Verified: experimental logic produces the same results as the verified logic on 42 2024 samples; the 2019 schema does not remove any valid keys. Simulated “2025/2026” files (`tools/analysis/simulate_newer.py`) open in CorelDRAW 2019 and render identically to Corel-downgraded files. When CorelDRAW 2025 is available (a trial version is sufficient), run the test harness with `CorelDRAW.Application.26` to enable official support.

## Code structure

| Directory | Contents |
|---|---|
| `src/CConvert.Core` | Core library: ZIP, `root.dat` (RIFF pointers), `loda`, `txsm`, `lnkt`, and conversion rules (`Downgrader`) |
| `src/CConvert.App` | WPF interface |
| `src/CConvert.Cli` | Command-line interface |
| `tests/CConvert.Tests` | Tests (requires `samples/gen`, created by the test harness below) |
| `tools/CdrDump` | Chunk-tree dump tool for `.cdr` files |
| `tools/analysis` | Python scripts for binary and image comparison |
| `tools/harness` | **Development only:** controls CorelDRAW 2024/2019 through COM to create samples and verify results |
| `docs/` | Format survey and discovered rules |

## Development
```
dotnet test tests/CConvert.Tests                                   # 167 tests
powershell -File tools/harness/run-regression.ps1 -Regenerate       # requires CorelDRAW 2024 + 2019
dotnet publish src/CConvert.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```
