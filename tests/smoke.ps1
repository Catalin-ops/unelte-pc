# Test de pornire in CI: incarca fiecare exe ca assembly, verifica ca XAML-ul se parseaza
# si ruleaza colectorii Sanatate PC (fara interfata), ca sa prindem erori inainte de livrare.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

foreach ($exe in 'RecuperareFisiere', 'SanatatePC', 'GeneratorLicente') {
    $asm = [Reflection.Assembly]::LoadFrom((Resolve-Path "dist\$exe.exe"))
    foreach ($rn in $asm.GetManifestResourceNames() | Where-Object { $_ -like '*.xaml' }) {
        $w = [Windows.Markup.XamlReader]::Load($asm.GetManifestResourceStream($rn))
        Write-Host "$exe : $rn OK ($($w.Title))"
    }
}

$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path 'dist\SanatatePC.exe'))
$c = $asm.GetType('InfoPcDisk.Collect')
$st = $c.GetMethod('ReadStability').Invoke($null, @())
$sn = $c.GetMethod('ReadSensors').Invoke($null, @())
$secs = @(
    $c.GetMethod('SystemInfo').Invoke($null, @($st)),
    $c.GetMethod('Cpu').Invoke($null, @($sn, $st)),
    $c.GetMethod('Board').Invoke($null, @($sn)),
    $c.GetMethod('Ram').Invoke($null, @($st)),
    $c.GetMethod('Gpu').Invoke($null, @($sn, $st)),
    $c.GetMethod('Monitors').Invoke($null, @()),
    $c.GetMethod('Network').Invoke($null, @()),
    $c.GetMethod('StabilitySection').Invoke($null, @($st))
)
foreach ($sec in $secs) {
    Write-Host ("[{0}] {1} - {2}" -f $sec.Status, $sec.Name, $sec.Summary)
    foreach ($card in $sec.Cards) {
        Write-Host ("    {0} | {1} | {2}" -f $card.Title, $card.Subtitle, $card.Status)
        foreach ($kv in $card.Props) { Write-Host ("        {0}: {1}" -f $kv.Key, $kv.Value) }
        foreach ($n in $card.NoteList) { Write-Host ("        ! {0}" -f $n) }
    }
}

# licenta: cheie valida, expirata, modificata
$L = $asm.GetType('InfoPcDisk.Licensing')
$keys = @{}
foreach ($line in Get-Content 'tests\licente_test.txt') { if ($line -match '^\w+=') { $k, $val = $line -split '=', 2; $keys[$k] = $val } }
$v = $L.GetMethod('Parse').Invoke($null, @($keys['screenshot']))
$x = $L.GetMethod('Parse').Invoke($null, @($keys['expired']))
$t = $L.GetMethod('Parse').Invoke($null, @($keys['tampered']))
Write-Host "Licenta capturi: Valid=$($v.Valid) IsPro=$($v.IsPro) Nume=$($v.Name) Expira=$($v.ExpiresText)"
Write-Host "Licenta expirata: Valid=$($x.Valid) Expired=$($x.Expired) IsPro=$($x.IsPro)"
Write-Host "Licenta modificata: Valid=$($t.Valid) Eroare=$($t.Error)"
if (-not $v.Valid -or $x.IsPro -or -not $x.Valid -or -not $x.Expired -or $t.Valid) { throw 'Testul de licenta a esuat' }

# licenta pe stick: salvata doar pe PC, la pornire se copiaza langa aplicatie
if ($v.IsPro) {
    $exeLic = Join-Path (Split-Path (Resolve-Path 'dist\SanatatePC.exe')) 'licenta.spc'
    if (Test-Path $exeLic) { Remove-Item $exeLic -Force }
    [void]$L.GetMethod('Save').Invoke($null, [object[]]@($v, $false))
    $copied = $L.GetMethod('CopyToExeDirIfMissing').Invoke($null, [object[]]@($v))
    Write-Host "Licenta copiata langa aplicatie: $copied, exista: $(Test-Path $exeLic)"
    if (-not $copied -or -not (Test-Path $exeLic)) { throw 'Licenta nu a fost copiata langa aplicatie' }
    $attr = (Get-Item $exeLic -Force).Attributes
    Write-Host "Atribute licenta: $attr"
    if (($attr -band [IO.FileAttributes]::Hidden) -eq 0) { throw 'Licenta de pe stick nu e ascunsa' }
    $re = $L.GetMethod('Load').Invoke($null, @())
    if (-not $re.IsPro) { throw 'Licenta ascunsa nu mai poate fi citita' }
    $L.GetMethod('Remove').Invoke($null, @()) | Out-Null
    if (Test-Path $exeLic) { throw 'Licenta ascunsa nu s-a putut sterge din aplicatie' }
    $L.GetMethod('Remove').Invoke($null, @()) | Out-Null
} else { Write-Host 'Licenta de test a expirat: sar testul de copiere pe stick' }

# raport PDF din sectiunile citite mai sus
New-Item -ItemType Directory -Force shots | Out-Null
$R = $asm.GetType('InfoPcDisk.Report')
$secT = $asm.GetType('InfoPcDisk.Section')
$list = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($secT))
foreach ($sec in $secs) { $list.Add($sec) }
$dl = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($asm.GetType('InfoPcDisk.DiskReport')))
$set = [Activator]::CreateInstance($asm.GetType('InfoPcDisk.Settings'))
$set.Company = 'Info PC Electric S.R.L.'; $set.Phone = '0700 000 000'; $set.Email = 'contact@infopc.ro'; $set.Website = 'www.infopc.ro'; $set.Address = 'Rediu, jud. Neamt'; $set.Technician = 'Test CI'
$doc = $R.GetMethod('Build').Invoke($null, @($list, $dl, $v, $set, 'Client Test SRL', 'Recomand curatarea de praf si inlocuirea pastei termoconductoare.'))
$pdfPath = [string](Join-Path (Get-Location).Path 'shots\raport_test.pdf')
$pages = $R.GetMethod('SavePdf').Invoke($null, [object[]]@($doc, $pdfPath, [string]'Info PC Electric - Sanatate PC Pro'))
Write-Host "PDF: $pages pagini, $((Get-Item shots\raport_test.pdf).Length) octeti"
if ($pages -lt 1) { throw 'PDF gol' }

