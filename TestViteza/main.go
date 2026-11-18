// Test Viteză Internet — Info PC Electric S.R.L.
// Aplicație Windows: server local pe 127.0.0.1 + interfață într-o fereastră Edge (mod aplicație).
package main

import (
	_ "embed"
	"encoding/json"
	"net"
	"net/http"
	"os"
	"sync/atomic"
	"time"
)

//go:embed web/index.html
var indexHTML []byte

const (
	listenAddr = "127.0.0.1:47123"
	appURL     = "http://127.0.0.1:47123/"
	appVersion = "1.0"
)

var (
	lastSeen atomic.Int64 // ultima dată când fereastra a dat semn de viață
	quitAt   atomic.Int64 // momentul închiderii cerute de fereastră (0 = nu)
)

func main() {
	ln, err := net.Listen("tcp", listenAddr)
	if err != nil {
		// Aplicația rulează deja: doar redeschidem fereastra.
		openWindow(appURL)
		return
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		w.Write(indexHTML)
	})
	mux.HandleFunc("/api/local", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, getLocalInfo())
	})
	mux.HandleFunc("/api/start", func(w http.ResponseWriter, r *http.Request) {
		if !postOK(w, r) {
			return
		}
		var body struct {
			Client string `json:"client"`
			Tech   string `json:"tech"`
		}
		json.NewDecoder(r.Body).Decode(&body)
		writeJSON(w, map[string]bool{"started": startTest(body.Client, body.Tech)})
	})
	mux.HandleFunc("/api/stop", func(w http.ResponseWriter, r *http.Request) {
		if !postOK(w, r) {
			return
		}
		stopTest()
		writeJSON(w, map[string]bool{"ok": true})
	})
	mux.HandleFunc("/api/status", func(w http.ResponseWriter, r *http.Request) {
		touch()
		writeJSON(w, getState())
	})
	mux.HandleFunc("/api/history", func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost {
			if !postOK(w, r) {
				return
			}
			clearHistory()
		}
		writeJSON(w, loadHistory())
	})
	mux.HandleFunc("/api/ping", func(w http.ResponseWriter, r *http.Request) {
		touch()
		writeJSON(w, map[string]string{"version": appVersion})
	})
	mux.HandleFunc("/api/quit", func(w http.ResponseWriter, r *http.Request) {
		if !postOK(w, r) {
			return
		}
		quitAt.Store(time.Now().Add(4 * time.Second).UnixMilli())
		w.WriteHeader(http.StatusNoContent)
	})

	touch()
	go watchdog()
	if os.Getenv("TESTVITEZA_NOWINDOW") == "" { // variabila e folosită doar de testul automat
		go func() {
			time.Sleep(250 * time.Millisecond)
			openWindow(appURL)
		}()
	}

	srv := &http.Server{Handler: hostGuard(mux), ReadHeaderTimeout: 10 * time.Second}
	srv.Serve(ln)
}

// touch marchează fereastra ca deschisă și anulează o închidere în curs (ex. la reîncărcare).
func touch() {
	lastSeen.Store(time.Now().UnixMilli())
	quitAt.Store(0)
}

// watchdog oprește aplicația când fereastra a fost închisă.
func watchdog() {
	for range time.Tick(time.Second) {
		now := time.Now().UnixMilli()
		if q := quitAt.Load(); q != 0 && now > q && !testRunning() {
			os.Exit(0)
		}
		// Plasă de siguranță: fără niciun semn de la fereastră timp de 3 minute.
		if now-lastSeen.Load() > 180_000 && !testRunning() {
			os.Exit(0)
		}
	}
}

// hostGuard acceptă doar cereri adresate serverului local (protecție DNS rebinding).
func hostGuard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != listenAddr && r.Host != "localhost:47123" {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// postOK cere POST + antet propriu, pe care alte site-uri nu îl pot trimite fără permisiune (CSRF).
func postOK(w http.ResponseWriter, r *http.Request) bool {
	if r.Method != http.MethodPost || r.Header.Get("X-App") != "1" {
		http.Error(w, "forbidden", http.StatusForbidden)
		return false
	}
	touch()
	return true
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(v)
}
