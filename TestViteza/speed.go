package main

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

var cfBase = envOr("TESTVITEZA_BASE", "https://speed.cloudflare.com") // variabila e doar pentru teste

// ---------- Starea testului (citită de interfață prin /api/status) ----------

type Sample struct {
	T float64 `json:"t"` // secunde de la începutul fazei
	V float64 `json:"v"` // Mbps
}

type Provider struct {
	ISP      string `json:"isp"`
	ASN      string `json:"asn"`
	IP       string `json:"ip"`
	City     string `json:"city"`
	Region   string `json:"region"`
	Country  string `json:"country"`
	Colo     string `json:"colo"`
	ColoName string `json:"coloName"`
	Source   string `json:"source"`
}

type Result struct {
	ID          string     `json:"id"`
	Time        string     `json:"time"`
	Client      string     `json:"client"`
	Tech        string     `json:"tech"`
	Provider    Provider   `json:"provider"`
	PingMs      float64    `json:"pingMs"`
	JitterMs    float64    `json:"jitterMs"`
	LossPct     float64    `json:"lossPct"` // -1 = nu s-a putut măsura
	PingMethod  string     `json:"pingMethod"`
	GwPingMs    float64    `json:"gwPingMs"` // -1 = nu s-a putut măsura
	GwJitterMs  float64    `json:"gwJitterMs"`
	GwLossPct   float64    `json:"gwLossPct"`
	DownMbps    float64    `json:"downMbps"`
	UpMbps      float64    `json:"upMbps"`
	DownPeak    float64    `json:"downPeak"`
	UpPeak      float64    `json:"upPeak"`
	DownMB      float64    `json:"downMB"`
	UpMB        float64    `json:"upMB"`
	DownLatency float64    `json:"downLatency"` // ping în timpul download-ului
	UpLatency   float64    `json:"upLatency"`
	Local       *LocalInfo `json:"local"`
}

type State struct {
	Running  bool     `json:"running"`
	Phase    string   `json:"phase"` // idle, info, ping, download, upload, done, error, stopped
	Label    string   `json:"label"`
	Progress float64  `json:"progress"` // 0..1
	Current  float64  `json:"current"`  // Mbps instantaneu
	Down     []Sample `json:"down"`
	Up       []Sample `json:"up"`
	Result   *Result  `json:"result"`
	Error    string   `json:"error"`
	Warning  string   `json:"warning"`
}

var (
	stMu   sync.Mutex
	st     = State{Phase: "idle"}
	cancel context.CancelFunc
)

func getState() State {
	stMu.Lock()
	defer stMu.Unlock()
	s := st
	s.Down = append([]Sample(nil), st.Down...)
	s.Up = append([]Sample(nil), st.Up...)
	return s
}

func update(f func(*State)) {
	stMu.Lock()
	f(&st)
	stMu.Unlock()
}

func testRunning() bool {
	stMu.Lock()
	defer stMu.Unlock()
	return st.Running
}

func startTest(client, tech string) bool {
	stMu.Lock()
	if st.Running {
		stMu.Unlock()
		return false
	}
	ctx, c := context.WithCancel(context.Background())
	cancel = c
	st = State{Running: true, Phase: "info", Label: "Identific providerul…", Result: &Result{Client: client, Tech: tech}}
	stMu.Unlock()
	go runTest(ctx, client, tech)
	return true
}

func stopTest() {
	stMu.Lock()
	if cancel != nil {
		cancel()
	}
	stMu.Unlock()
}

// ---------- Client HTTP (HTTP/1.1, conexiuni separate pe fiecare flux) ----------

var httpClient = &http.Client{
	Transport: &http.Transport{
		Proxy:               http.ProxyFromEnvironment,
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSHandshakeTimeout: 10 * time.Second,
		TLSNextProto:        map[string]func(string, *tls.Conn) http.RoundTripper{}, // fără HTTP/2
		MaxIdleConnsPerHost: 32,
		DisableCompression:  true,
		WriteBufferSize:     256 << 10,
		ReadBufferSize:      256 << 10,
	},
}

func newReq(ctx context.Context, method, url string, body io.Reader) (*http.Request, error) {
	req, err := http.NewRequestWithContext(ctx, method, url, body)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "InfoPC-TestViteza/"+appVersion)
	req.Header.Set("Referer", cfBase+"/")
	return req, nil
}

// ---------- Desfășurarea testului ----------

