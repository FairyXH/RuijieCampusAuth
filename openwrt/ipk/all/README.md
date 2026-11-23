# all

架构无关包：LuCI 页面、翻译、纯脚本资源。目录规则同样是
`<package>/<version>/<file>.ipk`。

## 当前包

| 包 | 版本 | 说明 |
|---|---|---|
| `luci-app-https-dns-proxy` | 2026.05.06-r1 | DoH LuCI 页面 |
| `luci-i18n-https-dns-proxy-zh-cn` | 26.301.70438~aa4865e | DoH 中文翻译 |

这些包不随 `GOARCH` 变化；`opkg` 会按 `Architecture: all` 安装到任意目标。
