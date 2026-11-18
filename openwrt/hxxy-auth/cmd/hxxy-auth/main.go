package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/config"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/control"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/daemon"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
	serverstore "github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/servers"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/status"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/version"
)

const defaultDataDir = "/etc/hxxy-auth"

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
	case "daemon":
		runDaemon(os.Args[2:])
	case "auth", "logoff", "pause", "resume", "reload":
		runControl(os.Args[1], os.Args[2:])
	case "accounts":
		runAccounts(os.Args[2:])
	case "servers":
		runServers(os.Args[2:])
	case "discovery":
		runDiscovery(os.Args[2:])
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
	fmt.Printf("%s | %s | %s | %s | %s\n",
		snapshot.State, snapshot.Message, snapshot.Account, snapshot.Interface, snapshot.Server)
}

func runDaemon(args []string) {
	defaults := daemon.DefaultPaths()
	fs := flag.NewFlagSet("daemon", flag.ExitOnError)
	configPath := fs.String("config", defaults.Config, "UCI 配置路径")
	socketPath := fs.String("socket", defaults.Socket, "控制 socket")
	statusPath := fs.String("status", defaults.Status, "状态文件")
	dataDir := fs.String("data-dir", defaults.DataDir, "服务器/缓存目录")
	logPath := fs.String("log", defaults.Log, "日志文件")
	_ = fs.Parse(args)
	if err := daemon.Run(context.Background(), daemon.Paths{
		Config:  *configPath,
		Socket:  *socketPath,
		Status:  *statusPath,
		DataDir: *dataDir,
		Log:     *logPath,
	}); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func runControl(command string, args []string) {
	fs := flag.NewFlagSet(command, flag.ExitOnError)
	socket := fs.String("socket", control.DefaultSocket, "控制 socket")
	_ = fs.Parse(args)
	if err := control.Send(*socket, command); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("ok")
}

func runAccounts(args []string) {
	fs := flag.NewFlagSet("accounts", flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	configPath := fs.String("config", config.DefaultPath, "UCI 配置路径")
	_ = fs.Parse(args)
	cfg, err := config.Load(*configPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	type publicAccount struct {
		Section  string `json:"section"`
		Username string `json:"username"`
		Note     string `json:"note"`
		Enabled  bool   `json:"enabled"`
		Active   bool   `json:"active"`
	}
	list := make([]publicAccount, 0, len(cfg.Accounts))
	for _, account := range cfg.Accounts {
		list = append(list, publicAccount{
			Section:  account.Section,
			Username: account.Username,
			Note:     account.Note,
			Enabled:  account.Enabled,
			Active:   account.Active,
		})
	}
	if *asJSON {
		data, _ := json.MarshalIndent(list, "", "  ")
		fmt.Println(string(data))
		return
	}
	for _, account := range list {
		flags := ""
		if account.Enabled {
			flags += "enabled"
		} else {
			flags += "disabled"
		}
		if account.Active {
			flags += ",active"
		}
		fmt.Printf("%s | %s | %s | %s\n", account.Section, account.Username, account.Note, flags)
	}
}

func runServers(args []string) {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "用法: servers list|lock|unlock|remove|rename")
		os.Exit(2)
	}
	action := args[0]
	fs := flag.NewFlagSet("servers "+action, flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	dataDir := fs.String("dir", defaultDataDir, "数据目录")
	_ = fs.Parse(args[1:])
	path := filepath.Join(*dataDir, serverstore.ServersFile)
	store, err := serverstore.Load(path)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	rest := fs.Args()
	switch action {
	case "list":
		list := store.Servers
		if list == nil {
			list = []serverstore.Server{}
		}
		if *asJSON {
			data, _ := json.MarshalIndent(list, "", "  ")
			fmt.Println(string(data))
			return
		}
		for _, record := range store.Servers {
			lock := ""
			if record.Locked {
				lock = " locked"
			}
			fmt.Printf("%s | %s | %s | success=%d%s\n",
				record.MAC, record.Name, record.Description, record.SuccessCount, lock)
		}
	case "lock":
		if len(rest) < 1 {
			fmt.Fprintln(os.Stderr, "用法: servers lock <mac>")
			os.Exit(2)
		}
		if !store.Lock(rest[0]) {
			fmt.Fprintln(os.Stderr, "服务器不存在或 MAC 无效")
			os.Exit(1)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("locked", rest[0])
	case "unlock":
		store.Unlock()
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("unlocked")
	case "remove":
		if len(rest) < 1 || !store.Remove(rest[0]) {
			fmt.Fprintln(os.Stderr, "服务器不存在或 MAC 无效")
			os.Exit(1)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("removed", rest[0])
	case "rename":
		if len(rest) < 2 || !store.Rename(rest[0], strings.Join(rest[1:], " ")) {
			fmt.Fprintln(os.Stderr, "用法: servers rename <mac> <name>")
			os.Exit(2)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("renamed", rest[0])
	default:
		fmt.Fprintln(os.Stderr, "用法: servers list|lock|unlock|remove|rename")
		os.Exit(2)
	}
}

func runDiscovery(args []string) {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "用法: discovery show|clear")
		os.Exit(2)
	}
	action := args[0]
	fs := flag.NewFlagSet("discovery "+action, flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	dataDir := fs.String("dir", defaultDataDir, "数据目录")
	_ = fs.Parse(args[1:])
	path := filepath.Join(*dataDir, serverstore.DiscoveryFile)
	switch action {
	case "show":
		discovery, err := serverstore.LoadDiscovery(path)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		if *asJSON {
			if discovery == nil {
				fmt.Println("{}")
				return
			}
			data, _ := json.MarshalIndent(discovery, "", "  ")
			fmt.Println(string(data))
			return
		}
		if discovery == nil {
			fmt.Println("no cache")
			return
		}
		fmt.Printf("%s | %s | %s -> %s | %s\n",
			discovery.Interface, discovery.Description, discovery.MAC,
			discovery.ServerMAC, discovery.UpdatedAt)
	case "clear":
		if err := serverstore.ClearDiscovery(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("cleared")
	default:
		fmt.Fprintln(os.Stderr, "用法: discovery show|clear")
		os.Exit(2)
	}
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
  version          打印版本
  daemon           常驻认证守护进程（procd 使用）
  status --json    读取 /var/run/hxxy-auth/status.json
  auth|logoff|pause|resume|reload
                   通过控制 socket 操作守护进程
  accounts         查看 UCI 账号池（不输出密码）
  servers list|lock|unlock|remove|rename
                   查看/管理已学习认证服务器
  discovery show|clear
                   查看/清除探测缓存
  tail             生成 569 字节私有尾（调试）
  selftest         跑 569 字节 golden vector
  help             显示帮助`)
}