func runTest(ctx context.Context, client, tech string) {
	res := &Result{
		ID:     fmt.Sprintf("%d", time.Now().UnixNano()),
		Time:   time.Now().Format("2006-01-02 15:04:05"),
		Client: client, Tech: tech,
		LossPct: -1, GwPingMs: -1, GwLossPct: -1, DownLatency: -1, UpLatency: -1,
	}
	finish := func(phase, label, errMsg string) {
		update(func(s *State) {
			s.Running = false
			s.Phase, s.Label, s.Error = phase, label, errMsg
			s.Current = 0
			if phase == "done" {
				s.Progress = 1
			}
			s.Result = res
		})
	}
	defer func() {
		if r := recover(); r != nil {
			finish("error", "Eroare", fmt.Sprint(r))
		}
	}()

	// 1. Rețeaua locală + providerul
	res.Local = getLocalInfo()
	p, err := fetchProvider(ctx)
	if err != nil {
		if ctx.Err() != nil {
			finish("stopped", "Test oprit", "")
			return
		}
		finish("error", "Fără conexiune la internet", "Nu am putut contacta serverul de test. Verificați conexiunea la internet. ("+err.Error()+")")
		return
	}
	res.Provider = p
	update(func(s *State) {
		s.Result = cloneResult(res)
		s.Progress = 0.05
		s.Phase = "ping"
		s.Label = "Măsor ping, jitter și pierderi…"
	})

	// 2. Ping la internet (1.1.1.1) și la router, în paralel
	var wg sync.WaitGroup
	var inet, gw pingStats
	wg.Add(1)
	go func() { defer wg.Done(); inet = pingSeries(ctx, net.IPv4(1, 1, 1, 1), 20, 150*time.Millisecond) }()
	if res.Local != nil && res.Local.Gateway != "" {
		if ip := net.ParseIP(res.Local.Gateway); ip != nil && ip.To4() != nil {
			wg.Add(1)
			go func() { defer wg.Done(); gw = pingSeries(ctx, ip, 20, 150*time.Millisecond) }()
		}
	}
	httpPing, httpJitter := httpLatency(ctx, 12)
	wg.Wait()
	if ctx.Err() != nil {
		finish("stopped", "Test oprit", "")
		return
	}
	if inet.received > 0 {
		res.PingMs, res.JitterMs, res.LossPct, res.PingMethod = inet.avg, inet.jitter, inet.loss, "ICMP 1.1.1.1"
	} else if httpPing > 0 {
		res.PingMs, res.JitterMs, res.PingMethod = httpPing, httpJitter, "HTTP (ICMP blocat)"
	}
	if gw.sent > 0 && gw.received > 0 {
		res.GwPingMs, res.GwJitterMs, res.GwLossPct = gw.avg, gw.jitter, gw.loss
	} else if gw.sent > 0 {
		res.GwLossPct = 100 // routerul nu răspunde la ping (unele routere îl blochează)
	}
	update(func(s *State) {
		s.Result = cloneResult(res)
		s.Progress = 0.15
		s.Phase = "download"
		s.Label = "Măsor viteza de download…"
	})

	// 3. Download
	d := runTransfer(ctx, false, 6, 12*time.Second, func(sm Sample, frac float64) {
		update(func(s *State) {
			s.Down = append(s.Down, sm)
			s.Current = sm.V
			s.Progress = 0.15 + 0.45*frac
		})
	})
	if ctx.Err() != nil {
		finish("stopped", "Test oprit", "")
		return
	}
	res.DownMbps, res.DownPeak, res.DownMB, res.DownLatency = d.mbps, d.peak, d.mb, d.loadedPing
	update(func(s *State) {
		s.Result = cloneResult(res)
		s.Phase, s.Label, s.Current = "upload", "Măsor viteza de upload…", 0
		if d.err != "" {
			s.Warning = "Download: " + d.err
		}
	})

	// 4. Upload
	u := runTransfer(ctx, true, 4, 12*time.Second, func(sm Sample, frac float64) {
		update(func(s *State) {
			s.Up = append(s.Up, sm)
			s.Current = sm.V
			s.Progress = 0.60 + 0.40*frac
		})
	})
	if ctx.Err() != nil {
		finish("stopped", "Test oprit", "")
		return
	}
	res.UpMbps, res.UpPeak, res.UpMB, res.UpLatency = u.mbps, u.peak, u.mb, u.loadedPing
	if u.err != "" {
		update(func(s *State) { s.Warning = strings.TrimPrefix(s.Warning+" · Upload: "+u.err, " · ") })
	}

	if res.DownMbps == 0 && res.UpMbps == 0 {
		finish("error", "Test eșuat", "Serverul de test nu a permis transferul de date. Încercați din nou peste un minut.")
		return
	}
	saveHistory(*res)
	finish("done", "Test finalizat", "")
}

