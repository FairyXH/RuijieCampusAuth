package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"runtime"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/status"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/version"
)

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	switch os.Args[1] {
	case "version":
		fmt.Printf("hxxy-auth %s commit=%s go=%s %s/%s\n",
			version.Version, version.Commit, runtime.Version(), runtime.GOOS, runtime.GOARCH)
	case "status":
		runStatus(os.Args[2:])
	case "tail":
		runTail(os.Args[2:])
	case "selftest":
		runSelftest()
	case "help", "-h", "--help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "未知命令: %s\n", os.Args[1])
		usage()
		os.Exit(2)
	}
}

func runStatus(args []string) {
	fs := flag.NewFlagSet("status", flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	path := fs.String("file", status.DefaultPath, "状态文件路径")
	_ = fs.Parse(args)
	snapshot := status.ReadOrDefault(*path)
	if *asJSON {
		data, err := json.MarshalIndent(snapshot, "", "  ")
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println(string(data))
		return
	}
	fmt.Printf("%s | %s | %s | %s\n",
		snapshot.State, snapshot.Message, snapshot.Interface, snapshot.Server)
}

func runTail(args []string) {
	fs := flag.NewFlagSet("tail", flag.ExitOnError)
	username := fs.String("username", "", "校园网用户名")
	password := fs.String("password", "", "校园网密码")
	mac := fs.String("mac", "", "本机 MAC")
	ip := fs.String("ip", "0.0.0.0", "IPv4 地址")
	netmask := fs.String("netmask", "0.0.0.0", "子网掩码")
	gateway := fs.String("gateway", "0.0.0.0", "网关")
	dhcp := fs.String("dhcp", "", "DHCP 服务器（默认同网关）")
	ipString := fs.String("ip-string", "", "0x76 IP 字符串（默认同 IP）")
	service := fs.String("service-name", "电信宽带服务", "0x39 服务名")
	staticID := fs.String("static-id", "Static:AB45A862", "0x54 静态 ID")
	ivHex := fs.String("iv", "", "16 字节 IV 的 hex，默认全零")
	_ = fs.Parse(args)

	if *username == "" || *password == "" || *mac == "" {
		fmt.Fprintln(os.Stderr, "tail 需要 --username --password --mac")
		os.Exit(2)
	}
	iv := make([]byte, 16)
	if *ivHex != "" {
		decoded, err := hex.DecodeString(*ivHex)
		if err != nil || len(decoded) != 16 {
			fmt.Fprintln(os.Stderr, "--iv 必须是 16 字节 hex")
			os.Exit(2)
		}
		iv = decoded
	}
	serviceName, err := protocol.GB18030Encode(*service)
	if err != nil {
		fmt.Fprintf(os.Stderr, "服务名 GB18030 编码失败: %v\n", err)
		os.Exit(1)
	}
	dhcpValue := *dhcp
	if dhcpValue == "" {
		dhcpValue = *gateway
	}
	network := protocol.NetworkInfo{
		IP:       *ip,
		Netmask:  *netmask,
		Gateway:  *gateway,
		DHCP:     dhcpValue,
		IPString: *ipString,
		StaticID: []byte(*staticID),
	}
	tail, err := protocol.BuildTail(
		[]byte(*username),
		[]byte(*password),
		*mac,
		network,
		iv,
		&protocol.TailOptions{HeaderFlag: 1, ServiceName: serviceName},
	)
	if err != nil {
		fmt.Fprintf(os.Stderr, "生成私有尾失败: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("len=%d\n%x\n", len(tail), tail)
}

func runSelftest() {
	serviceName, err := protocol.GB18030Encode("电信宽带服务")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	network := protocol.NetworkInfo{
		IP:       "172.18.28.6",
		Netmask:  "255.255.128.0",
		Gateway:  "172.18.0.1",
		DHCP:     "172.18.0.1",
		IPv6:     make([]byte, 16),
		IPString: "172.18.28.6",
		StaticID: []byte("Static:AB45A862"),
	}
	tail, err := protocol.BuildTail(
		[]byte("user"),
		[]byte("pass"),
		"aa:bb:cc:dd:ee:ff",
		network,
		make([]byte, 16),
		&protocol.TailOptions{
			HeaderFlag:  1,
			Nonce:       bytes.Repeat([]byte("A"), 32),
			Fingerprint: bytes.Repeat([]byte("B"), 128),
			ServiceName: serviceName,
		},
	)
	if err != nil {
		fmt.Fprintf(os.Stderr, "selftest 构建失败: %v\n", err)
		os.Exit(1)
	}
	got := fmt.Sprintf("%x", sha256.Sum256(tail))
	const want = "779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5"
	fmt.Printf("len=%d sha256=%s\n", len(tail), got)
	if len(tail) != 569 || got != want {
		fmt.Fprintln(os.Stderr, "selftest 失败")
		os.Exit(1)
	}
	fmt.Println("selftest ok")
}

func usage() {
	fmt.Println(`用法: hxxy-auth <command>

命令:
  version        打印版本
  status         读取 /var/run/hxxy-auth/status.json
  status --json  LuCI 使用的 JSON 输出
  tail           用给定账号/MAC/IP 生成 569 字节私有尾（调试）
  selftest       跑 569 字节 golden vector
  help           显示帮助`)
}
