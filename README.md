# Unelte PC — Info PC Electric

Aplicații Windows (.exe), fără instalare:

- **Recuperare Fișiere** — recuperează PDF, Word, Excel, PowerPoint și poze JPG șterse sau pierdute (căutare rapidă NTFS cu nume originale + căutare profundă sector cu sector). Discul sursă e deschis doar pentru citire; împiedică intrarea PC-ului în repaus în timpul scanării.
- **Sănătate PC** — procesor, placă de bază, RAM, placă video, discuri (SMART HDD/SSD/NVMe), monitoare, baterie, rețea, Windows și stabilitate, fiecare cu model și stare; raport text pentru client.
- **Test Viteză Internet** — identifică providerul (ISP, ASN, IP public, locație), măsoară download, upload, ping, jitter și pierderi de pachete (spre internet și până la router, plus ping sub încărcare) și arată toate datele rețelei locale și Wi-Fi (semnal, canal, bandă, standard, securitate). Diagnostic automat, istoric și raport A4/PDF cu antet. Scris în Go (dosarul `TestViteza`), fără drepturi de administrator.

Executabilele se construiesc automat de GitHub Actions la fiecare push (tab-ul **Actions** → ultima rulare → **unelte-pc**). Local: `powershell -ExecutionPolicy Bypass -File build.ps1`.
