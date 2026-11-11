# Full regression (only use when development, requires CorelDRAW 2024 + 2019 on machine):
#   1. (depending choose) create again sample with CorelDRAW 2024          -Regenerate
#   2. downgrade every *_25.cdr sample to 2019 with C_Converter
#   3. compare byte-for-byte with file Corel saved automatically with Save As 2019 (*_21.cdr)
#   4. open all second with CorelDRAW 2019 real, comparing structure + image
param(
    [string]$Samples = (Join-Path $PSScriptRoot '..\..\samples\gen'),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\out\regression'),
    [switch]$Regenerate
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:PYTHONIOENCODING = 'utf-8'

if ($Regenerate) { & (Join-Path $PSScriptRoot 'make-samples.ps1') -OutDir $Samples }
$Samples = (Resolve-Path $Samples).Path
Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$Out\converted", "$Out\verify" | Out-Null

dotnet build "$repo\src\CConvert.Cli" -c Release | Select-String ' error |Error\(s\)'
$cli = "$repo\src\CConvert.Cli\bin\Release\net8.0\CConvert.Cli.dll"
dotnet $cli (Get-ChildItem "$Samples\*_25.cdr").FullName --out "$Out\converted" | Out-File "$Out\convert.log" -Encoding utf8
Select-String -Path "$Out\convert.log" -Pattern 'FAILURE|\[ERROR\]' | ForEach-Object { $_.Line }

Write-Host "`n== So each byte with CorelDRAW (Save As 2019)"
python "$repo\tools\analysis\compare.py" $Samples "$Out\converted" | Select-Object -Last 1

Write-Host "`n== Open with CorelDRAW 2019"
$files = @(Get-ChildItem "$Out\converted\*_2019.cdr") + @(Get-ChildItem "$Samples\*_21.cdr")
& (Join-Path $PSScriptRoot 'verify-2019.ps1') -Files ($files.FullName) -OutDir "$Out\verify" | Where-Object { $_ -notmatch 'open OK' }
python "$repo\tools\analysis\compare_render.py" "$Out\verify" | Where-Object { $_ -notmatch '^MATCH' }
