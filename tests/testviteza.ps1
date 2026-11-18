# Test automat Test Viteza Internet pe Windows real (GitHub Actions):
# porneste aplicatia fara fereastra, citeste reteaua locala si ruleaza un test complet de viteza.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$exe = Resolve-Path 'dist\TestVitezaInternet.exe'
$base = 'http://127.0.0.1:47123'
$hdr = @{ 'X-App' = '1' }

$env:TESTVITEZA_NOWINDOW = '1'
$p = Start-Process $exe -PassThru
try {
    $ok = $false
    for ($i = 0; $i -lt 40 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 250
        try { Invoke-RestMethod "$base/api/ping" | Out-Null; $ok = $true } catch {}
    }
    if (-not $ok) { throw 'Serverul aplicatiei nu a pornit' }

    $page = Invoke-WebRequest "$base/" -UseBasicParsing
    if ($page.Content -notmatch 'Test Vitez') { throw 'Pagina principala nu se incarca' }

    # protectie: cererile fara antetul aplicatiei sunt refuzate
    try { Invoke-WebRequest "$base/api/start" -Method Post -UseBasicParsing | Out-Null; throw 'Cererea fara antet a fost acceptata' }
    catch { if ($_.Exception.Message -eq 'Cererea fara antet a fost acceptata') { throw } }

    $li = Invoke-RestMethod "$base/api/local"
    Write-Host "Retea locala: tip=$($li.connType) gateway=$($li.gateway) dns=$($li.dns -join ',') ipv6=$($li.ipv6) host=$($li.hostname)"
    foreach ($a in $li.adapters) { Write-Host ("  {0} | {1} | {2} | {3} Mbps | MTU {4} | MAC {5} | principal={6}" -f $a.name, $a.type, ($a.ips -join ','), $a.linkMbps, $a.mtu, $a.mac, $a.primary) }
    if (-not $li.primary) { throw 'Adaptorul principal nu a fost gasit' }
    if (-not $li.gateway) { throw 'Gateway-ul nu a fost citit' }
    if (-not $li.dns -or $li.dns.Count -eq 0) { throw 'Serverele DNS nu au fost citite' }
    Write-Host "Wi-Fi: $(if ($li.wifi) { $li.wifi | ConvertTo-Json -Compress } else { 'nu (normal pe server)' })"

    Invoke-RestMethod "$base/api/start" -Method Post -Headers $hdr -ContentType 'application/json' -Body '{"client":"Test CI","tech":"GitHub"}' | Out-Null
    $s = $null
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Seconds 1
        $s = Invoke-RestMethod "$base/api/status"
        if (-not $s.running) { break }
        if ($i % 5 -eq 0) { Write-Host ("  {0} {1:P0} {2} Mbps" -f $s.phase, $s.progress, $s.current) }
    }
    $r = $s.result
    Write-Host "Faza finala: $($s.phase) - $($s.label) $($s.error) $($s.warning)"
    Write-Host "Provider: $($r.provider.isp) $($r.provider.asn) IP=$($r.provider.ip) $($r.provider.city) $($r.provider.country) server=$($r.provider.colo) ($($r.provider.source))"
    Write-Host ("Download {0} Mbps (varf {1}, {2} MB) | Upload {3} Mbps (varf {4}, {5} MB)" -f $r.downMbps, $r.downPeak, $r.downMB, $r.upMbps, $r.upPeak, $r.upMB)
    Write-Host ("Ping {0} ms ({1}) jitter {2} pierderi {3}% | router {4} ms pierderi {5}% | sub incarcare {6}/{7} ms" -f $r.pingMs, $r.pingMethod, $r.jitterMs, $r.lossPct, $r.gwPingMs, $r.gwLossPct, $r.downLatency, $r.upLatency)

    if ($s.phase -ne 'done') {
        # Serverele GitHub pot fi limitate de Cloudflare; nu blocam build-ul, dar semnalam.
        Write-Warning "Testul de viteza nu s-a finalizat in CI: $($s.error)"
    } else {
        if ($r.downMbps -le 0) { throw 'Download-ul nu a fost masurat' }
        if (-not $r.provider.isp) { throw 'Providerul nu a fost identificat' }
        $h = Invoke-RestMethod "$base/api/history"
        if (@($h).Count -lt 1) { throw 'Rezultatul nu a fost salvat in istoric' }
        Write-Host "Istoric salvat: $(@($h).Count) test(e)"
    }
}
finally {
    if (-not $p.HasExited) { Stop-Process $p -Force }
    Remove-Item (Join-Path (Split-Path $exe) 'TestViteza-istoric.json*') -ErrorAction SilentlyContinue
}
Write-Host 'Test Viteza Internet: OK'
