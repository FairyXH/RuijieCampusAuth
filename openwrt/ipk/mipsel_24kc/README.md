# mipsel_24kc

适用于 `ramips/mt7621`、`ramips/mt7620`、`ramips/mt76x8`、`ramips/rt305x`。
Go 交叉编译使用 `GOARCH=mipsle`（MT7621 无 FPU，使用 softfloat）。

## 当前包

| 包 | 版本 | 说明 |
|---|---|---|
| `ruijie-auth` | 0.10.0 / 0.11.4 | 锐捷认证器 |
| `ua3f` | 3.7.0-r1 | UA3F，适合 32MB+ 机型 |
| `ua2f` | 4.10.2-r1 | UA2F，适合 16MB 机型 |
| `https-dns-proxy` | 2026.05.06-r1 | DoH 代理 |
| `kmod-cfg80211` | 6.6.73.6.12.6-r1 | R3G 无线解锁，可选 |

## 构建

```sh
cd openwrt
GOARCH=mipsle PKG_ARCH=mipsel_24kc sh scripts/build-ipk.sh
```

产物放到 `ipk/mipsel_24kc/<package>/<version>/`，并重新生成 `SHA256SUMS`。
