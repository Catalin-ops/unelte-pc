# Test real: partitie NTFS noua (VHD), fisiere scrise si sterse definitiv, apoi cautare rapida + profunda + recuperare.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, System.Drawing
$vhd = Join-Path $env:RUNNER_TEMP 'test_ntfs.vhd'
$dp = Join-Path $env:RUNNER_TEMP 'dp.txt'
@"
create vdisk file="$vhd" maximum=400 type=fixed
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=ntfs quick label=TESTREC
assign letter=T
"@ | Set-Content $dp -Encoding ASCII
diskpart /s $dp | Out-Host
Start-Sleep 3
if (-not (Test-Path 'T:\')) { throw 'Partitia de test nu s-a creat' }

# fisiere de test: un xlsx real (zip cu structura Excel), un pdf, o poza
function New-Xlsx($path, $title, $kb) {
    $fs = [IO.File]::Create($path)
    $z = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($e in @(@('[Content_Types].xml', '<Types/>'), @('xl/workbook.xml', '<workbook><sheets/></workbook>'),
                     @('docProps/core.xml', "<cp:coreProperties><dc:title>$title</dc:title><dcterms:modified>2026-10-15T10:00:00Z</dcterms:modified></cp:coreProperties>"))) {
        $w = New-Object IO.StreamWriter($z.CreateEntry($e[0]).Open()); $w.Write($e[1]); $w.Close()
    }
    $rnd = New-Object Random 3; $b = New-Object byte[] ($kb * 1024); $rnd.NextBytes($b)
    $s = $z.CreateEntry('xl/media/image1.bin', [IO.Compression.CompressionLevel]::NoCompression).Open(); $s.Write($b, 0, $b.Length); $s.Close()
    $z.Dispose(); $fs.Close()
}
New-Item -ItemType Directory 'T:\Documente\Contabilitate' | Out-Null
New-Xlsx 'T:\Documente\Contabilitate\Buget 2026.xlsx' 'Buget anual' 300
New-Xlsx 'T:\Documente\Inventar.xlsx' 'Inventar magazie' 40
$bmp = New-Object Drawing.Bitmap 800, 600; $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::DarkCyan); $g.FillEllipse([Drawing.Brushes]::Gold, 100, 80, 500, 400); $g.Dispose()
$bmp.Save('T:\Documente\poza.jpg', [Drawing.Imaging.ImageFormat]::Jpeg)
$orig = @{}
Get-ChildItem T:\Documente -Recurse -File | ForEach-Object { $orig[$_.Name] = (Get-FileHash $_.FullName -Algorithm MD5).Hash; Write-Host "creat $($_.FullName) $($_.Length) B" }
Write-VolumeCache T
Get-ChildItem T:\Documente -Recurse -File | Remove-Item -Force       # stergere definitiva (ca Shift+Delete)
Write-VolumeCache T
Start-Sleep 5

$rec = [Reflection.Assembly]::LoadFrom((Resolve-Path 'dist\RecuperareFisiere.exe'))
$out = Join-Path $env:RUNNER_TEMP 'rec_out'
New-Item -ItemType Directory -Force $out | Out-Null
function Run($e, $what) {
    $t = 0; while (-not $e.Finished -and $t -lt 1800) { Start-Sleep -Milliseconds 100; $t++ }
    if ($e.Error) { throw "$what : $($e.Error)" }
    $items = @(); $it = $null
    while ($e.NewItems.TryDequeue([ref]$it)) { $items += $it }
    return ,$items
}
$e = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e.SourcePath = '\\.\T:'; $e.DestDir = $out
$e.WantPdf = $true; $e.WantWord = $true; $e.WantJpg = $true; $e.WantExcel = $true; $e.WantPpt = $true; $e.SkipSmallJpg = $false; $e.FreeSpaceOnly = $true
$e.StartQuick()
$found = Run $e 'rapida'
Write-Host "RAPIDA: $($found.Count) fisiere"
foreach ($f in $found) { Write-Host ("   {0} | {1} | {2} | {3}" -f $f.Name, $f.Folder, $f.SizeText, $f.Status) }
if (-not ($found | Where-Object { $_.Name -eq 'Buget 2026.xlsx' })) { throw 'Cautarea rapida nu a gasit Buget 2026.xlsx' }

$arrT = [Array]::CreateInstance($rec.GetType('InfoPcRecover.FoundFile'), $found.Count)
for ($i = 0; $i -lt $found.Count; $i++) { $arrT[$i] = $found[$i] }
$e.StartRecover($arrT)
$null = Run $e 'recuperare'
$ok = 0
foreach ($f in $found) {
    if ($f.SavedPath -and (Test-Path -LiteralPath $f.SavedPath)) {
        $h = (Get-FileHash -LiteralPath $f.SavedPath -Algorithm MD5).Hash
        $same = $orig[$f.Name] -eq $h
        Write-Host "   recuperat $($f.SavedPath) identic=$same"
        if ($same) { $ok++ }
    }
}
if ($ok -lt $orig.Count) { throw "Recuperare rapida: doar $ok din $($orig.Count) identice" }

$e2 = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e2.SourcePath = '\\.\T:'; $e2.DestDir = (Join-Path $out 'profunda')
$e2.WantPdf = $true; $e2.WantWord = $true; $e2.WantJpg = $true; $e2.WantExcel = $true; $e2.WantPpt = $true; $e2.SkipSmallJpg = $false; $e2.FreeSpaceOnly = $true
New-Item -ItemType Directory -Force $e2.DestDir | Out-Null
$e2.StartDeep()
$deep = Run $e2 'profunda'
Write-Host "PROFUNDA: $($deep.Count) fisiere"
foreach ($f in $deep) { Write-Host ("   {0} | {1} | {2}" -f $f.Name, $f.SizeText, $f.Status) }
$hashes = @($deep | ForEach-Object { (Get-FileHash -LiteralPath $_.SavedPath -Algorithm MD5).Hash })
foreach ($k in $orig.Keys) { if ($hashes -notcontains $orig[$k]) { throw "Cautarea profunda nu a recuperat identic $k" } }
Write-Host 'Test NTFS: toate fisierele gasite si recuperate identic.'

# Cosul de reciclare: stergere simpla (Delete), fisierul e doar mutat in $Recycle.Bin
Add-Type -AssemblyName Microsoft.VisualBasic
New-Xlsx 'T:\Documente\Oferta client.xlsx' 'Oferta' 60
$hOf = (Get-FileHash 'T:\Documente\Oferta client.xlsx' -Algorithm MD5).Hash
[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('T:\Documente\Oferta client.xlsx', 'OnlyErrorDialogs', 'SendToRecycleBin')
Write-VolumeCache T; Start-Sleep 5
$e3 = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e3.SourcePath = '\\.\T:'; $e3.DestDir = (Join-Path $out 'cos')
New-Item -ItemType Directory -Force $e3.DestDir | Out-Null
$e3.WantPdf = $true; $e3.WantWord = $true; $e3.WantJpg = $true; $e3.WantExcel = $true; $e3.WantPpt = $true
$e3.StartQuick()
$f3 = Run $e3 'cos'
foreach ($f in $f3) { Write-Host ("   {0} | {1} | {2}" -f $f.Name, $f.Folder, $f.Status) }
$of = $f3 | Where-Object { $_.Name -eq 'Oferta client.xlsx' }
if (-not $of -or $of.Status -notlike '*reciclare') { throw 'Fisierul din Cosul de reciclare nu a fost gasit cu numele original' }
$a1 = [Array]::CreateInstance($rec.GetType('InfoPcRecover.FoundFile'), 1); $a1[0] = $of
$e3.StartRecover($a1); $null = Run $e3 'recuperare cos'
if ((Get-FileHash -LiteralPath $of.SavedPath -Algorithm MD5).Hash -ne $hOf) { throw 'Fisierul din Cos nu s-a recuperat identic' }
Write-Host "Cos de reciclare: OK ($($e3.InRecycleBin) fisier)"

# SSD cu TRIM simulat: un PDF sters, apoi golim (zero) a doua jumatate a datelor lui direct in discul virtual
$sb = New-Object Text.StringBuilder
[void]$sb.Append("%PDF-1.4`n")
for ($i = 0; $i -lt 6000; $i++) { [void]$sb.Append("% linie de continut $i pentru testul de recuperare`n") }
[void]$sb.Append("trailer << /Root 1 0 R >>`n%%EOF`n")
[IO.File]::WriteAllText('T:\Documente\Raport.pdf', $sb.ToString())
Write-VolumeCache T
Remove-Item 'T:\Documente\Raport.pdf' -Force
Write-VolumeCache T; Start-Sleep 5
$e5 = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e5.SourcePath = '\\.\T:'; $e5.DestDir = $out; $e5.WantPdf = $true
$e5.StartQuick(); $f5 = Run $e5 'pdf'
$pdf = $f5 | Where-Object { $_.Name -eq 'Raport.pdf' }
if (-not $pdf -or $pdf.Status -ne 'Bun') { throw "PDF-ul intact ar trebui sa fie Bun, e: $($pdf.Status)" }
$run = $pdf.Runs[0]; $cl = $pdf.ClusterSize
$partOff = (Get-Partition -DriveLetter T).Offset
foreach ($x in @($e, $e2, $e3, $e5)) { try { $x.Close() } catch {} }
[GC]::Collect(); [GC]::WaitForPendingFinalizers()
"select vdisk file=`"$vhd`"`ndetach vdisk" | Set-Content $dp -Encoding ASCII
diskpart /s $dp | Out-Host
for ($w = 0; $w -lt 20 -and (Get-DiskImage -ImagePath $vhd).Attached; $w++) { Start-Sleep 1 }
if ((Get-DiskImage -ImagePath $vhd).Attached) { throw 'Discul virtual nu s-a desprins' }
$half = [long]($run[1] / 2)
$fsv = New-Object IO.FileStream($vhd, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
$fsv.Position = $partOff + ($run[0] + $half) * $cl
$z = New-Object byte[] (($run[1] - $half) * $cl)
$fsv.Write($z, 0, $z.Length); $fsv.Flush($true); $fsv.Close()
Write-Host "Golire: LCN=$($run[0]) clustere=$($run[1]) cluster=$cl partitie@$partOff -> octeti $($z.Length) de la $($partOff + ($run[0] + $half) * $cl); runs=$($pdf.Runs.Count) size=$($pdf.Size)"
$chk = [IO.File]::OpenRead($vhd); $chk.Position = $partOff + $run[0] * $cl; $hb = New-Object byte[] 16; [void]$chk.Read($hb, 0, 16); $chk.Close()
Write-Host ("Inceput fisier in VHD: " + [Text.Encoding]::ASCII.GetString($hb))
"select vdisk file=`"$vhd`"`nattach vdisk`nselect partition 1`nassign letter=T" | Set-Content $dp -Encoding ASCII
diskpart /s $dp | Out-Host
for ($w = 0; $w -lt 20 -and -not (Test-Path 'T:\'); $w++) { Start-Sleep 1 }
$e6 = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e6.SourcePath = '\\.\T:'; $e6.DestDir = $out; $e6.WantPdf = $true
$e6.StartQuick(); $f6 = Run $e6 'pdf golit'
$pdf2 = $f6 | Where-Object { $_.Name -eq 'Raport.pdf' }
Write-Host "PDF dupa golirea a jumatate din date: $($pdf2.Status) (selectat: $($pdf2.Selected))"
if (-not $pdf2 -or $pdf2.Status -notlike '*TRIM*' -or $pdf2.Selected) { throw 'PDF-ul golit partial nu a fost detectat' }

# tipuri nebifate: cauta fara Excel, trebuie sa raporteze Excel-urile sarite
$e4 = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e4.SourcePath = '\\.\T:'; $e4.DestDir = $out
$e4.WantPdf = $true; $e4.WantWord = $true; $e4.WantJpg = $true
$e4.StartQuick(); $null = Run $e4 'nebifate'
$sk = 0; [void]$e4.SkippedKinds.TryGetValue('Excel', [ref]$sk)
Write-Host "Excel sarite cand nu e bifat: $sk"
if ($sk -lt 1) { throw 'Nu au fost raportate fisierele Excel sarite' }
"select vdisk file=`"$vhd`"`ndetach vdisk" | Set-Content $dp -Encoding ASCII
diskpart /s $dp | Out-Null
