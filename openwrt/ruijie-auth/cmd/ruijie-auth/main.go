package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"ruijie-auth/internal/config"
	"ruijie-auth/internal/control"
	"ruijie-auth/internal/daemon"
	"ruijie-auth/internal/netcfg"
	"ruijie-auth/internal/profile"
	"ruijie-auth/internal/protocol"
	serverstore "ruijie-auth/internal/servers"
	"ruijie-auth/internal/status"
	"ruijie-auth/internal/version"
)

const (
	defaultDataDir     = "/etc/ruijie-auth"
	defaultProfileDirs = "/etc/ruijie-auth/profiles:/usr/share/ruijie-auth/profiles"
)

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	switch os.Args[1] {
	case "version":
		fmt.Printf("ruijie-auth %s commit=%s go=%s %s/%s\n",
			version.Version, version.Commit, runtime.Version(), runtime.GOOS, runtime.GOARCH)
	case "status":
		runStatus(os.Args[2:])
	case "log":
		runLog(os.Args[2:])
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
	case "mac":
		runMAC(os.Args[2:])
	case "interfaces":
		runInterfaces(os.Args[2:])
	case "profile":
		runProfile(os.Args[2:])
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
	fmt.Printf("%s | %s | %s | %s | %s | %s\n",
		stateText(snapshot.State), snapshot.Message, snapshot.Account,
		snapshot.Interface, snapshot.Server,
		protocol.ParseAuthMethod(snapshot.AuthMode).Display())
}

func runLog(args []string) {
	fs := flag.NewFlagSet("log", flag.ExitOnError)
	lines := fs.Int("lines", 300, "输出最后 N 行")
	path := fs.String("file", "/var/log/ruijie-auth.log", "日志文件")
	_ = fs.Parse(args)

	data, err := os.ReadFile(*path)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	text := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
	start := len(text) - *lines
	if start < 0 {
		start = 0
	}
	for _, line := range text[start:] {
		fmt.Println(line)
	}
}

func stateText(state string) string {
	switch state {
	case "starting":
		return "启动中"
	case "authenticating":
		return "认证中"
	case "authenticated":
		return "已认证"
	case "retrying":
		return "重试中"
	case "paused":
		return "已暂停"
	case "idle":
		return "已注销"
	case "disabled":
		return "已停用"
	case "error":
		return "错误"
	case "stopped":
		return "已停止"
	default:
		return state
	}
}

func runDaemon(args []string) {
	defaults := daemon.DefaultPaths()
	fs := flag.NewFlagSet("daemon", flag.ExitOnError)
	configPath := fs.String("config", defaults.Config, "UCI 配置路径")
	socketPath := fs.String("socket", defaults.Socket, "控制套接字")
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
	socket := fs.String("socket", control.DefaultSocket, "控制套接字")
	_ = fs.Parse(args)
	if err := control.Send(*socket, command); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("成功")
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
		flags := "已停用"
		if account.Enabled {
			flags = "已启用"
		}
		if account.Active {
			flags += "，当前"
		}
		fmt.Printf("%s | %s | %s | %s\n",
			account.Section, account.Username, account.Note, flags)
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
				lock = " 已锁定"
			}
			fmt.Printf("%s | %s | %s | 成功=%d%s\n",
				record.MAC, record.Name, record.Description, record.SuccessCount, lock)
		}
	case "lock":
		if len(rest) < 1 || !store.Lock(rest[0]) {
			fmt.Fprintln(os.Stderr, "服务器不存在或 MAC 无效")
			os.Exit(1)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("已锁定", rest[0])
	case "unlock":
		store.Unlock()
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("已解锁")
	case "remove":
		if len(rest) < 1 || !store.Remove(rest[0]) {
			fmt.Fprintln(os.Stderr, "服务器不存在或 MAC 无效")
			os.Exit(1)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("已删除", rest[0])
	case "rename":
		if len(rest) < 2 || !store.Rename(rest[0], strings.Join(rest[1:], " ")) {
			fmt.Fprintln(os.Stderr, "用法: servers rename <mac> <name>")
			os.Exit(2)
		}
		if err := store.Save(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println("已重命名", rest[0])
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
			fmt.Println("无缓存")
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
		fmt.Println("已清除")
	default:
		fmt.Fprintln(os.Stderr, "用法: discovery show|clear")
		os.Exit(2)
	}
}

func runMAC(args []string) {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "用法: mac show|set|random|clear")
		os.Exit(2)
	}
	action := args[0]
	fs := flag.NewFlagSet("mac "+action, flag.ExitOnError)
	section := fs.String("section", "wan", "网络配置节")
	asJSON := fs.Bool("json", false, "输出 JSON")
	_ = fs.Parse(args[1:])
	rest := fs.Args()

	switch action {
	case "show":
		info, err := netcfg.Show(*section)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		if *asJSON {
			data, _ := json.MarshalIndent(info, "", "  ")
			fmt.Println(string(data))
			return
		}
		fmt.Printf("section=%s device=%s configured=%s effective=%s\n",
			info.Section, info.Device, info.Configured, info.Effective)
	case "set":
		if len(rest) < 1 {
			fmt.Fprintln(os.Stderr, "用法: mac set [--section wan] <mac>")
			os.Exit(2)
		}
		if err := netcfg.Set(*section, rest[0]); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		_ = control.Send(control.DefaultSocket, "reload")
		fmt.Println("MAC 已设置:", rest[0])
	case "random":
		mac, err := netcfg.Random()
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		if err := netcfg.Set(*section, mac); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		_ = control.Send(control.DefaultSocket, "reload")
		fmt.Println(mac)
	case "clear":
		if err := netcfg.Clear(*section); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		_ = control.Send(control.DefaultSocket, "reload")
		fmt.Println("MAC 已清除")
	default:
		fmt.Fprintln(os.Stderr, "用法: mac show|set|random|clear")
		os.Exit(2)
	}
}

