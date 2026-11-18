//go:build windows

package main

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

// DLL-urile se încarcă doar din System32 (nu din folderul .exe-ului — siguranță când rulează de pe stick).
func sys32(name string) string {
	root := os.Getenv("SystemRoot")
	if root == "" {
		root = `C:\Windows`
	}
	return filepath.Join(root, "System32", name)
}

var (
	iphlpapi             = syscall.NewLazyDLL(sys32("iphlpapi.dll"))
	procIcmpCreateFile   = iphlpapi.NewProc("IcmpCreateFile")
	procIcmpSendEcho     = iphlpapi.NewProc("IcmpSendEcho")
	procIcmpCloseHandle  = iphlpapi.NewProc("IcmpCloseHandle")
	procGetNetworkParams = iphlpapi.NewProc("GetNetworkParams")

	wlanapi                = syscall.NewLazyDLL(sys32("wlanapi.dll"))
	procWlanOpenHandle     = wlanapi.NewProc("WlanOpenHandle")
	procWlanCloseHandle    = wlanapi.NewProc("WlanCloseHandle")
	procWlanEnumInterfaces = wlanapi.NewProc("WlanEnumInterfaces")
	procWlanQueryInterface = wlanapi.NewProc("WlanQueryInterface")
	procWlanFreeMemory     = wlanapi.NewProc("WlanFreeMemory")
)

// ---------- Fereastra aplicației ----------

func openWindow(url string) {
	hide := &syscall.SysProcAttr{HideWindow: true}
	candidates := []string{
		filepath.Join(os.Getenv("ProgramFiles(x86)"), `Microsoft\Edge\Application\msedge.exe`),
		filepath.Join(os.Getenv("ProgramFiles"), `Microsoft\Edge\Application\msedge.exe`),
		filepath.Join(os.Getenv("LocalAppData"), `Microsoft\Edge\Application\msedge.exe`),
		filepath.Join(os.Getenv("ProgramFiles"), `Google\Chrome\Application\chrome.exe`),
		filepath.Join(os.Getenv("ProgramFiles(x86)"), `Google\Chrome\Application\chrome.exe`),
		filepath.Join(os.Getenv("LocalAppData"), `Google\Chrome\Application\chrome.exe`),
	}
	for _, p := range candidates {
		if st, err := os.Stat(p); err == nil && !st.IsDir() {
			cmd := exec.Command(p, "--app="+url, "--window-size=1240,900", "--no-first-run")
			cmd.SysProcAttr = hide
			if cmd.Start() == nil {
				return
			}
		}
	}
	// Rezervă: browserul implicit.
	cmd := exec.Command(sys32("rundll32.exe"), "url.dll,FileProtocolHandler", url)
	cmd.SysProcAttr = hide
	cmd.Start()
}

// ---------- Ping ICMP (fără drepturi de administrator) ----------

func icmpPing(ip net.IP, timeout time.Duration) (float64, bool) {
	ip4 := ip.To4()
	if ip4 == nil || procIcmpCreateFile.Find() != nil {
		return 0, false
	}
	h, _, _ := procIcmpCreateFile.Call()
	if h == 0 || h == uintptr(syscall.InvalidHandle) {
		return 0, false
	}
	defer procIcmpCloseHandle.Call(h)

	data := []byte("InfoPC-TestViteza-ping-32-bytes!")
	reply := make([]byte, 256+len(data))
	addr := binary.LittleEndian.Uint32(ip4) // IPAddr în ordinea octeților din memorie
	n, _, _ := procIcmpSendEcho.Call(
		h, uintptr(addr),
		uintptr(unsafe.Pointer(&data[0])), uintptr(len(data)),
		0,
		uintptr(unsafe.Pointer(&reply[0])), uintptr(len(reply)),
		uintptr(timeout.Milliseconds()),
	)
	if n == 0 {
		return 0, false
	}
	status := binary.LittleEndian.Uint32(reply[4:8])
	rtt := binary.LittleEndian.Uint32(reply[8:12])
	if status != 0 {
		return 0, false
	}
	if rtt == 0 {
		return 0.5, true // sub 1 ms
	}
	return float64(rtt), true
}

// ---------- Adaptoare de rețea ----------

func cstr(b []byte) string {
	if i := bytes.IndexByte(b, 0); i >= 0 {
		return string(b[:i])
	}
	return string(b)
}

func ipList(l *syscall.IpAddrString) (ips []string, mask string) {
	for ; l != nil; l = l.Next {
		ip := cstr(l.IpAddress.String[:])
		if ip != "" && ip != "0.0.0.0" {
			ips = append(ips, ip)
			if mask == "" {
				mask = cstr(l.IpMask.String[:])
			}
		}
	}
	return
}

func adapterType(t uint32) string {
	switch t {
	case 6:
		return "Cablu (Ethernet)"
	case 71:
		return "Wi-Fi"
	case 243, 244:
		return "Mobil (WWAN)"
	case 23:
		return "PPP / Dial-up"
	case 24:
		return "Loopback"
	case 131:
		return "Tunel / VPN"
	}
	return fmt.Sprintf("Altul (%d)", t)
}

