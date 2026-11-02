# Construieste RecuperareFisiere.exe si SanatatePC.exe cu compilatorul C# inclus in Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Management.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Xml.dll"
) | ForEach-Object { "/r:$_" }
New-Item -ItemType Directory -Force dist | Out-Null

function Build($name, $folder, $icon) {
    Write-Host "== $name"
    $src = Get-ChildItem "$folder\*.cs" | ForEach-Object { $_.FullName }
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu `
        "/out:dist\$name.exe" "/win32manifest:assets\app.manifest" "/win32icon:assets\$icon" `
        "/resource:$folder\MainWindow.xaml,MainWindow.xaml" @refs @src
    if ($LASTEXITCODE -ne 0) { throw "Compilarea $name a esuat" }
}

Build 'RecuperareFisiere' 'RecuperareFisiere' 'recuperare.ico'
Build 'SanatatePC' 'SanatatePC' 'sanatate.ico'
Get-ChildItem dist
