# Porneste fiecare aplicatie si salveaza o captura de ecran (verificare vizuala in CI).
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force shots | Out-Null
function Shot($exe, $file, $wait) {
    $p = Start-Process -FilePath (Resolve-Path "dist\$exe.exe") -PassThru
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
Shot 'SanatatePC' 'sanatate.png' 25
Shot 'RecuperareFisiere' 'recuperare.png' 10
