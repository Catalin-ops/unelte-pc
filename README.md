# Unelte PC — Info PC Electric

Două aplicații Windows (.exe), fără instalare:

- **Recuperare Fișiere** — recuperează PDF, Word, Excel, PowerPoint și poze JPG șterse sau pierdute (căutare rapidă NTFS cu nume originale + căutare profundă sector cu sector). Discul sursă e deschis doar pentru citire; împiedică intrarea PC-ului în repaus în timpul scanării.
- **Sănătate PC** — procesor, placă de bază, RAM, placă video, discuri (SMART HDD/SSD/NVMe), monitoare, baterie, rețea, Windows și stabilitate, fiecare cu model și stare; raport text pentru client.

Executabilele se construiesc automat de GitHub Actions la fiecare push (tab-ul **Actions** → ultima rulare → **unelte-pc**). Local: `powershell -ExecutionPolicy Bypass -File build.ps1`.

## Scanare Rețea (Windows + Android)

- **ScanareRetea.exe** (Windows) și **ScanareRetea.apk** (Android): sus apare IP-ul tău (plus gateway, mască, DNS, IP public), dedesubt toate aparatele din rețea cu tipul ghicit — router, casă de marcat, imprimantă, cameră/NVR, calculator, telefon, TV, IoT — după producătorul plăcii de rețea (MAC), porturile deschise, numele NetBIOS/DNS și titlul paginii web.
- Se construiesc separat de workflow-ul `scanare.yml` și se publică în Release-urile `scanare-retea-v1.0.N`. Local: `powershell -ExecutionPolicy Bypass -File build-scanare.ps1`.
