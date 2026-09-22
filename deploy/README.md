# Linux 部署

**方式：在 Windows 上发布 → 打包上传 → 服务器解包运行。** 服务器不用装 .NET SDK，也不用给它 GitHub 访问权。

目标系统：Ubuntu 22.04/24.04、Debian 12。RHEL 系差异在末尾。

## 布局

| 路径 | 内容 | 属主 | 权限 |
|---|---|---|---|
| `/opt/dtuserver` | 程序 + wwwroot | `root` | 服务只读 |
| `/var/lib/dtuserver` | SQLite 库 | `dtuserver` | 服务可写 |
| `/opt/dtuserver/appsettings.Production.json` | 密钥 | `root:dtuserver` | `640` |

程序目录只读是刻意的——被入侵也改不了程序本身。SQLite 单独放可写目录：它要在库文件**同目录**建 `-wal`/`-shm`，光给文件写权限不够，得给目录权限。

## 端口

| 端口 | 用途 | 能不能改 |
|---|---|---|
| **9116** | DTU 裸 TCP | **改不了**，设备里写死的 |
| 5000 | 网页 + API + SignalR | 能改，跑完预检按实际空闲端口定 |
| 9080 | 桌面端 SignalR 备用口 | 能改，但要和桌面端配置对上 |

> **9116 不走任何代理** —— 它是裸 TCP 不是 HTTP，DTU 直连服务器这个端口。

---

## 1. 预检（第一步，别跳过）

服务器上可能已经跑着别的服务。先摸清端口占用和既有组件：

```bash
sudo apt install -y iproute2
sudo bash deploy/preflight.sh 2>&1 | tee ~/preflight.txt
```

脚本只读，不改系统。重点看三处：**9116 是否空闲**、网页端口候选哪个空着、**到阿里云 MQTT 1883 的出站通不通**。

## 2. 在 Windows 上发布

```bat
cd /d D:\Project\MaxChemical.DtuServer
git pull
deploy\publish-linux.bat
```

产出 `D:\Publish\DtuServer-linux.tar.gz`，约 100 MB（自包含，含 .NET 运行时）。

脚本会自动校验产物完整性，并确认 `appsettings.Production.json` 没混进去。

> **为什么要专门查这个**：Web SDK 默认把 `**/*.json` 都发布出去，你 Windows 那台的密钥配置会被一起带走。带到 Linux 上证书路径、SQLite 路径全是错的，更糟的是 `Mqtt:ClientSuffix` 撞了——阿里云同一个 clientId 只允许一个连接，两台服务器会互相把对方踢下线，现象是反复「连上又断」。csproj 里已经禁止它进发布产物了，脚本再兜一道。

手工发布的话：

```bat
dotnet publish -c Release -r linux-x64 --self-contained true -o D:\Publish\DtuServer-linux
del D:\Publish\DtuServer-linux\appsettings.Production.json
tar -czf D:\Publish\DtuServer-linux.tar.gz -C D:\Publish\DtuServer-linux .
```

## 3. 服务器准备

```bash
sudo apt update && sudo apt install -y libicu-dev

sudo useradd --system --no-create-home --shell /usr/sbin/nologin dtuserver
sudo mkdir -p /opt/dtuserver /var/lib/dtuserver
sudo chown dtuserver:dtuserver /var/lib/dtuserver
sudo chmod 750 /var/lib/dtuserver
```

> `libicu` 是硬依赖，**自包含产物也要装**（ICU 不打包进去）。缺了程序直接崩在
> `Couldn't find a valid ICU package`。这项目全是中文，别用 `InvariantGlobalization` 绕。

## 4. 上传解包

用 WinSCP / scp 把 `DtuServer-linux.tar.gz` 传到服务器家目录，然后：

```bash
sudo tar -xzf ~/DtuServer-linux.tar.gz -C /opt/dtuserver
sudo chmod +x /opt/dtuserver/MaxChemical.DtuServer
```

⚠️ **`chmod +x` 不能漏。** Windows 打的包没有可执行位，漏了 systemd 报 `203/EXEC` 起不来。

## 5. 配密钥

把 `deploy/appsettings.Production.json.example` 的内容贴进去改：

```bash
sudo nano /opt/dtuserver/appsettings.Production.json
sudo chown root:dtuserver /opt/dtuserver/appsettings.Production.json
sudo chmod 640 /opt/dtuserver/appsettings.Production.json
```

`640` = root 可改、服务账号可读、其他人看不见（里面有阿里云 AK/SK）。

不用域名时，几个关键值：

```jsonc
"Kestrel": { "Endpoints": {
  "Http":    { "Url": "http://0.0.0.0:5000" },   // 换成预检确认空闲的端口
  "Hub9080": { "Url": "http://0.0.0.0:9080" }
}},
"ConnectionStrings": { "Default": "Data Source=/var/lib/dtuserver/maxchemic.db" },
"PublicBaseUrl": "http://服务器公网IP:5000",       // 二维码和物模型里的地址都用它
"Mqtt": { "ClientSuffix": "platform-linux" }      // ★ 必须和 Windows 那台不同
```

## 6. 迁数据库

Windows 那台**先停服务**再拷，让 WAL 并回主库：

```bat
net stop DtuServer
```

拷 `maxchemic.db`（`-wal`/`-shm` 不用拷）到服务器，然后：

```bash
sudo mv ~/maxchemic.db /var/lib/dtuserver/
sudo chown dtuserver:dtuserver /var/lib/dtuserver/maxchemic.db
sudo chmod 640 /var/lib/dtuserver/maxchemic.db
```

全新部署跳过。首启会自己建库并播种管理员，临时密码打在日志里：

