# Test harness (used only during development; not a C_Converter dependency):
# Control CorelDRAW 2024 through COM, draw each sample, then save two versions:
#   <name>_25.cdr  (version source 2024)   and   <name>_21.cdr  (CorelDRAW automatically "Save As" 2019 = answer plan)
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\samples\gen'),
    [string[]]$Only
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$assets = Join-Path $OutDir 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# Test image: transparent PNG background + JPG
Add-Type -AssemblyName System.Drawing
$png = Join-Path $assets 'alpha.png'
if (-not (Test-Path $png)) {
    $bmp = New-Object System.Drawing.Bitmap 64, 48
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse([System.Drawing.Brushes]::Blue, 4, 4, 40, 40)
    $g.FillRectangle([System.Drawing.Brushes]::Orange, 30, 10, 30, 30)
    $g.Dispose(); $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$jpg = Join-Path $assets 'photo.jpg'
if (-not (Test-Path $jpg)) {
    $bmp = New-Object System.Drawing.Bitmap 80, 60
    for ($x = 0; $x -lt 80; $x++) { for ($y = 0; $y -lt 60; $y++) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(($x * 3) % 256, ($y * 4) % 256, 128)) } }
    $bmp.Save($jpg, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
}

$app = New-Object -ComObject CorelDRAW.Application.25
function Rgb($r, $g, $b) { $app.CreateRGBColor($r, $g, $b) }

