# Construieste RecuperareFisiere.exe, SanatatePC.exe si GeneratorLicente.exe cu compilatorul C# inclus in Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$wpf\ReachFramework.dll",
    "$fw\System.Xaml.dll", "$fw\System.Management.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Xml.dll"
) | ForEach-Object { "/r:$_" }
New-Item -ItemType Directory -Force dist | Out-Null

# three.js (licenta MIT) pentru modelul 3D din raportul pentru client; se descarca o singura data
$three = 'assets\three.min.js'
if (-not (Test-Path $three)) {
    Write-Host '== descarc three.js 0.147.0'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    try { Invoke-WebRequest 'https://cdn.jsdelivr.net/npm/three@0.147.0/build/three.min.js' -OutFile $three -UseBasicParsing }
    catch { Write-Warning "Nu am putut descarca three.js: raportul 3D va avea doar panourile cu date. $_" }
}
if ((Test-Path $three) -and (Get-Item $three).Length -lt 400000) { Remove-Item $three; Write-Warning 'three.js descarcat incomplet' }

function Build($name, $folder, $icon, $manifest, $extra) {
    Write-Host "== $name"
    $src = @(Get-ChildItem "$folder\*.cs" | ForEach-Object { $_.FullName })
    if ($extra) { $src += @($extra) }
    $res = @(Get-ChildItem "$folder\*.xaml", "$folder\*.html" -ErrorAction SilentlyContinue | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" })
    if ($folder -eq 'SanatatePC' -and (Test-Path $three)) { $res += "/resource:$((Resolve-Path $three).Path),three.min.js" }
    & $csc /nologo /target:winexe /optimize+ /unsafe /codepage:65001 /platform:anycpu `
        "/out:dist\$name.exe" "/win32manifest:assets\$manifest" "/win32icon:assets\$icon" `
        @res @refs @src
    if ($LASTEXITCODE -ne 0) { throw "Compilarea $name a esuat" }
}

Build 'RecuperareFisiere' 'RecuperareFisiere' 'recuperare.ico' 'app.manifest' @()
Build 'SanatatePC' 'SanatatePC' 'sanatate.ico' 'app.manifest' @()
# varianta pentru Microsoft Store (MSIX): porneste fara cerere de administrator
Build 'SanatatePC_Store' 'SanatatePC' 'sanatate.ico' 'app_user.manifest' @()
Build 'GeneratorLicente' 'GeneratorLicente' 'sanatate.ico' 'app_user.manifest' @((Resolve-Path 'SanatatePC\Licenta.cs').Path)
Get-ChildItem dist
