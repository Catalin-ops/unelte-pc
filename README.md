# Unelte PC — Info PC Electric

Două aplicații Windows (.exe), fără instalare:

- **Recuperare Fișiere** — recuperează PDF, Word, Excel, PowerPoint și poze JPG șterse sau pierdute (căutare rapidă NTFS cu nume originale + căutare profundă sector cu sector). Discul sursă e deschis doar pentru citire; împiedică intrarea PC-ului în repaus în timpul scanării.
- **Sănătate PC** — procesor, placă de bază, RAM, placă video, discuri (SMART HDD/SSD/NVMe), monitoare, baterie, rețea, Windows și stabilitate, fiecare cu model și stare; raport text pentru client. În Pro: raport PDF cu logo și **Raport 3D** — o pagină .html interactivă (model 3D desktop sau laptop, starea fiecărei componente, ce trebuie făcut, istoricul verificărilor), care merge fără internet și se trimite clientului pe WhatsApp sau e-mail direct din aplicație.

Executabilele se construiesc automat de GitHub Actions la fiecare push (tab-ul **Actions** → ultima rulare → **unelte-pc**). Local: `powershell -ExecutionPolicy Bypass -File build.ps1`.
