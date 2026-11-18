package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
)

const maxHistory = 500

var histMu sync.Mutex

// historyPath: lângă .exe (merge și de pe stick USB); dacă folderul nu e inscriptibil, în %APPDATA%.
func historyPath() string {
	if exe, err := os.Executable(); err == nil {
		dir := filepath.Dir(exe)
		p := filepath.Join(dir, "TestViteza-istoric.json")
		if f, err := os.OpenFile(p, os.O_RDWR|os.O_CREATE, 0o644); err == nil {
			f.Close()
			return p
		}
	}
	base, err := os.UserConfigDir()
	if err != nil {
		base = os.TempDir()
	}
	dir := filepath.Join(base, "InfoPC", "TestViteza")
	os.MkdirAll(dir, 0o755)
	return filepath.Join(dir, "istoric.json")
}

func loadHistory() []Result {
	histMu.Lock()
	defer histMu.Unlock()
	return readHistory()
}

func readHistory() []Result {
	var h []Result
	b, err := os.ReadFile(historyPath())
	if err == nil && len(b) > 0 {
		json.Unmarshal(b, &h)
	}
	if h == nil {
		h = []Result{}
	}
	return h
}

func saveHistory(r Result) {
	histMu.Lock()
	defer histMu.Unlock()
	h := append([]Result{r}, readHistory()...)
	if len(h) > maxHistory {
		h = h[:maxHistory]
	}
	writeHistory(h)
}

func clearHistory() {
	histMu.Lock()
	defer histMu.Unlock()
	writeHistory([]Result{})
}

func writeHistory(h []Result) {
	p := historyPath()
	b, _ := json.MarshalIndent(h, "", " ")
	tmp := p + ".tmp"
	if os.WriteFile(tmp, b, 0o644) == nil {
		if os.Rename(tmp, p) != nil {
			os.WriteFile(p, b, 0o644)
			os.Remove(tmp)
		}
	}
}
