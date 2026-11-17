# Wi-Fi Stabil in CI: XAML, licenta separata de Sanatate PC, motorul de masurare, raportul HTML, capturi si pachetul MSIX.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
New-Item -ItemType Directory -Force 'shots\wifi' | Out-Null
$exe = (Resolve-Path 'dist\WiFiStabil.exe').Path
$asm = [Reflection.Assembly]::LoadFrom($exe)

# 1. ferestrele XAML se incarca
foreach ($rn in $asm.GetManifestResourceNames()) {
    if ($rn -like '*.xaml') { $w = [Windows.Markup.XamlReader]::Load($asm.GetManifestResourceStream($rn)); Write-Host "WiFiStabil : $rn OK ($($w.Title))" }
}
if (-not ($asm.GetManifestResourceNames() -contains 'raport.html')) { throw 'Lipseste sablonul raport.html din aplicatie' }

# 2. licenta: o cheie Sanatate PC NU trebuie sa activeze Wi-Fi Stabil
$L = $asm.GetType('InfoPcDisk.Licensing')
$L.GetMethod('UseWifiStabil').Invoke($null, @()) | Out-Null
$spcKey = ((Get-Content 'tests\licente_test.txt') | Where-Object { $_ -like 'screenshot=*' }).Substring(11)
$x = $L.GetMethod('Parse').Invoke($null, @($spcKey))
Write-Host "Cheie Sanatate PC in Wi-Fi Stabil: Valid=$($x.Valid) Eroare=$($x.Error)"
if ($x.Valid) { throw 'O cheie Sanatate PC a fost acceptata de Wi-Fi Stabil' }
$x2 = $L.GetMethod('Parse').Invoke($null, @('WFS1.' + $spcKey.Substring(5)))
Write-Host "Cheie Sanatate PC cu prefix schimbat: Valid=$($x2.Valid) Eroare=$($x2.Error)"
if ($x2.Valid) { throw 'Cheia cu prefixul schimbat a fost acceptata' }
Write-Host "Folder date: $($L.GetProperty('AppDataDir').GetValue($null))"

# 3. motorul: 10 secunde de masuratori reale pe runner (fara Wi-Fi: testeaza conexiunea pe cablu)
$wi = $asm.GetType('InfoPcWifi.Wlan').GetMethod('Query').Invoke($null, @())
Write-Host "Placa Wi-Fi gasita: $($wi.Found) conectat: $($wi.Connected)"
$M = $asm.GetType('InfoPcWifi.Monitor')
$m = [Activator]::CreateInstance($M)
$m.MaxMinutes = 0.17
$m.SaveDir = Join-Path $env:RUNNER_TEMP 'wifi_test'
$m.Start()
$t = 0; while (-not $m.Finished -and $t -lt 300) { Start-Sleep -Milliseconds 100; $t++ }
$snap = $m.Snap($true)
Write-Host ("Masuratori: {0}, OK: {1}, disponibilitate {2:0.0}%, intreruperi {3}, placa: {4} ({5}), router {6}" -f $snap.Total, $snap.Ok, $snap.Availability, $snap.Incidents.Count, $m.AdapterName, $m.AdapterKind, $m.Gateway)
foreach ($s in $snap.Samples) { Write-Host ("   t={0} stare={1} router={2} internet={3}" -f $s.T, $s.State, $s.GwMs, $s.NetMs) }
if (-not $m.Finished) { throw 'Testul nu s-a oprit la durata aleasa' }
if ($snap.Total -lt 6) { throw "Prea putine masuratori: $($snap.Total)" }
$csv = Join-Path $m.SaveDir 'masuratori.csv'
if (-not (Test-Path $csv)) { throw 'Lipseste masuratori.csv' }
Write-Host "CSV: $((Get-Content $csv).Count) randuri"

# 4. raport HTML si export CSV
$R = $asm.GetType('InfoPcWifi.Report')
$html = Join-Path (Get-Location).Path 'shots\wifi\raport_runner.html'
$R.GetMethod('SaveHtml').Invoke($null, [object[]]@($html, $m, $snap, 'Test CI', $true)) | Out-Null
$len = (Get-Item $html).Length
Write-Host "Raport HTML: $len octeti"
if ($len -lt 10000 -or (Get-Content $html -Raw) -match '/\*__DATA__\*/') { throw 'Raportul HTML nu a fost completat' }
$R.GetMethod('SaveCsv').Invoke($null, [object[]]@((Join-Path $env:RUNNER_TEMP 'export.csv'), $snap, 1)) | Out-Null