# Each sample: name => scriptblock receiving ($doc, $layer). Units are millimeters; the origin is the page’s bottom-left corner.
$samples = [ordered]@{
    'T00_empty'      = { param($doc, $L) }
    'T01_rect'       = { param($doc, $L) $s = $L.CreateRectangle2(10, 10, 100, 50); $s.Fill.UniformColor.RGBAssign(255, 0, 0); $s.Outline.SetNoOutline() }
    'T02_rect_moved' = { param($doc, $L) $s = $L.CreateRectangle2(11, 10, 100, 50); $s.Fill.UniformColor.RGBAssign(255, 0, 0); $s.Outline.SetNoOutline() }
    'T03_rect_cmyk'  = { param($doc, $L) $s = $L.CreateRectangle2(10, 10, 100, 50); $s.Fill.UniformColor.CMYKAssign(0, 100, 100, 0) }
    'T04_two_rects'  = { param($doc, $L) foreach ($x in 10, 120) { $s = $L.CreateRectangle2($x, 10, 50, 50); $s.Fill.UniformColor.RGBAssign(0, 128, 0) } }
    'T05_ellipse'    = { param($doc, $L) $s = $L.CreateEllipse2(80, 150, 25, 15); $s.Fill.UniformColor.RGBAssign(0, 0, 255) }
    'T06_line'       = { param($doc, $L) $s = $L.CreateLineSegment(20, 20, 180, 200); $s.Outline.Width = 1 }
    'T07_curve'      = { param($doc, $L) $s = $L.CreateCurveSegment2(20, 50, 60, 200, 140, -50, 190, 120); $s.Outline.Width = 0.5; $s.Outline.Color.RGBAssign(200, 0, 100) }
    'T08_outline'    = { param($doc, $L) $s = $L.CreateLineSegment(20, 100, 180, 100); $s.Outline.Width = 2; $s.Outline.Color.RGBAssign(0, 0, 255); $s.Outline.Style = $app.OutlineStyles.Item(5); $s.Outline.EndArrow = $app.ArrowHeads.Item(3) }
    'T09_roundrect'  = { param($doc, $L) $s = $L.CreateRectangle2(30, 30, 120, 80, 10, 10, 10, 10); $s.Fill.UniformColor.RGBAssign(255, 200, 0) }
    'T10_rotated'    = { param($doc, $L) $s = $L.CreateRectangle2(50, 100, 100, 50); $s.Fill.UniformColor.RGBAssign(255, 0, 0); $s.Rotate(30) }
    'T11_group'      = { param($doc, $L) $a = $L.CreateRectangle2(10, 10, 60, 40); $b = $L.CreateEllipse2(120, 40, 30, 20); $r = $app.CreateShapeRange(); $r.Add($a); $r.Add($b); $r.Group() | Out-Null }
    'T12_layers'     = { param($doc, $L) $L.CreateRectangle2(10, 10, 50, 50) | Out-Null; $L2 = $doc.ActivePage.CreateLayer('Lop thu hai'); $L2.CreateEllipse2(150, 150, 30, 30) | Out-Null }
    'T13_pages'      = { param($doc, $L) $L.CreateRectangle2(10, 10, 50, 50) | Out-Null; $doc.AddPages(1) | Out-Null; $p = $doc.Pages.Item(2); $p.SetSize(210, 148); $p.ActiveLayer.CreateEllipse2(100, 70, 30, 20) | Out-Null }
    'T14_text'       = { param($doc, $L) $t = $L.CreateArtisticText(20, 200, 'Xin chào ĐƯỜNG phố'); $t.Text.Story.Font = 'Arial'; $t.Text.Story.Size = 24 }
    'T14b_text_curves' = { param($doc, $L) $t = $L.CreateArtisticText(20, 200, 'Xin chào ĐƯỜNG phố'); $t.Text.Story.Font = 'Arial'; $t.Text.Story.Size = 24; $t.ConvertToCurves() }
    'T15_paragraph'  = { param($doc, $L) $L.CreateParagraphText(20, 250, 190, 150, "Dòng thứ nhất của đoạn văn bản.`rDòng thứ hai, có dấu tiếng Việt: ăâđêôơư.`rDòng thứ ba.") | Out-Null }
    'T16_bitmap'     = { param($doc, $L) $L.Import($png, 0, $app.CreateStructImportOptions()); $L.Import($jpg, 0, $app.CreateStructImportOptions()) }
    'T17_linear'     = { param($doc, $L) $s = $L.CreateRectangle2(20, 20, 150, 80); $s.Fill.ApplyFountainFill((Rgb 255 0 0), (Rgb 0 0 255), 1, 0) | Out-Null }
    'T18_radial'     = { param($doc, $L) $s = $L.CreateEllipse2(100, 150, 50, 50); $s.Fill.ApplyFountainFill((Rgb 255 255 0), (Rgb 0 128 0), 2, 0) | Out-Null }
    'T19_powerclip'  = { param($doc, $L) $c = $L.CreateEllipse2(100, 150, 50, 50); $i = $L.CreateRectangle2(60, 120, 100, 80); $i.Fill.UniformColor.RGBAssign(255, 0, 0); $i.AddToPowerClip($c) | Out-Null }
    'T20_blend'      = { param($doc, $L) $a = $L.CreateRectangle2(10, 10, 30, 30); $a.Fill.UniformColor.RGBAssign(255, 0, 0); $b = $L.CreateEllipse2(170, 200, 20, 20); $b.Fill.UniformColor.RGBAssign(0, 0, 255); $a.CreateBlend($b, 10, 0, 0, 0, 0, $false, $null, $false, 0, 0, $false) | Out-Null }
    'T22_transparency' = { param($doc, $L) $a = $L.CreateRectangle2(20, 20, 100, 100); $a.Fill.UniformColor.RGBAssign(255, 0, 0); $b = $L.CreateRectangle2(60, 60, 100, 100); $b.Fill.UniformColor.RGBAssign(0, 0, 255); $b.Transparency.ApplyUniformTransparency(50) | Out-Null }
    'T23_shadow'     = { param($doc, $L) $a = $L.CreateRectangle2(40, 40, 100, 60); $a.Fill.UniformColor.RGBAssign(0, 160, 255); $a.CreateDropShadow(1, 60, 10, 3, -3, (Rgb 0 0 0), 0, 0, 0, 0, 0, 3) | Out-Null }
    'T23b_shadow_soft' = { param($doc, $L) $a = $L.CreateEllipse2(100, 150, 50, 30); $a.Fill.UniformColor.RGBAssign(255, 0, 0); $a.CreateDropShadow(1, 80, 25, -5, -8, (Rgb 0 0 128), 0, 0, 0, 0, 0, 3) | Out-Null }
    'T23c_shadow_persp' = { param($doc, $L) $a = $L.CreateRectangle2(50, 100, 80, 120); $a.Fill.UniformColor.RGBAssign(0, 160, 0); $a.CreateDropShadow(2, 50, 5, 0, 0, (Rgb 0 0 0), 0, 0, 0, 0, 0, 3) | Out-Null }
    'T23d_shadow_text' = { param($doc, $L) $t = $L.CreateArtisticText(20, 200, 'Bóng chữ'); $t.Text.Story.Font = 'Arial'; $t.Text.Story.Size = 48; $t.CreateDropShadow(1, 40, 3, 2, -2, (Rgb 0 0 0), 0, 0, 0, 0, 0, 3) | Out-Null }
    'T24_contour'    = { param($doc, $L) $a = $L.CreateEllipse2(100, 150, 40, 30); $a.Fill.UniformColor.RGBAssign(255, 128, 0); $a.CreateContour(2, 3, 4, 0, (Rgb 0 0 0), (Rgb 255 255 0), (Rgb 255 0 0), 0, 0, 0, 0, 15) | Out-Null }
    'T21_lens'       = { param($doc, $L) $a = $L.CreateRectangle2(20, 20, 150, 100); $a.Fill.UniformColor.RGBAssign(0, 128, 255); $b = $L.CreateEllipse2(95, 70, 40, 40); $b.CreateLens(2, 50, (Rgb 255 0 0), (Rgb 0 0 0), 0) | Out-Null }
    'T26_envelope'   = { param($doc, $L) $a = $L.CreateRectangle2(30, 30, 140, 80); $a.Fill.UniformColor.RGBAssign(0, 200, 100); $a.CreateEnvelope(1, 0, $false) | Out-Null }
    'T27_extrude'    = { param($doc, $L) $a = $L.CreateRectangle2(40, 40, 80, 60); $a.Fill.UniformColor.RGBAssign(200, 0, 0); $a.CreateExtrude(1, 0, 150, 200, 20, 0, (Rgb 200 0 0), (Rgb 0 0 0), 0, 0, (Rgb 0 0 0), $false) | Out-Null }
    'T28_pushpull'   = { param($doc, $L) $a = $L.CreatePolygon(30, 250, 180, 100, 8); $a.Fill.UniformColor.RGBAssign(150, 0, 200); $a.CreatePushPullDistortion(105, 175, 40) | Out-Null }
    'T29_pattern'    = { param($doc, $L) $a = $L.CreateRectangle2(20, 20, 150, 100); $a.Fill.ApplyPatternFill(1, '', 0, (Rgb 0 0 0), (Rgb 255 255 255), $false) | Out-Null }
    'T30_texture'    = { param($doc, $L) $a = $L.CreateRectangle2(20, 20, 150, 100); $a.Fill.ApplyTextureFill('', '') | Out-Null }
    'T31_spot'       = { param($doc, $L) $a = $L.CreateRectangle2(20, 20, 100, 60); $a.Fill.UniformColor = $app.CreatePaletteColor('pantone+ solid coated', 10) }
    'T32_inner_shadow' = { param($doc, $L) $a = $L.CreateRectangle2(40, 40, 100, 60); $a.Fill.UniformColor.RGBAssign(255, 220, 0); $a.CreateInnerShadow(60, 10, 3, -3, (Rgb 0 0 0), 0, 0, 3, 0) | Out-Null }
    'T33_symbol'     = { param($doc, $L) $a = $L.CreateEllipse2(50, 50, 20, 20); $a.Fill.UniformColor.RGBAssign(255, 0, 0); $a.ConvertToSymbol('ky_hieu_1') | Out-Null }
    'T34_to_bitmap'  = { param($doc, $L) $a = $L.CreateEllipse2(80, 80, 40, 30); $a.Fill.ApplyFountainFill((Rgb 255 0 0), (Rgb 0 0 255), 1, 45) | Out-Null; $a.ConvertToBitmapEx(4, $false, $true, 150, 1, $true, $false, 95) | Out-Null }
    'T35_spiral'     = { param($doc, $L) $L.CreateSpiral(30, 250, 180, 100, 5, 0, 50) | Out-Null }
    'T36_bullets'    = { param($doc, $L) $t = $L.CreateParagraphText(20, 270, 190, 150, "Mục thứ nhất`rMục thứ hai`rMục thứ ba"); $t.Text.Story.ApplyBulletEffect() }
    'T37_varfont'    = { param($doc, $L) $t = $L.CreateArtisticText(20, 200, 'Bahnschrift biến thể'); $t.Text.Story.Font = 'Bahnschrift'; $t.Text.Story.Size = 30 }
    'T38_many'       = { param($doc, $L) $rnd = New-Object Random 42; for ($i = 0; $i -lt 300; $i++) { $s = $L.CreateRectangle2($rnd.Next(0, 190), $rnd.Next(0, 280), $rnd.Next(2, 20), $rnd.Next(2, 20)); $s.Fill.UniformColor.RGBAssign($rnd.Next(256), $rnd.Next(256), $rnd.Next(256)) } }
    'T39_bitmap_effect' = { param($doc, $L) $L.Import($jpg, 0, $app.CreateStructImportOptions()); $b = $doc.ActiveShape; $b.ApplyEffectInvert() }
    'T40_mixed'      = { param($doc, $L)
        $g = $L.CreateRectangle2(10, 200, 190, 80, 5, 5, 5, 5); $g.Fill.ApplyFountainFill((Rgb 255 240 200), (Rgb 255 160 0), 1, 90) | Out-Null
        $t = $L.CreateArtisticText(20, 240, 'CÔNG TY TNHH ĐẠI PHÁT'); $t.Text.Story.Font = 'Arial'; $t.Text.Story.Size = 28; $t.Text.Story.Bold = $true
        $L.Import($png, 0, $app.CreateStructImportOptions())
        $c = $L.CreateEllipse2(105, 100, 60, 60); $c.Outline.Width = 1.5; $c.Outline.Color.CMYKAssign(100, 0, 0, 0)
        $i = $L.CreateRectangle2(40, 40, 130, 120); $i.Fill.ApplyFountainFill((Rgb 0 100 255), (Rgb 0 255 100), 2, 0) | Out-Null; $i.AddToPowerClip($c) | Out-Null
        $doc.AddPages(1) | Out-Null; $p2 = $doc.Pages.Item(2); $p2.ActiveLayer.CreateParagraphText(20, 270, 190, 200, 'Trang 2: nội dung đoạn văn.') | Out-Null }
    'T25_polygon'    = { param($doc, $L) $L.CreatePolygon(30, 250, 180, 100, 6) | Out-Null; $L.CreatePolygon(30, 90, 120, 10, 5, 1, 1, $true) | Out-Null }
}

$results = @()
foreach ($name in $samples.Keys) {
    if ($Only -and $name -notin $Only) { continue }
    $f25 = Join-Path $OutDir "${name}_25.cdr"; $f21 = Join-Path $OutDir "${name}_21.cdr"
    try {
        $doc = $app.CreateDocument()
        $doc.Unit = 3   # cdrMillimeter
        $doc.ActivePage.SetSize(210, 297)
        & $samples[$name] $doc $doc.ActiveLayer
        Remove-Item $f25, $f21 -ErrorAction SilentlyContinue
        $o = $app.CreateStructSaveAsOptions(); $o.Version = 25; $doc.SaveAs($f25, $o)
        $o = $app.CreateStructSaveAsOptions(); $o.Version = 21; $doc.SaveAs($f21, $o)
        $doc.Close()
        $results += "OK    $name"
    } catch {
        $results += "ERROR  $name : $($_.Exception.Message)"
        try { $doc.Close() } catch {}
    }
}
$results
