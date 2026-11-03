# Porneste fiecare aplicatie si salveaza o captura de ecran (verificare vizuala in CI).
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force shots | Out-Null
function Shot($exe, $file, $wait, $args2) {
    if ($args2) { $p = Start-Process -FilePath (Resolve-Path "dist\$exe.exe") -ArgumentList $args2 -PassThru }
    else { $p = Start-Process -FilePath (Resolve-Path "dist\$exe.exe") -PassThru }
    Start-Sleep -Seconds $wait
    $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
    $bmp.Save("shots\$file", [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    if (-not $p.HasExited) { $p.Kill() }
    Write-Host "captura $file"
}
try {
    Set-DisplayResolution -Width 1600 -Height 1000 -Force -ErrorAction Stop
} catch { Write-Host "rezolutia nu s-a putut schimba: $($_.Exception.Message)" }
Shot 'SanatatePC' 'sanatate_free.png' 25
Shot 'SanatatePC' 'sanatate_free_ram.png' 20 '--pagina=ram'
$valid = ((Get-Content 'tests\licente_test.txt') | Where-Object { $_ -like 'screenshot=*' }).Substring(11)
Set-Content -Path 'dist\licenta.spc' -Value $valid -Encoding ASCII
Shot 'SanatatePC' 'sanatate_pro.png' 25
Shot 'SanatatePC' 'sanatate_pro_ram.png' 20 '--pagina=ram'
Shot 'SanatatePC' 'sanatate_pro_live.png' 30 '--pagina=live'
Shot 'SanatatePC' 'sanatate_pro_disc.png' 20 '--pagina=discuri'
Remove-Item 'dist\licenta.spc'
Shot 'RecuperareFisiere' 'recuperare.png' 10
Shot 'GeneratorLicente' 'generator.png' 6