# 5. capturi: date de demonstratie + test real pornit din aplicatie
function Cap($file, $argsx, $size = '1600x1000') {
    $out = Join-Path (Get-Location).Path "shots\wifi\$file"
    $p = Start-Process -FilePath $exe -ArgumentList (@("--captura=$out", "--marime=$size") + $argsx) -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill() }
    if (Test-Path "$out.eroare.txt") { Get-Content "$out.eroare.txt" | Write-Host; throw "Captura $file a dat eroare" }
    if (Test-Path $out) { Write-Host "captura $file" } else { throw "Captura $file lipseste" }
}
Cap 'demo.png' @('--demo', "--raport=$((Join-Path (Get-Location).Path 'shots\wifi\raport_demo.html'))")
Cap 'pornit.png' @('--porneste')
Cap 'gol.png' @()
Cap 'store_1920.png' @('--demo') '1920x1080'

# 6. pachet MSIX pentru Microsoft Store + instalare de test
$ver = '1.0.0.0'
$kit = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } | Where-Object { Test-Path (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
$makeappx = Join-Path $kit 'makeappx.exe'; $signtool = Join-Path $kit 'signtool.exe'
$id = @{}
foreach ($l in Get-Content 'store_wifi\identitate.txt' -Encoding UTF8) { if ($l -match '^(\w+)=(.*)$') { $id[$matches[1]] = $matches[2].Trim() } }
$real = $id['Name'] -and $id['Publisher']
$name = if ($real) { $id['Name'] } else { 'InfoPCElectric.WiFiStabil.Test' }
$pub = if ($real) { $id['Publisher'] } else { 'CN=InfoPcTest' }
$pubName = if ($id['PublisherDisplayName']) { $id['PublisherDisplayName'] } else { 'Info PC Electric S.R.L.' }
Write-Host "Identitate Wi-Fi Stabil: $name / $pub (reala: $real)"
$dir = Join-Path $env:RUNNER_TEMP 'msix_wifi'
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Force "$dir\Assets" | Out-Null
Copy-Item $exe "$dir\WiFiStabil.exe"
Copy-Item 'store_wifi\Assets\*' "$dir\Assets\"
$tpl = Get-Content 'store_wifi\AppxManifest.template.xml' -Raw -Encoding UTF8
$mf = $tpl.Replace('{NAME}', $name).Replace('{PUBLISHER}', $pub).Replace('{PUBLISHERNAME}', $pubName).Replace('{VERSION}', $ver)
[IO.File]::WriteAllText("$dir\AppxManifest.xml", $mf, (New-Object Text.UTF8Encoding $false))
New-Item -ItemType Directory -Force 'dist\store' | Out-Null
$pkgFile = "dist\store\WiFiStabil_$($ver)_store.msix"
& $makeappx pack /d $dir /p $pkgFile /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx a esuat pentru Wi-Fi Stabil' }
Write-Host "Pachet: $pkgFile ($((Get-Item $pkgFile).Length) octeti)"

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $pub -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$pfx = Join-Path $env:RUNNER_TEMP 'wifi_test.pfx'; $pw = ConvertTo-SecureString 'test' -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null
Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $pw | Out-Null
$tst = Join-Path $env:RUNNER_TEMP 'wifi_test.msix'; Copy-Item $pkgFile $tst
& $signtool sign /fd SHA256 /f $pfx /p test $tst | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Semnarea de test a esuat' }
Add-AppxPackage -Path $tst
$pkg = Get-AppxPackage -Name $name
if (-not $pkg) { throw 'Pachetul Wi-Fi Stabil nu s-a instalat' }
Write-Host "Instalat MSIX: $($pkg.PackageFullName)"
$shot = Join-Path (Get-Location).Path 'shots\wifi\msix.png'
Start-Process (Join-Path $pkg.InstallLocation 'WiFiStabil.exe') -ArgumentList "--captura=$shot", '--marime=1600x1000', '--demo'
for ($w = 0; $w -lt 90 -and -not (Test-Path $shot); $w++) { Start-Sleep 1 }
if (-not (Test-Path $shot)) { throw 'Wi-Fi Stabil din MSIX nu a pornit' }
Write-Host 'Wi-Fi Stabil din pachetul MSIX a pornit si a facut captura.'
Start-Sleep 2
Get-Process WiFiStabil -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-AppxPackage -Package $pkg.PackageFullName
Write-Host 'Wi-Fi Stabil: toate testele au trecut.'
