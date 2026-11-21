# Vendored plugin artifacts

These files are pinned copies of the external packages baked into the R3G
firmware. `build-image.sh` prefers this directory over downloading, so a
fresh clone can reproduce the same image without network access.

| File | Version | Upstream | License |
|---|---|---|---|
| `ua3f_3.7.0-r1_mipsel_24kc.ipk` | 3.7.0 | https://github.com/SunBK201/UA3F | MIT |
| `luci-app-openclash_0.48.002_all.ipk` | 0.48.002 | https://github.com/vernesong/OpenClash | GPL-3.0 |
| `mihomo-linux-mipsle-softfloat-v1.19.34.gz` | 1.19.34 | https://github.com/MetaCubeX/mihomo | GPL-3.0 |
| `luci-theme-vantage_1.0.21-r1_all.ipk` | 1.0.21 | https://github.com/anothaDev/luci-theme-vantage | Apache-2.0 |
| `luci-i18n-vantage-theme-zh-cn_1.0.21-r1_all.ipk` | 1.0.21 | same | Apache-2.0 |
| `luci-app-vantage_1.0.21-r1_all.ipk` | 1.0.21 | same | Apache-2.0 |
| `luci-i18n-vantage-zh-cn_1.0.21-r1_all.ipk` | 1.0.21 | same | Apache-2.0 |

`SHA256SUMS` is checked by the build script for every file. To update a
plugin, replace the vendored file, update its SHA256 in both `SHA256SUMS`
and the matching constant in `scripts/build-image.sh`, then rebuild.

Runtime updates without reflashing:

- `ruijie-auth`, `ua3f`, Vantage and OpenClash are installed as packages;
  `opkg install --force-reinstall <newer.ipk>` upgrades them in place.
- `/etc/config/*` files are conffiles and survive package upgrades.
- The mihomo core can be replaced directly at `/etc/openclash/core/clash_meta`
  (matching softfloat mipsle build), or updated from the OpenClash UI.
