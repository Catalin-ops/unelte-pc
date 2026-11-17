# Unelte PC — Info PC Electric

Aplicații Windows (.exe), fără instalare:

- **Recuperare Fișiere** — recuperează PDF, Word, Excel, PowerPoint și poze JPG șterse sau pierdute (căutare rapidă NTFS cu nume originale + căutare profundă sector cu sector). Discul sursă e deschis doar pentru citire; împiedică intrarea PC-ului în repaus în timpul scanării.
- **Sănătate PC** — procesor, placă de bază, RAM, placă video, discuri (SMART HDD/SSD/NVMe), monitoare, baterie, rețea, Windows și stabilitate, fiecare cu model și stare; raport text pentru client.
- **Wi-Fi Stabil** — lași calculatorul pornit și vezi dacă rețeaua cade: fiecare întrerupere cu ora, durata și cauza (Wi-Fi, router sau internet), semnal, latență, grafic și raport HTML pentru client. Free: teste de până la 1 oră; Pro: teste nelimitate, raport și export Excel. Pachet MSIX pentru Microsoft Store în `store_wifi`.

Executabilele se construiesc automat de GitHub Actions la fiecare push (tab-ul **Actions** → ultima rulare → **unelte-pc**). Local: `powershell -ExecutionPolicy Bypass -File build.ps1`.
