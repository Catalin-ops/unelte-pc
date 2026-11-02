# Test de pornire in CI: incarca fiecare exe ca assembly, verifica ca XAML-ul se parseaza
# si ruleaza colectorii Sanatate PC (fara interfata), ca sa prindem erori inainte de livrare.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

foreach ($exe in 'RecuperareFisiere', 'SanatatePC') {
    $asm = [Reflection.Assembly]::LoadFrom((Resolve-Path "dist\$exe.exe"))
    $s = $asm.GetManifestResourceStream('MainWindow.xaml')
    if (-not $s) { throw "$exe : lipseste resursa MainWindow.xaml" }
    $w = [Windows.Markup.XamlReader]::Load($s)
    Write-Host "$exe : XAML OK ($($w.Title))"
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