func cloneResult(r *Result) *Result { c := *r; return &c }

// ---------- Provider (ISP) ----------

var coloNames = map[string]string{
	"OTP": "București", "BUD": "Budapesta", "SOF": "Sofia", "KIV": "Chișinău", "VIE": "Viena",
	"FRA": "Frankfurt", "WAW": "Varșovia", "PRG": "Praga", "AMS": "Amsterdam", "LHR": "Londra",
	"CDG": "Paris", "MXP": "Milano", "BEG": "Belgrad", "IST": "Istanbul", "ATH": "Atena",
	"ZAG": "Zagreb", "MUC": "München", "DUS": "Düsseldorf", "HAM": "Hamburg", "BTS": "Bratislava",
	"KBP": "Kiev", "SKG": "Salonic", "ZRH": "Zürich", "MAD": "Madrid", "ARN": "Stockholm",
}

func fetchProvider(ctx context.Context) (Provider, error) {
	var p Provider
	getJSON := func(url string, v any) error {
		c, cc := context.WithTimeout(ctx, 8*time.Second)
		defer cc()
		req, err := newReq(c, "GET", url, nil)
		if err != nil {
			return err
		}
		resp, err := httpClient.Do(req)
		if err != nil {
			return err
		}
		defer resp.Body.Close()
		if resp.StatusCode != 200 {
			return fmt.Errorf("HTTP %d", resp.StatusCode)
		}
		return json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(v)
	}

	// Serverul Cloudflare ales (colo) — confirmă și că internetul merge.
	firstErr := error(nil)
	func() {
		c, cc := context.WithTimeout(ctx, 8*time.Second)
		defer cc()
		req, _ := newReq(c, "GET", cfBase+"/cdn-cgi/trace", nil)
		if resp, err := httpClient.Do(req); err == nil {
			b, _ := io.ReadAll(io.LimitReader(resp.Body, 64<<10))
			resp.Body.Close()
			for _, line := range strings.Split(string(b), "\n") {
				k, v, _ := strings.Cut(strings.TrimSpace(line), "=")
				switch k {
				case "colo":
					p.Colo = v
				case "ip":
					p.IP = v
				}
			}
		} else {
			firstErr = err
		}
	}()

	var m map[string]any
	if err := getJSON(cfBase+"/meta", &m); err == nil {
		p.ISP = str(m["asOrganization"])
		if a := str(m["asn"]); a != "" {
			p.ASN = "AS" + a
		}
		if v := str(m["clientIp"]); v != "" {
			p.IP = v
		}
		p.City, p.Region, p.Country = str(m["city"]), str(m["region"]), str(m["country"])
		if v := str(m["colo"]); v != "" && p.Colo == "" {
			p.Colo = v
		}
		p.Source = "Cloudflare"
	}
	if p.ISP == "" { // rezervă: ipinfo.io
		var m2 map[string]any
		if err := getJSON("https://ipinfo.io/json", &m2); err == nil {
			org := str(m2["org"]) // ex. "AS8708 RCS & RDS S.A."
			if asn, name, ok := strings.Cut(org, " "); ok && strings.HasPrefix(asn, "AS") {
				p.ASN, p.ISP = asn, name
			} else {
				p.ISP = org
			}
			if p.IP == "" {
				p.IP = str(m2["ip"])
			}
			if p.City == "" {
				p.City, p.Region, p.Country = str(m2["city"]), str(m2["region"]), str(m2["country"])
			}
			p.Source = "ipinfo.io"
		}
	}
	if p.ISP == "" && p.IP == "" {
		if firstErr == nil {
			firstErr = fmt.Errorf("fără răspuns")
		}
		return p, firstErr
	}
	if p.ASN == "AS8708" && !strings.Contains(strings.ToLower(p.ISP), "digi") {
		p.ISP = "Digi (" + p.ISP + ")"
	}
	p.ColoName = coloNames[p.Colo]
	return p, nil
}