# proiect cu 3 calculatoare + raport pe institutie
$recsT = $asm.GetType('InfoPcDisk.Recs')
$recNow = $recsT.GetMethod('Take').Invoke($null, @())
Write-Host "Recomandari pentru PC-ul de test: $($recNow.Count)"
foreach ($r in $recNow) { Write-Host ("   [{0}] {1} | {2} | {3}" -f $r.PriorityText, $r.Component, $r.Action, $r.Part) }
$P = $asm.GetType('InfoPcDisk.Projects'); $PR = $asm.GetType('InfoPcDisk.PcRecord'); $RecT = $asm.GetType('InfoPcDisk.Rec')
$base = [string](Join-Path $env:RUNNER_TEMP 'proiecte')
$proj = $P.GetMethod('Create').Invoke($null, [object[]]@($base, [string]'Scoala Gimnaziala Test'))
function NewRec($pri, $comp, $act, $det, $part) { $r = [Activator]::CreateInstance($RecT); $r.Priority = $pri; $r.Component = $comp; $r.Action = $act; $r.Detail = $det; $r.Part = $part; return $r }
$labels = @(@('Secretariat PC1', 'Secretariat', 'Maria Ionescu', 0), @('Laborator PC3', 'Sala 12', '', 2), @('Director', 'Direcțiune', 'Director', 1))
foreach ($lb in $labels) {
    $rec = $PR.GetMethod('FromCurrent').Invoke($null, [object[]]@($list, $dl, $recNow, [string]$lb[0], [string]$lb[1], [string]$lb[2], [string]'verificat in CI'))
    $rec.Level = $lb[3]; $rec.Score = @(100, 35, 72)[$lb[3]]
    if ($lb[3] -eq 2) { $rec.Recs.Add((NewRec 1 'Disc' 'Backup imediat și înlocuire disc WDC WD5000 (500 GB)' '12 sectoare instabile.' 'SSD 512 GB SATA 2.5"')) ; $rec.Recs.Add((NewRec 2 'Memorie RAM' 'Upgrade memorie RAM la minim 8 GB' 'Acum 4 GB; există slot liber.' 'Memorie RAM 4 GB DDR4 DIMM')) }
    if ($lb[3] -eq 1) { $rec.Recs.Add((NewRec 2 'Disc' 'Înlocuire HDD de sistem cu SSD' 'Windows rulează de pe HDD.' 'SSD 512 GB SATA 2.5"')) }
    $P.GetMethod('Save').Invoke($null, @($proj, $rec))
}
$all = $P.GetMethod('List').Invoke($null, [object[]]@($base))
$pj = $all[0]
Write-Host "Proiect: $($pj.Name) - $($pj.Sub)"
if ($pj.Pcs.Count -ne 3) { throw "Proiectul ar trebui sa aiba 3 calculatoare, are $($pj.Pcs.Count)" }
$pdoc = $R.GetMethod('BuildProject').Invoke($null, @($pj, $v, $set))
$pdfP = [string](Join-Path (Get-Location).Path 'shots\raport_proiect.pdf')
$np = $R.GetMethod('SavePdf').Invoke($null, [object[]]@($pdoc, $pdfP, [string]'Info PC Electric - Scoala Gimnaziala Test'))
Write-Host "PDF proiect: $np pagini"

# motorul de recuperare: test pe o imagine de disc sintetica (doar cautare profunda)
$rec = [Reflection.Assembly]::LoadFrom((Resolve-Path 'dist\RecuperareFisiere.exe'))
$img = Join-Path $env:RUNNER_TEMP 'disk.img'
$out = Join-Path $env:RUNNER_TEMP 'recuperate'
New-Item -ItemType Directory -Force $out | Out-Null
$rnd = New-Object Random 7
$buf = New-Object byte[] (2MB); $rnd.NextBytes($buf)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 640, 480
$g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::SteelBlue); $g.FillEllipse([Drawing.Brushes]::Orange, 100, 100, 300, 200); $g.Dispose()
$ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Jpeg); $jpg = $ms.ToArray()
[Array]::Copy($jpg, 0, $buf, 4096, $jpg.Length)
[IO.File]::WriteAllBytes($img, $buf)
$e = [Activator]::CreateInstance($rec.GetType('InfoPcRecover.Engine'))
$e.SourcePath = $img; $e.DestDir = $out; $e.WantJpg = $true; $e.SkipSmallJpg = $false; $e.FreeSpaceOnly = $false
$e.StartDeep()
$t = 0; while (-not $e.Finished -and $t -lt 300) { Start-Sleep -Milliseconds 100; $t++ }
if ($e.Error) { throw "Motor: $($e.Error)" }
$f = Get-ChildItem -Recurse $out -File
Write-Host "Recuperare test: $($f.Count) fisier(e)"
if ($f.Count -ne 1 -or $f[0].Length -ne $jpg.Length) { throw "Recuperarea de test nu a dat fisierul corect" }
Write-Host 'Toate testele au trecut.'
