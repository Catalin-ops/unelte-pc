# Construieste pachetele MSIX pentru Microsoft Store si testeaza instalarea + pornirea variantei fara administrator.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$ver = '1.4.0.0'
$kit = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } | Where-Object { Test-Path (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
if (-not $kit) { throw 'Nu gasesc makeappx.exe (Windows SDK)' }
$makeappx = Join-Path $kit 'makeappx.exe'; $signtool = Join-Path $kit 'signtool.exe'

# identitatea din Partner Center (store\identitate.txt); goala = identitate de test
$id = @{}
foreach ($l in Get-Content 'store\identitate.txt' -Encoding UTF8) { if ($l -match '^(\w+)=(.*)$') { $id[$matches[1]] = $matches[2].Trim() } }
$real = $id['Name'] -and $id['Publisher']
$name = if ($real) { $id['Name'] } else { 'InfoPCElectric.SanatatePC.Test' }
$pub = if ($real) { $id['Publisher'] } else { 'CN=InfoPcTest' }
$pubName = if ($id['PublisherDisplayName']) { $id['PublisherDisplayName'] } else { 'Info PC Electric S.R.L.' }
Write-Host "Identitate: $name / $pub / $pubName (reala: $real)"

New-Item -ItemType Directory -Force 'dist\store' | Out-Null
$tpl = Get-Content 'store\AppxManifest.template.xml' -Raw -Encoding UTF8
function Pack($flavor, $exe, $extra) {
    $dir = Join-Path $env:RUNNER_TEMP "msix_$flavor"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force "$dir\Assets" | Out-Null
    Copy-Item "dist\$exe" "$dir\SanatatePC.exe"
    Copy-Item 'store\Assets\*' "$dir\Assets\"
    $m = $tpl.Replace('{NAME}', $name).Replace('{PUBLISHER}', $pub).Replace('{PUBLISHERNAME}', $pubName).Replace('{VERSION}', $ver).Replace('{EXTRA}', $extra)
    [IO.File]::WriteAllText("$dir\AppxManifest.xml", $m, (New-Object Text.UTF8Encoding $false))
    $out = "dist\store\SanatatePC_$($ver)_$flavor.msix"
    & $makeappx pack /d $dir /p $out /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "makeappx a esuat pentru $flavor" }
    Write-Host "Pachet: $out ($((Get-Item $out).Length) octeti)"
    return (Resolve-Path $out).Path
}
$pAdmin = Pack 'cu_administrator' 'SanatatePC.exe' "`n    <rescap:Capability Name=`"allowElevation`"/>"
$pStd = Pack 'fara_administrator' 'SanatatePC_Store.exe' ''

# test: semnam o COPIE cu un certificat de test (acelasi Publisher), o instalam si pornim aplicatia; pachetele pentru Store raman nesemnate
if ($true) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $pub -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $pfx = Join-Path $env:RUNNER_TEMP 'test.pfx'; $pw = ConvertTo-SecureString 'test' -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null
    Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $pw | Out-Null
    $tst = Join-Path $env:RUNNER_TEMP 'test.msix'; Copy-Item $pStd $tst
    & $signtool sign /fd SHA256 /f $pfx /p test $tst | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Semnarea de test a esuat' }
    Add-AppxPackage -Path $tst
    $pkg = Get-AppxPackage -Name $name
    if (-not $pkg) { throw 'Pachetul nu s-a instalat' }
    Write-Host "Instalat MSIX: $($pkg.PackageFullName) in $($pkg.InstallLocation)"
    New-Item -ItemType Directory -Force 'shots\store' | Out-Null
    $shot = Join-Path (Get-Location).Path 'shots\store\msix_sumar.png'
    # pornire prin sistemul de aplicatii (ca din meniul Start); captura o face aplicatia singura
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!SanatatePC" -ArgumentList "--captura=$shot", '--marime=1600x1000'
    for ($w = 0; $w -lt 90 -and -not (Test-Path $shot); $w++) { Start-Sleep 1 }
    if (-not (Test-Path $shot)) {
        # unele versiuni de Windows nu transmit argumentele prin shell: pornim direct executabilul din pachet
        Start-Process (Join-Path $pkg.InstallLocation 'SanatatePC.exe') -ArgumentList "--captura=$shot", '--marime=1600x1000'
        for ($w = 0; $w -lt 90 -and -not (Test-Path $shot); $w++) { Start-Sleep 1 }
    }
    if (Test-Path $shot) { Write-Host 'Aplicatia din pachetul MSIX a pornit si a facut captura.' } else { throw 'Aplicatia din MSIX nu a pornit' }
    Start-Sleep 3
    Get-Process SanatatePC -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host 'Dezinstalare MSIX: OK'
}