func str(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case float64:
		return fmt.Sprintf("%.0f", x)
	case nil:
		return ""
	}
	return fmt.Sprint(v)
}

// ---------- Ping ----------

type pingStats struct {
	sent, received         int
	avg, jitter, loss, min float64
}

func pingSeries(ctx context.Context, ip net.IP, count int, interval time.Duration) pingStats {
	var s pingStats
	var rtts []float64
	for i := 0; i < count && ctx.Err() == nil; i++ {
		s.sent++
		if rtt, ok := icmpPing(ip, time.Second); ok {
			rtts = append(rtts, rtt)
		}
		select {
		case <-ctx.Done():
		case <-time.After(interval):
		}
	}
	return summarize(s.sent, rtts)
}

func summarize(sent int, rtts []float64) pingStats {
	s := pingStats{sent: sent, received: len(rtts)}
	if sent > 0 {
		s.loss = 100 * float64(sent-len(rtts)) / float64(sent)
	}
	if len(rtts) == 0 {
		return s
	}
	sum, mn := 0.0, math.MaxFloat64
	for i, r := range rtts {
		sum += r
		mn = math.Min(mn, r)
		if i > 0 {
			s.jitter += math.Abs(r - rtts[i-1])
		}
	}
	s.avg, s.min = sum/float64(len(rtts)), mn
	if len(rtts) > 1 {
		s.jitter /= float64(len(rtts) - 1)
	}
	return s
}

// httpLatency măsoară timpul de răspuns HTTP pe o conexiune deja deschisă (rezervă când ICMP e blocat).
func httpLatency(ctx context.Context, n int) (avg, jitter float64) {
	var rtts []float64
	for i := 0; i <= n && ctx.Err() == nil; i++ {
		c, cc := context.WithTimeout(ctx, 5*time.Second)
		req, _ := newReq(c, "GET", cfBase+"/__down?bytes=0", nil)
		t0 := time.Now()
		resp, err := httpClient.Do(req)
		if err == nil {
			io.Copy(io.Discard, resp.Body)
			resp.Body.Close()
			ms := float64(time.Since(t0).Microseconds()) / 1000
			// Scădem timpul de procesare raportat de server, dacă există.
			if st := resp.Header.Get("Server-Timing"); st != "" {
				if _, after, ok := strings.Cut(st, "dur="); ok {
					var dur float64
					if _, e := fmt.Sscanf(after, "%f", &dur); e == nil && dur < ms {
						ms -= dur
					}
				}
			}
			if i > 0 { // prima cerere include deschiderea conexiunii
				rtts = append(rtts, ms)
			}
		}
		cc()
	}
	s := summarize(len(rtts), rtts)
	return s.avg, s.jitter
}

// ---------- Transfer (download / upload) ----------

type transferResult struct {
	mbps, peak, mb, loadedPing float64
	err                        string
}

var randBlock = func() []byte {
	b := make([]byte, 1<<20)
	rand.Read(b)
	return b
}()

type countingReader struct {
	ctx       context.Context
	remaining int64
	pos       int
	counter   *atomic.Int64
}

func (r *countingReader) Read(p []byte) (int, error) {
	if r.ctx.Err() != nil {
		return 0, r.ctx.Err()
	}
	if r.remaining <= 0 {
		return 0, io.EOF
	}
	if int64(len(p)) > r.remaining {
		p = p[:r.remaining]
	}
	if len(p) > 64<<10 {
		p = p[:64<<10] // bucăți mici = numărare mai fidelă a ce pleacă efectiv
	}
	n := copy(p, randBlock[r.pos:])
	r.pos = (r.pos + n) % len(randBlock)
	r.remaining -= int64(n)
	r.counter.Add(int64(n))
	return n, nil
}

