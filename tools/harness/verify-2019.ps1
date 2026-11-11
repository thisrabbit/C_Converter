# Set check test (only use when development): open each file .cdr with CorelDRAW 2019 REAL,
# Re-read the file structure (page/layer/object) as JSON and export each page as PNG.
# Compare result of file C_Converter with file 2019 do Corel saved automatically with tools/analysis/compare_render.py.
param(
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$ProgId = 'CorelDRAW.Application.21',
    [int]$Dpi = 72
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
# -ProgId CorelDRAW.Application.25 to build image reference of file source with 2024.
$exeFilter = if ($ProgId -like '*.21') { '*Suite 2019*' } else { '*Suite\25\*' }
function Restart-Corel {
    # Sau when CorelDRAW encounter file broken it often stuck at box dialog hidden → start dynamic again progress process.
    Get-Process CorelDRW -ErrorAction SilentlyContinue | Where-Object { $_.Path -like $exeFilter } | Stop-Process -Force
    Start-Sleep -Seconds 2
    New-Object -ComObject $ProgId
}
$app = New-Object -ComObject $ProgId

function Rnd($v) { [math]::Round([double]$v, 2) }

function Describe-Shape($s) {
    $d = [ordered]@{ type = [int]$s.Type; name = [string]$s.Name
        x = (Rnd $s.LeftX); y = (Rnd $s.BottomY); w = (Rnd $s.SizeWidth); h = (Rnd $s.SizeHeight) }
    try {
        $d.fill = [int]$s.Fill.Type
        if ($d.fill -eq 1) { $d.colorModel = [int]$s.Fill.UniformColor.Type; $d.fillHex = [string]$s.Fill.UniformColor.HexValue }
    } catch {}
    try { $d.outline = (Rnd $s.Outline.Width); $d.outlineType = [int]$s.Outline.Type } catch {}
    try { if ($s.Type -eq 6) { $d.text = [string]$s.Text.Story.Text; $d.font = [string]$s.Text.Story.Font; $d.size = (Rnd $s.Text.Story.Size) } } catch {}
    try { $d.effects = @($s.Effects | ForEach-Object { [int]$_.Type }) } catch {}
    try { if ($s.Type -eq 5) { $d.bitmap = "$($s.Bitmap.SizeWidth)x$($s.Bitmap.SizeHeight) mode=$($s.Bitmap.Mode)" } } catch {}
    try { $d.transparency = [int]$s.Transparency.Type } catch {}
    try { if ($s.Type -eq 7) { $d.children = @($s.Shapes | ForEach-Object { Describe-Shape $_ }) } } catch {}
    try { if ($s.PowerClip) { $d.powerclip = @($s.PowerClip.Shapes | ForEach-Object { Describe-Shape $_ }) } } catch {}
    $d
}

$report = @()
foreach ($f in $Files) {
    $full = (Resolve-Path $f).Path
    $base = [IO.Path]::GetFileNameWithoutExtension($full)
    $entry = [ordered]@{ file = $base; opened = $false }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $doc = $app.OpenDocument($full, 0)
        $entry.opened = $true
        $doc.Unit = 3
        $pages = @()
        for ($i = 1; $i -le $doc.Pages.Count; $i++) {
            $p = $doc.Pages.Item($i)
            $p.Activate()
            $layers = @()
            foreach ($l in $p.Layers) {
                $layers += [ordered]@{ name = [string]$l.Name; visible = [bool]$l.Visible
                    shapes = @($l.Shapes | ForEach-Object { Describe-Shape $_ }) }
            }
            $png = Join-Path $OutDir "$base.p$i.png"
            Remove-Item $png -ErrorAction SilentlyContinue
            # Export(file, cdrPNG=802, cdrCurrentPage=1, options, palette); ImageType 4 = RGB, AntiAliasing 1 = often
            $eo = $app.CreateStructExportOptions(); $eo.ResolutionX = $Dpi; $eo.ResolutionY = $Dpi; $eo.ImageType = 4; $eo.AntiAliasingType = 1
            $doc.Export($png, 802, 1, $eo, $app.CreateStructPaletteOptions())
            $pages += [ordered]@{ index = $i; w = (Rnd $p.SizeWidth); h = (Rnd $p.SizeHeight); layers = $layers }
        }
        $entry.pages = $pages
        $doc.Close()
    } catch {
        $entry.error = $_.Exception.Message
        try { $doc.Close() } catch {}
        $app = Restart-Corel
    }
    $entry.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $report += $entry
    Write-Host ("{0,-40} {1}" -f $base, $(if ($entry.opened -and -not $entry.error) { 'open OK' } else { "ERROR: $($entry.error)" }))
}
$json = ConvertTo-Json $report -Depth 30
[IO.File]::WriteAllText((Join-Path $OutDir 'report.json'), $json, (New-Object Text.UTF8Encoding $false))
