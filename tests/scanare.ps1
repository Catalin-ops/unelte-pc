# Test in CI pentru Scanare Retea: ruleaza motorul fara interfata, apoi porneste aplicatia
# cu scanare automata si salveaza o captura de ecran.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing, System.Windows.Forms
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path 'dist\ScanareRetea.exe'))
$eng = $asm.GetType('ScanareRetea.Engine')
$out = $eng.GetMethod('SelfTest').Invoke($null, @())
Write-Host $out

New-Item -ItemType Directory -Force shots | Out-Null
try { Set-DisplayResolution -Width 1600 -Height 1000 -Force -ErrorAction Stop } catch { Write-Host "rezolutia nu s-a putut schimba" }
$p = Start-Process -FilePath (Resolve-Path 'dist\ScanareRetea.exe') -ArgumentList '--scaneaza' -PassThru
Start-Sleep -Seconds 40
if ($p.HasExited) { throw "Aplicatia s-a inchis singura (cod $($p.ExitCode))" }
$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
$bmp.Save("shots\scanare.png", [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
$p.Kill()
Write-Host "captura shots\scanare.png"
