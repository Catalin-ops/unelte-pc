# Server de monitorizare — Sănătate PC

Primește rapoartele calculatoarelor monitorizate, le organizează pe instituții, afișează panoul web și trimite raportul zilnic și alertele pe e-mail. Rulează doar cu Python 3 (fără pachete pip), în spatele lui Caddy (HTTPS automat).

## Instalare / actualizare (Ubuntu, ca root)

    curl -fsSL https://raw.githubusercontent.com/Catalin-ops/unelte-pc/main/server/install.sh | bash

Cu domeniul tău (înregistrare DNS de tip A către IP-ul serverului):

    curl -fsSL https://raw.githubusercontent.com/Catalin-ops/unelte-pc/main/server/install.sh | bash -s -- monitor.domeniu.ro

Fără domeniu, panoul folosește adresa gratuită `IP-cu-liniute.sslip.io`.

## Administrare

    sanatate-monitor status        # serviciile și timer-ele
    sanatate-monitor jurnal        # ultimele mesaje
    sanatate-monitor test-email    # e-mail de test
    sanatate-monitor smtp          # schimbă datele de e-mail
    sanatate-monitor parola        # schimbă parola panoului
    sanatate-monitor actualizare   # ultima versiune din GitHub

Date: `/var/lib/sanatate-monitor/monitor.db` · backup zilnic în `/var/backups/sanatate-monitor` (14 zile) · setări: `/etc/sanatate-monitor/config.ini`.