func fillPlatform(li *LocalInfo, outIP string) {
	// GetAdaptersInfo
	size := uint32(16 << 10)
	var buf []byte
	for i := 0; i < 3; i++ {
		buf = make([]byte, size)
		err := syscall.GetAdaptersInfo((*syscall.IpAdapterInfo)(unsafe.Pointer(&buf[0])), &size)
		if err == nil {
			break
		}
		if err != syscall.ERROR_BUFFER_OVERFLOW {
			buf = nil
			break
		}
	}
	var primaryIdx = -1
	if buf != nil {
		for ai := (*syscall.IpAdapterInfo)(unsafe.Pointer(&buf[0])); ai != nil; ai = ai.Next {
			a := Adapter{
				Name: cstr(ai.Description[:]),
				Type: adapterType(ai.Type),
				DHCP: ai.DhcpEnabled != 0,
			}
			if ai.AddressLength > 0 && ai.AddressLength <= 8 {
				parts := make([]string, ai.AddressLength)
				for i := range parts {
					parts[i] = fmt.Sprintf("%02X", ai.Address[i])
				}
				a.MAC = strings.Join(parts, "-")
			}
			a.IPs, a.Mask = ipList(&ai.IpAddressList)
			if gws, _ := ipList(&ai.GatewayList); len(gws) > 0 {
				a.Gateway = gws[0]
			}
			if a.DHCP {
				if s, _ := ipList(&ai.DhcpServer); len(s) > 0 {
					a.DHCPServer = s[0]
				}
			}
			row := syscall.MibIfRow{Index: ai.Index}
			if syscall.GetIfEntry(&row) == nil {
				a.LinkMbps = float64(row.Speed) / 1e6
				a.MTU = int(row.Mtu)
				a.Up = row.OperStatus == 5 || row.OperStatus == 4 // CONNECTED / OPERATIONAL
			}
			if len(a.IPs) == 0 {
				continue // adaptor deconectat
			}
			for _, ip := range a.IPs {
				if ip == outIP {
					a.Primary = true
				}
			}
			li.Adapters = append(li.Adapters, a)
			if a.Primary {
				primaryIdx = len(li.Adapters) - 1
			}
		}
	}
	if primaryIdx < 0 {
		for i, a := range li.Adapters {
			if a.Gateway != "" {
				primaryIdx = i
				li.Adapters[i].Primary = true
				break
			}
		}
	}
	if primaryIdx >= 0 {
		p := li.Adapters[primaryIdx]
		li.Primary = &p
		li.Gateway = p.Gateway
		li.ConnType = p.Type
	}

	li.DNS = dnsServers()
	li.WiFi = wifiInfo()
	if li.ConnType == "Wi-Fi" && li.WiFi == nil {
		li.Note = "Datele Wi-Fi nu au putut fi citite (serviciul WLAN AutoConfig e oprit?)."
	}
}

type fixedInfo struct {
	HostName         [132]byte
	DomainName       [132]byte
	CurrentDnsServer *syscall.IpAddrString
	DnsServerList    syscall.IpAddrString
	NodeType         uint32
	ScopeId          [260]byte
	EnableRouting    uint32
	EnableProxy      uint32
	EnableDns        uint32
}

func dnsServers() []string {
	out := []string{}
	if procGetNetworkParams.Find() != nil {
		return out
	}
	size := uint32(unsafe.Sizeof(fixedInfo{}))
	for i := 0; i < 3; i++ {
		buf := make([]byte, size)
		r, _, _ := procGetNetworkParams.Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)))
		if r == 0 {
			fi := (*fixedInfo)(unsafe.Pointer(&buf[0]))
			ips, _ := ipList(&fi.DnsServerList)
			return append(out, ips...)
		}
		if r != uintptr(syscall.ERROR_BUFFER_OVERFLOW) {
			break
		}
	}
	return out
}

// ---------- Wi-Fi (Native Wifi API) ----------

type wlanIntfInfo struct {
	Guid  syscall.GUID
	Desc  [256]uint16
	State uint32
}

type dot11SSID struct {
	Len  uint32
	SSID [32]byte
}

type wlanAssoc struct {
	SSID     dot11SSID
	BssType  uint32
	Bssid    [6]byte
	_        [2]byte
	PhyType  uint32
	PhyIndex uint32
	Signal   uint32
	RxRate   uint32 // kbps
	TxRate   uint32 // kbps
}

type wlanSec struct {
	Enabled int32
	OneX    int32
	Auth    uint32
	Cipher  uint32
}

type wlanConnAttr struct {
	State   uint32
	Mode    uint32
	Profile [256]uint16
	Assoc   wlanAssoc
	Sec     wlanSec
}

const (
	opCurrentConnection = 7
	opChannelNumber     = 8
	opRSSI              = 0x10000102
)

