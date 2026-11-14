# Construieste ScanareRetea.exe cu compilatorul C# inclus in Windows (.NET Framework 4.x).
# Daca exista ScanareRetea\manuf.txt (lista producatorilor dupa MAC), e inclusa in exe.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Xml.dll"
) | ForEach-Object { "/r:$_" }
New-Item -ItemType Directory -Force dist | Out-Null

$src = @(Get-ChildItem "ScanareRetea\*.cs" | ForEach-Object { $_.FullName })
$res = @("/resource:assets\retea.ico,retea.ico")
if (Test-Path 'ScanareRetea\manuf.txt') {
    $res += "/resource:ScanareRetea\manuf.txt,manuf.txt"
    Write-Host "Lista producatori inclusa ($((Get-Item 'ScanareRetea\manuf.txt').Length) octeti)"
} else { Write-Host "ATENTIE: manuf.txt lipseste - se foloseste doar lista scurta interna" }

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu `
    "/out:dist\ScanareRetea.exe" "/win32manifest:assets\app_user.manifest" "/win32icon:assets\retea.ico" `
    @res @refs @src
if ($LASTEXITCODE -ne 0) { throw "Compilarea ScanareRetea a esuat" }
Get-Item dist\ScanareRetea.exe
