package main

import (
	"net"
	"os"
	"time"
)

type WiFiInfo struct {
	Interface string  `json:"interface"`
	SSID      string  `json:"ssid"`
	BSSID     string  `json:"bssid"`
	Profile   string  `json:"profile"`
	Signal    int     `json:"signal"` // 0..100 %
	RSSI      int     `json:"rssi"`   // dBm
	Channel   int     `json:"channel"`
	Band      string  `json:"band"`
	Standard  string  `json:"standard"`
	RxMbps    float64 `json:"rxMbps"`
	TxMbps    float64 `json:"txMbps"`
	Security  string  `json:"security"`
	Cipher    string  `json:"cipher"`
}

type Adapter struct {
	Name       string   `json:"name"`
	Type       string   `json:"type"`
	MAC        string   `json:"mac"`
	IPs        []string `json:"ips"`
	Mask       string   `json:"mask"`
	Gateway    string   `json:"gateway"`
	DHCP       bool     `json:"dhcp"`
	DHCPServer string   `json:"dhcpServer"`
	LinkMbps   float64  `json:"linkMbps"`
	MTU        int      `json:"mtu"`
	Up         bool     `json:"up"`
	Primary    bool     `json:"primary"`
}

type LocalInfo struct {
	Hostname string    `json:"hostname"`
	ConnType string    `json:"connType"`
	Primary  *Adapter  `json:"primary"`
	Gateway  string    `json:"gateway"`
	DNS      []string  `json:"dns"`
	IPv6     bool      `json:"ipv6"`
	IPv6Addr string    `json:"ipv6Addr"`
	WiFi     *WiFiInfo `json:"wifi"`
	Adapters []Adapter `json:"adapters"`
	Note     string    `json:"note"`
}

// outboundIP: IP-ul local folosit pentru ieșirea spre internet (nu trimite niciun pachet).
func outboundIP() string {
	c, err := net.Dial("udp4", "1.1.1.1:53")
	if err != nil {
		return ""
	}
	defer c.Close()
	return c.LocalAddr().(*net.UDPAddr).IP.String()
}

func ipv6Check() (bool, string) {
	c, err := net.DialTimeout("tcp6", "[2606:4700:4700::1111]:443", 2*time.Second)
	if err != nil {
		return false, ""
	}
	defer c.Close()
	return true, c.LocalAddr().(*net.TCPAddr).IP.String()
}

func getLocalInfo() *LocalInfo {
	li := &LocalInfo{DNS: []string{}, Adapters: []Adapter{}}
	li.Hostname, _ = os.Hostname()
	defer func() {
		if r := recover(); r != nil {
			li.Note = "Unele date locale nu au putut fi citite."
		}
	}()
	fillPlatform(li, outboundIP())
	li.IPv6, li.IPv6Addr = ipv6Check()
	return li
}