func wlanQuery(h uintptr, guid *syscall.GUID, op uint32) (unsafe.Pointer, uint32, bool) {
	var size uint32
	var data unsafe.Pointer
	r, _, _ := procWlanQueryInterface.Call(h, uintptr(unsafe.Pointer(guid)), uintptr(op), 0,
		uintptr(unsafe.Pointer(&size)), uintptr(unsafe.Pointer(&data)), 0)
	if r != 0 || data == nil {
		return nil, 0, false
	}
	return data, size, true
}

func wifiInfo() (wi *WiFiInfo) {
	defer func() {
		if recover() != nil {
			wi = nil
		}
	}()
	if wlanapi.Load() != nil {
		return nil
	}
	var negotiated uint32
	var h uintptr
	if r, _, _ := procWlanOpenHandle.Call(2, 0, uintptr(unsafe.Pointer(&negotiated)), uintptr(unsafe.Pointer(&h))); r != 0 {
		return nil
	}
	defer procWlanCloseHandle.Call(h, 0)

	var list unsafe.Pointer
	if r, _, _ := procWlanEnumInterfaces.Call(h, 0, uintptr(unsafe.Pointer(&list))); r != 0 || list == nil {
		return nil
	}
	defer procWlanFreeMemory.Call(uintptr(list))

	n := *(*uint32)(list)
	for i := uint32(0); i < n; i++ {
		item := (*wlanIntfInfo)(unsafe.Add(list, 8+uintptr(i)*unsafe.Sizeof(wlanIntfInfo{})))
		if item.State != 1 { // wlan_interface_state_connected
			continue
		}
		data, size, ok := wlanQuery(h, &item.Guid, opCurrentConnection)
		if !ok {
			continue
		}
		if uintptr(size) < unsafe.Sizeof(wlanConnAttr{}) {
			procWlanFreeMemory.Call(uintptr(data))
			continue
		}
		ca := *(*wlanConnAttr)(data)
		procWlanFreeMemory.Call(uintptr(data))

		w := &WiFiInfo{
			Interface: syscall.UTF16ToString(item.Desc[:]),
			Profile:   syscall.UTF16ToString(ca.Profile[:]),
			Signal:    int(ca.Assoc.Signal),
			RxMbps:    float64(ca.Assoc.RxRate) / 1000,
			TxMbps:    float64(ca.Assoc.TxRate) / 1000,
			Standard:  phyName(ca.Assoc.PhyType),
			Security:  authName(ca.Sec.Auth),
			Cipher:    cipherName(ca.Sec.Cipher),
		}
		l := ca.Assoc.SSID.Len
		if l > 32 {
			l = 32
		}
		w.SSID = string(ca.Assoc.SSID.SSID[:l])
		b := ca.Assoc.Bssid
		w.BSSID = fmt.Sprintf("%02X:%02X:%02X:%02X:%02X:%02X", b[0], b[1], b[2], b[3], b[4], b[5])

		if d, _, ok := wlanQuery(h, &item.Guid, opChannelNumber); ok {
			w.Channel = int(*(*uint32)(d))
			procWlanFreeMemory.Call(uintptr(d))
		}
		if d, _, ok := wlanQuery(h, &item.Guid, opRSSI); ok {
			w.RSSI = int(*(*int32)(d))
			procWlanFreeMemory.Call(uintptr(d))
		}
		if w.RSSI == 0 || w.RSSI < -120 || w.RSSI > 0 {
			w.RSSI = w.Signal/2 - 100 // aproximare din calitatea semnalului
		}
		switch {
		case w.Channel >= 1 && w.Channel <= 14:
			w.Band = "2,4 GHz"
		case w.Channel >= 32 && w.Channel <= 177:
			w.Band = "5 GHz"
		case w.Channel > 177:
			w.Band = "6 GHz"
		}
		return w
	}
	return nil
}

func phyName(t uint32) string {
	switch t {
	case 4:
		return "802.11a"
	case 5:
		return "802.11b"
	case 6:
		return "802.11g"
	case 7:
		return "802.11n (Wi-Fi 4)"
	case 8:
		return "802.11ac (Wi-Fi 5)"
	case 9:
		return "802.11ad (WiGig)"
	case 10:
		return "802.11ax (Wi-Fi 6/6E)"
	case 11:
		return "802.11be (Wi-Fi 7)"
	}
	return "necunoscut"
}

func authName(a uint32) string {
	switch a {
	case 1:
		return "Deschisă (fără parolă)"
	case 2:
		return "WEP partajat"
	case 3:
		return "WPA-Enterprise"
	case 4:
		return "WPA-Personal"
	case 6:
		return "WPA2-Enterprise"
	case 7:
		return "WPA2-Personal"
	case 8, 11:
		return "WPA3-Enterprise"
	case 9:
		return "WPA3-Personal"
	case 10:
		return "OWE (Enhanced Open)"
	}
	return fmt.Sprintf("altă (%d)", a)
}

func cipherName(c uint32) string {
	switch c {
	case 0:
		return "fără criptare"
	case 1, 5, 0x101:
		return "WEP"
	case 2:
		return "TKIP"
	case 4:
		return "AES (CCMP)"
	case 8, 9:
		return "AES (GCMP)"
	}
	return fmt.Sprintf("%d", c)
}
