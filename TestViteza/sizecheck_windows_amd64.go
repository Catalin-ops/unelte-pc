//go:build windows && amd64

package main

import "unsafe"

// Verificări la compilare: dimensiunile structurilor trebuie să fie identice cu cele din Windows SDK (x64).
var (
	_ = [1]struct{}{}[unsafe.Sizeof(wlanIntfInfo{})-532]
	_ = [1]struct{}{}[unsafe.Sizeof(wlanConnAttr{})-604]
	_ = [1]struct{}{}[unsafe.Offsetof(wlanConnAttr{}.Assoc)-520]
	_ = [1]struct{}{}[unsafe.Offsetof(wlanAssoc{}.PhyType)-48]
	_ = [1]struct{}{}[unsafe.Offsetof(fixedInfo{}.DnsServerList)-272]
)