func runTransfer(parent context.Context, upload bool, streams int, dur time.Duration, onSample func(Sample, float64)) transferResult {
	ctx, cc := context.WithTimeout(parent, dur)
	defer cc()

	var total atomic.Int64
	var lastErr atomic.Value
	var wg sync.WaitGroup
	chunk := int64(100 << 20)
	if upload {
		chunk = 25 << 20
	}
	var chunkSize atomic.Int64
	chunkSize.Store(chunk)

	worker := func(id int) {
		defer wg.Done()
		time.Sleep(time.Duration(id) * 60 * time.Millisecond) // pornire eșalonată
		for ctx.Err() == nil {
			size := chunkSize.Load()
			var req *http.Request
			if upload {
				body := &countingReader{ctx: ctx, remaining: size, counter: &total}
				req, _ = newReq(ctx, "POST", cfBase+"/__up", body)
				req.ContentLength = size
				req.Header.Set("Content-Type", "application/octet-stream")
			} else {
				req, _ = newReq(ctx, "GET", fmt.Sprintf("%s/__down?bytes=%d", cfBase, size), nil)
			}
			resp, err := httpClient.Do(req)
			if err != nil {
				if ctx.Err() == nil {
					lastErr.Store(shortErr(err))
					time.Sleep(300 * time.Millisecond)
				}
				continue
			}
			if resp.StatusCode != 200 {
				io.Copy(io.Discard, io.LimitReader(resp.Body, 64<<10))
				resp.Body.Close()
				lastErr.Store(fmt.Sprintf("serverul a răspuns HTTP %d", resp.StatusCode))
				if size > 4<<20 {
					chunkSize.CompareAndSwap(size, size/2)
				}
				time.Sleep(500 * time.Millisecond)
				continue
			}
			if upload {
				io.Copy(io.Discard, resp.Body)
			} else {
				buf := make([]byte, 128<<10)
				for {
					n, e := resp.Body.Read(buf)
					total.Add(int64(n))
					if e != nil {
						break
					}
				}
			}
			resp.Body.Close()
		}
	}
	for i := 0; i < streams; i++ {
		wg.Add(1)
		go worker(i)
	}

	// Ping în timpul transferului (arată dacă routerul/linia „se sufocă” sub încărcare).
	var loaded []float64
	var lmu sync.Mutex
	go func() {
		time.Sleep(1500 * time.Millisecond)
		for ctx.Err() == nil {
			if rtt, ok := icmpPing(net.IPv4(1, 1, 1, 1), time.Second); ok {
				lmu.Lock()
				loaded = append(loaded, rtt)
				lmu.Unlock()
			}
			select {
			case <-ctx.Done():
			case <-time.After(400 * time.Millisecond):
			}
		}
	}()

	const warmup = 2 * time.Second
	start := time.Now()
	type pt struct {
		t time.Time
		b int64
	}
	var window []pt
	var warmBytes int64 = -1
	var warmTime time.Time
	var res transferResult
	tick := time.NewTicker(200 * time.Millisecond)
	defer tick.Stop()
loop:
	for {
		select {
		case <-ctx.Done():
			break loop
		case now := <-tick.C:
			b := total.Load()
			window = append(window, pt{now, b})
			for len(window) > 2 && now.Sub(window[0].t) > time.Second {
				window = window[1:]
			}
			var inst float64
			if len(window) >= 2 {
				dt := window[len(window)-1].t.Sub(window[0].t).Seconds()
				if dt > 0 {
					inst = float64(window[len(window)-1].b-window[0].b) * 8 / dt / 1e6
				}
			}
			el := now.Sub(start)
			if warmBytes < 0 && el >= warmup {
				warmBytes, warmTime = b, now
			}
			if el >= warmup && inst > res.peak {
				res.peak = inst
			}
			onSample(Sample{T: round(el.Seconds(), 2), V: round(inst, 2)}, math.Min(1, el.Seconds()/dur.Seconds()))
		}
	}
	end := time.Now()
	b := total.Load()
	wg.Wait()

	if warmBytes >= 0 && end.Sub(warmTime) > 500*time.Millisecond {
		res.mbps = float64(b-warmBytes) * 8 / end.Sub(warmTime).Seconds() / 1e6
	} else if e := end.Sub(start).Seconds(); e > 0 {
		res.mbps = float64(b) * 8 / e / 1e6
	}
	res.mbps = round(res.mbps, 2)
	res.peak = round(res.peak, 2)
	res.mb = round(float64(b)/1e6, 1)
	lmu.Lock()
	if len(loaded) > 0 {
		res.loadedPing = round(summarize(len(loaded), loaded).avg, 1)
	} else {
		res.loadedPing = -1
	}
	lmu.Unlock()
	if v, ok := lastErr.Load().(string); ok && b == 0 {
		res.err = v
	}
	return res
}

func shortErr(err error) string {
	s := err.Error()
	if len(s) > 120 {
		s = s[:120] + "…"
	}
	return s
}

func round(v float64, d int) float64 {
	p := math.Pow(10, float64(d))
	return math.Round(v*p) / p
}

func envOr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}