// runInterfaces lists campus-WAN candidates for the LuCI multi-select.
func runInterfaces(args []string) {
	fs := flag.NewFlagSet("interfaces", flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	auto := fs.Bool("auto", false, "只输出自动识别会选择的接口")
	_ = fs.Parse(args)
	list := netcfg.Interfaces()
	if *auto {
		list = netcfg.ResolveCandidates([]string{config.AutoDevice})
	}
	if *asJSON {
		data, _ := json.MarshalIndent(list, "", "  ")
		fmt.Println(string(data))
		return
	}
	for _, item := range list {
		marker := " "
		if item.IsWAN {
			marker = "*"
		}
		link := "无链路"
		if item.Carrier {
			link = "已连接"
		}
		fmt.Printf("%s %s | %s | 链路=%s | %s\n",
			marker, item.Name, item.MAC, link, item.Description)
	}
}

func runProfile(args []string) {
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "用法: profile list|show|import")
		os.Exit(2)
	}
	action := args[0]
	fs := flag.NewFlagSet("profile "+action, flag.ExitOnError)
	asJSON := fs.Bool("json", false, "输出 JSON")
	dirs := fs.String("dir", defaultProfileDirs, "档案目录（冒号分隔）")
	file := fs.String("file", "", "档案文件")
	configPath := fs.String("config", config.DefaultPath, "UCI 配置路径")
	_ = fs.Parse(args[1:])

	switch action {
	case "list":
		var entries []map[string]string
		for _, directory := range strings.Split(*dirs, ":") {
			for _, path := range profile.List(directory) {
				loaded, err := profile.Load(path)
				name := filepath.Base(path)
				if err == nil {
					name = loaded.Name
				}
				entries = append(entries, map[string]string{
					"path": path, "name": name,
				})
			}
		}
		if entries == nil {
			entries = []map[string]string{}
		}
		if *asJSON {
			data, _ := json.MarshalIndent(entries, "", "  ")
			fmt.Println(string(data))
			return
		}
		for _, entry := range entries {
			fmt.Printf("%s | %s\n", entry["path"], entry["name"])
		}
	case "show":
		if *file == "" {
			fmt.Fprintln(os.Stderr, "用法: profile show --file <path>")
			os.Exit(2)
		}
		loaded, err := profile.Load(*file)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		data, _ := json.MarshalIndent(loaded, "", "  ")
		fmt.Println(string(data))
	case "import":
		if *file == "" {
			fmt.Fprintln(os.Stderr, "用法: profile import --file <path> [--config path]")
			os.Exit(2)
		}
		loaded, err := profile.Load(*file)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		absolute, _ := filepath.Abs(*file)
		result, err := profile.Import(loaded, *configPath, absolute)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		_ = control.Send(control.DefaultSocket, "reload")
		if *asJSON {
			data, _ := json.MarshalIndent(result, "", "  ")
			fmt.Println(string(data))
			return
		}
		fmt.Printf("已导入 %s -> %s（应用 %d 个字段）\n",
			result.ProfileName, result.ConfigPath, len(result.Applied))
		if result.BackupPath != "" {
			fmt.Println("备份:", result.BackupPath)
		}
	default:
		fmt.Fprintln(os.Stderr, "用法: profile list|show|import")
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
	service := fs.String("service-name", "", "0x39 服务名（档案提供）")
	staticID := fs.String("static-id", "", "0x54 静态 ID（档案提供）")
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

// runSelftest exercises the algorithm layer with vendor-neutral vectors so a
// freshly flashed router can prove the binary works without a live server.
func runSelftest() {
	failures := 0
	check := func(name string, ok bool, detail string) {
		status := "通过"
		if !ok {
			status = "失败"
			failures++
		}
		fmt.Printf("%-28s %s %s\n", name, status, detail)
	}

	rc4 := protocol.RC4Transform([]byte("Plaintext"), []byte("Key"))
	check("rc4-vector", hex.EncodeToString(rc4) == "bbf316e8d940af0ad3",
		hex.EncodeToString(rc4))

	md4 := hex.EncodeToString(protocol.MD4Hash([]byte("abc")))
	check("md4-vector", md4 == "a448017aaf21d8525fc10ae87aa6729d", md4)

	nt := protocol.GenerateNTResponse(
		mustDecodeHex("5B5D7C7D7B3F2F3E3C2C602132262628"),
		mustDecodeHex("21402324255E262A28295F2B3A337C7E"),
		"User", "clientPass")
	check("mschapv2-nt-response",
		hex.EncodeToString(nt) == "82309ecd8d708b5ea08faa3981cd83544233114a3d85d6df",
		hex.EncodeToString(nt))

	network := protocol.NetworkInfo{
		IP:       "192.0.2.100",
		Netmask:  "255.255.255.0",
		Gateway:  "192.0.2.1",
		DHCP:     "192.0.2.1",
		IPv6:     make([]byte, 16),
		IPString: "192.0.2.100",
	}
	tail, err := protocol.BuildTail(
		[]byte("user"), []byte("pass"), "aa:bb:cc:dd:ee:ff", network,
		make([]byte, 16),
		&protocol.TailOptions{
			HeaderFlag:  1,
			Nonce:       bytes.Repeat([]byte("A"), 32),
			Fingerprint: bytes.Repeat([]byte("B"), 128),
		})
	check("private-tail", err == nil && len(tail) == 569,
		fmt.Sprintf("长度=%d 错误=%v", len(tail), err))

	obfuscated := protocol.Obfuscate([]byte{0x00, 0x00, 0x13, 0x11, 0x0A, 0x00})
	roundTrip := protocol.Obfuscate(obfuscated)
	check("obfuscate-involution",
		hex.EncodeToString(roundTrip) == "000013110a00",
		hex.EncodeToString(obfuscated))

	parsed := protocol.ParsePrivateProperties(selftestPrivateBlock(60))
	intervalOk := parsed.Section5IntervalMs != nil && *parsed.Section5IntervalMs == 20000
	propertyOk := len(parsed.Properties) == 1 &&
		parsed.Properties[0].Name == "SU_REAUTH_INTERV" &&
		parsed.Properties[0].Number != nil && *parsed.Properties[0].Number == 60
	check("private-properties", len(parsed.Errors) == 0 && intervalOk && propertyOk,
		fmt.Sprintf("错误=%v 属性=%d", parsed.Errors, len(parsed.Properties)))

	if failures > 0 {
		fmt.Fprintf(os.Stderr, "selftest 失败: %d\n", failures)
		os.Exit(1)
	}
	fmt.Println("自检通过")
}

// selftestPrivateBlock builds a complete synthetic server private-property
// block: sections 1-5 plus one SU_REAUTH_INTERV TLV.
func selftestPrivateBlock(reauthSeconds byte) []byte {
	var out []byte
	out = append(out, 0x00, 0x00, 0x13, 0x11, 0x00, 0x02, 'O', 'K')
	out = append(out, 0x00, 0x00, 0x13, 0x11, 0x00, 0x00)
	out = append(out, 0x00, 0x00, 0x00, 0x00)
	out = append(out, make([]byte, 0x64)...)
	out = append(out, 0x00)
	out = append(out, 0x00, 0x00, 0x13, 0x11, 0x01, 0x00)
	out = append(out, 0x00, 0x00, 0x13, 0x11, 0x01, 0x01)
	plain := []byte{0x00, 0x00, 0x13, 0x11, 0x0A, 0x00, 0x19, 0x59,
		0x4C, 0x7A, 0x00, 0x00, 0x00, 0x14, 0x00}
	out = append(out, protocol.Obfuscate(plain)...)
	out = append(out, 0x00, 0x00, 0x13, 0x11, 0x56, 0x06,
		0x00, 0x00, 0x00, reauthSeconds)
	return out
}

func mustDecodeHex(value string) []byte {
	data, err := hex.DecodeString(value)
	if err != nil {
		panic(err)
	}
	return data
}

func usage() {
	fmt.Println(`用法: ruijie-auth <command>

命令:
  version          打印版本
  daemon           常驻认证守护进程（procd 使用）
  status --json    读取 /var/run/ruijie-auth/status.json
  log [--lines N]  输出认证日志末尾（默认 300 行）
  auth|logoff|pause|resume|reload
                   通过控制 socket 操作守护进程
  accounts         查看 UCI 账号池（不输出密码）
  servers list|lock|unlock|remove|rename
                   查看/管理已学习认证服务器
  discovery show|clear
                   查看/清除探测缓存
  mac show|set|random|clear
                   查看/修改 WAN MAC（写 network UCI 并 ifup）
  interfaces --json
                   列出可用的 WAN 候选接口（LuCI 下拉框）
  profile list|show|import
                   查看/导入学校配置档案
  tail             生成 569 字节私有尾（调试）
  selftest         跑算法层自检向量
  help             显示帮助`)
}
