//go:build !windows

package main

import (
	"net"
	"os/exec"
	"time"
)

// Variantă pentru dezvoltare pe Linux/macOS (aplicația țintă e Windows).
func openWindow(url string) { exec.Command("xdg-open", url).Start() }

func icmpPing(ip net.IP, timeout time.Duration) (float64, bool) { return 0, false }

func fillPlatform(li *LocalInfo, outIP string) {
	ifs, _ := net.Interfaces()
	for _, i := range ifs {
		addrs, _ := i.Addrs()
		a := Adapter{Name: i.Name, Type: "Cablu (Ethernet)", MAC: i.HardwareAddr.String(), MTU: i.MTU, Up: i.Flags&net.FlagUp != 0}
		for _, ad := range addrs {
			if n, ok := ad.(*net.IPNet); ok && n.IP.To4() != nil && !n.IP.IsLoopback() {
				a.IPs = append(a.IPs, n.IP.String())
				a.Mask = net.IP(n.Mask).String()
				if n.IP.String() == outIP {
					a.Primary = true
				}
			}
		}
		if len(a.IPs) > 0 {
			li.Adapters = append(li.Adapters, a)
			if a.Primary {
				p := a
				li.Primary = &p
				li.ConnType = a.Type
			}
		}
	}
}
