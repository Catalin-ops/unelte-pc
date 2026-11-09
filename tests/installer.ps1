# Construieste installer-ul Sanatate PC, il testeaza (instalare + dezinstalare silentioasa) si face capturile pentru Store.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$ver = '1.3.0'

# Inno Setup + traducerea romaneasca (neoficiala, din sursele Inno Setup)
if (-not (Test-Path 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')) { choco install innosetup -y --no-progress | Out-Null }
try {
    Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/Unofficial/Romanian.isl' -OutFile 'installer\Romanian.isl'
    Write-Host 'Traducere romaneasca: OK'
} catch { Write-Host "Traducerea romaneasca nu s-a putut descarca, installer-ul ramane in engleza: $($_.Exception.Message)" }
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' /Q "/DAppVersion=$ver" 'installer\SanatatePC.iss'
if ($LASTEXITCODE -ne 0) { throw 'Compilarea installer-ului a esuat' }
$setup = Resolve-Path "dist\SanatatePC_Setup_$ver.exe"
Write-Host "Installer: $setup ($((Get-Item $setup).Length) octeti)"

# test instalare silentioasa (asa o face Microsoft Store)
$p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Instalarea a iesit cu codul $($p.ExitCode)" }
$app = 'C:\Program Files\Info PC Electric\Sanatate PC\SanatatePC.exe'
if (-not (Test-Path $app)) { throw 'Dupa instalare nu exista SanatatePC.exe' }
$lnk = Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu\Programs" -Filter '*.lnk' | Where-Object { $_.Name -like 'S*n*tate PC.lnk' }
if (-not $lnk) { throw 'Lipseste scurtatura din meniul Start' }
$reg = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' | Get-ItemProperty | Where-Object { $_.DisplayName -like 'S*n*tate PC*' }
if (-not $reg) { throw 'Aplicatia nu apare in Aplicatii instalate' }
Write-Host "Instalat: $($reg.DisplayName) $($reg.DisplayVersion) de $($reg.Publisher)"

# dezinstalare silentioasa
$un = Join-Path (Split-Path $app) 'unins000.exe'
$p = Start-Process -FilePath $un -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
if (Test-Path $app) { throw 'Dezinstalarea nu a sters aplicatia' }
Write-Host 'Dezinstalare: OK'

# capturi in modul Free (fara licenta), ca sa verificam ce se vede gratuit
New-Item -ItemType Directory -Force 'shots\free' | Out-Null
foreach ($pg in 'sumar', 'discuri', 'stabilitate', 'todo', 'ram') {
    $out = Join-Path (Get-Location).Path "shots\free\$pg.png"
    $q = Start-Process -FilePath (Resolve-Path 'dist\SanatatePC.exe') -ArgumentList "--pagina=$pg", "--captura=$out", '--marime=1600x1000' -PassThru
    if (-not $q.WaitForExit(90000)) { $q.Kill() }
}

# capturi pentru Microsoft Store, 1920x1080, versiunea Pro
New-Item -ItemType Directory -Force 'shots\store' | Out-Null
$valid = ((Get-Content 'tests\licente_test.txt') | Where-Object { $_ -like 'screenshot=*' }).Substring(11)
Set-Content -Path 'dist\licenta.spc' -Value $valid -Encoding ASCII
foreach ($pg in 'sumar', 'todo', 'live', 'ram', 'gpu', 'discuri', 'stabilitate') {
    $out = Join-Path (Get-Location).Path "shots\store\$pg.png"
    $q = Start-Process -FilePath (Resolve-Path 'dist\SanatatePC.exe') -ArgumentList "--pagina=$pg", "--captura=$out", '--marime=1920x1080' -PassThru
    if (-not $q.WaitForExit(90000)) { $q.Kill() }
    if (Test-Path $out) { Write-Host "captura store $pg" } else { Write-Host "captura store $pg LIPSA" }
}
Remove-Item 'dist\licenta.spc' -Force