```bash
journalctl -u dtuserver | grep 临时管理员密码
```

## 7. 装服务

```bash
sudo cp deploy/dtuserver.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now dtuserver
sudo systemctl status dtuserver
journalctl -u dtuserver -f
```

日志里应看到 Kestrel 监听、DTU 监听 9116；`Mqtt:Enabled=true` 时还有 `MQTT 已连接` + 四条订阅。

| 症状 | 原因 |
|---|---|
| `203/EXEC` | 忘了 `chmod +x` |
| 卡在 `Starting...` 到超时 | `UseSystemd()` 没生效，unit 里 `Type=notify` 改 `Type=simple` |
| `Couldn't find a valid ICU package` | 没装 libicu |
| `unable to open database file` | `/var/lib/dtuserver` 属主不对，或连接串写的相对路径 |
| `FormatException ... appsettings` | JSON 写坏了，检查 Production 文件的逗号括号 |

## 8. 防火墙

```bash
sudo ufw allow 9116/tcp        # DTU，必须开
sudo ufw allow 5000/tcp        # 网页，换成你定的端口
sudo ufw allow 9080/tcp        # 桌面端直连才需要
sudo ufw enable
```

**云厂商安全组要单独再放一遍**，ufw 放行不等于云上放行。出站要允许到阿里云 MQTT 的 1883。

> ⚠️ **没有 HTTPS 时，登录密码和 API 令牌是明文过网的。** 建议在云安全组里把网页端口限制到你们公司出口 IP，只留 9116 对全网开放（DTU 从蜂窝网络连进来，源 IP 不固定）。

## 9. 时区

```bash
sudo timedatectl set-timezone Asia/Shanghai
```

代码里时间戳基本是 UTC，但日志和 DTU 连接时长用本地时间，时区不对看日志会很别扭。

---

## 验证清单

| 查什么 | 命令 / 位置 | 期望 |
|---|---|---|
| 服务活着 | `systemctl status dtuserver` | `active (running)` |
| 端口在听 | `ss -tlnp \| grep -E '5000\|9116'` | 都在 |
| 库可写 | `ls -la /var/lib/dtuserver/` | 有 `-wal`/`-shm`，属主 dtuserver |
| 网页 | `http://IP:5000/` | 能登录 |
| 中文正常 | 设备列表 | 不是乱码 |
| 静态资源 | F12 Network | 没有 404（Linux 区分大小写） |
| 物模型模板 | `http://IP:5000/templates/HT2000_Full_Config_v2.1.jsonc` | 能下载 |
| MQTT | `http://IP:5000/admin/mqtt-status` | `connected: true` |
| 二维码 | 设备详情点二维码 | 链接是 `http://IP:5000/bind.html?code=...` |
| DTU | 等设备重连 | 设备总览变在线 |
| 优雅停机 | `systemctl stop dtuserver` | 30 秒内停掉，不是被 SIGKILL |

## 更新

Windows 上重新 `deploy\publish-linux.bat`，上传新包，然后：

```bash
sudo systemctl stop dtuserver
sudo rm -rf /opt/dtuserver/wwwroot          # 删掉重铺，避免留下已改名/删除的旧文件
sudo tar -xzf ~/DtuServer-linux.tar.gz -C /opt/dtuserver
sudo chmod +x /opt/dtuserver/MaxChemical.DtuServer
sudo systemctl start dtuserver
```

`appsettings.Production.json` 和 `/var/lib/dtuserver` 不在包里，不会被覆盖。

## 备份

```bash
sudo apt install -y sqlite3
sudo tee /etc/cron.daily/dtuserver-backup >/dev/null <<'EOF'
#!/bin/sh
D=/var/backups/dtuserver; mkdir -p $D
sqlite3 /var/lib/dtuserver/maxchemic.db ".backup '$D/maxchemic-$(date +%F).db'"
find $D -name 'maxchemic-*.db' -mtime +30 -delete
EOF
sudo chmod +x /etc/cron.daily/dtuserver-backup
```

用 `.backup` 不是 `cp` —— 运行中的库直接 cp 会拷到不一致的状态。

---

## RHEL / CentOS / 阿里云 Linux 差异

```bash
sudo dnf install -y libicu
sudo firewall-cmd --permanent --add-port={5000,9116,9080}/tcp && sudo firewall-cmd --reload
```

SELinux 为 Enforcing 时，非标准端口要放行：

```bash
sudo semanage port -a -t http_port_t -p tcp 5000
```

---

## 附录 A：以后要域名 + HTTPS

`deploy/nginx-dtuserver.conf` 是配好的反代（含 SignalR 的 WebSocket 升级和长连接超时）。届时：

1. 域名解析到服务器
2. `apt install nginx certbot`，按 conf 里的注释签证书
3. Kestrel 的 Http 改成只监听 `127.0.0.1:5000`
4. `PublicBaseUrl` 改成 `https://域名`
5. 安全组关掉 5000，只留 80/443/9116

程序里已经有 `UseForwardedHeaders`，反代下登录跳转和二维码链接不会掉回 http。**9116 仍然直连，不进 nginx。**

## 附录 B：⚠️ DTU 里配的是 IP 还是域名

- **域名** → 改 DNS 即可，设备自己重连。
- **写死的公网 IP** → **每台 DTU 都要现场改配置**，这是迁移最大的工作量。

第二种建议：新服务器先跑起来，拿一台设备改配置试通，再分批改；或者把弹性公网 IP 从老服务器解绑重绑到新服务器，设备完全无感。

切换后 Windows 那台建议留一周再下线，随时能切回去。
