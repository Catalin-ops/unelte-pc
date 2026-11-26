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
Remove-Item 'dist\licenta.spc' -Force
Shot 'RecuperareFisiere' 'recuperare.png' 10
Shot 'GeneratorLicente' 'generator.png' 6

# raportul 3D generat de smoke.ps1, randat cu Edge (WebGL prin SwiftShader)
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$r3 = Join-Path (Resolve-Path 'shots').Path 'Raport3D_test.html'
if ($edge -and (Test-Path $r3)) {
    foreach ($v in @(@('raport3d.png', '1600,1000'), @('raport3d_telefon.png', '412,900'))) {
        $out = Join-Path (Resolve-Path 'shots').Path $v[0]
        Start-Process -FilePath $edge -Wait -ArgumentList @('--headless=new', "--user-data-dir=$env:TEMP\edge3d", '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--hide-scrollbars', "--window-size=$($v[1])", '--virtual-time-budget=8000', "--screenshot=$out", ('file:///' + ($r3 -replace '\\', '/')))
        Write-Host "captura $($v[0]): $(Test-Path $out)"
    }
}

# TEMPORAR: verificare vizuala a raportului 3D (se scoate)
foreach ($f in 'raport3d.png','raport3d_telefon.png','sanatate_pro.png') {
    $pth = Join-Path (Resolve-Path 'shots').Path $f
    if (-not (Test-Path $pth)) { continue }
    $img = [Drawing.Image]::FromFile($pth); $w = [Math]::Min(900, $img.Width); $h = [int]($img.Height * $w / $img.Width)
    $sm = New-Object Drawing.Bitmap $img, $w, $h; $ms = New-Object IO.MemoryStream
    $sm.Save($ms, [Drawing.Imaging.ImageFormat]::Jpeg); $img.Dispose(); $sm.Dispose()
    $b = [Convert]::ToBase64String($ms.ToArray())
    for ($i = 0; $i -lt $b.Length; $i += 4000) { Write-Host ("B64|$f|" + $b.Substring($i, [Math]::Min(4000, $b.Length - $i))) }
}
