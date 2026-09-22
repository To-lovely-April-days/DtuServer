# Linux 部署

目标系统：Ubuntu 22.04/24.04 或 Debian 12。RHEL/CentOS 系的差异在末尾单列。

**布局**

| 路径 | 内容 | 属主 | 权限 |
|---|---|---|---|
| `/opt/dtuserver` | 程序 + wwwroot | `root` | 服务只读 |
| `/var/lib/dtuserver` | SQLite 库 | `dtuserver` | 服务可写 |
| `/opt/dtuserver/appsettings.Production.json` | 密钥 | `root:dtuserver` | `640` |

程序目录只读是刻意的：被入侵也改不了程序本身。SQLite 单独放可写目录 —— 注意 SQLite 要在库文件**同目录**建 `-wal`/`-shm`，所以给的是目录写权限，光给文件写权限不够。

**端口**

| 端口 | 谁在听 | 对外 |
|---|---|---|
| 80 / 443 | nginx | 开放 |
| 5000 | Kestrel（仅回环） | 不开放 |
| 9116 | DTU 裸 TCP | **开放** |
| 9080 | 桌面端 SignalR | 看桌面端怎么配 |

> **9116 不能走 nginx** —— 它是裸 TCP，不是 HTTP。DTU 直连服务器这个端口。

---

## 1. 装依赖

```bash
sudo apt update
sudo apt install -y git nginx libicu-dev

# .NET 8 SDK（Ubuntu 24.04 / Debian 12 官方源就有）
sudo apt install -y dotnet-sdk-8.0
dotnet --version     # 应输出 8.0.x
```

> `libicu` 是硬依赖。缺了程序会直接崩在 `Couldn't find a valid ICU package`，
> 而且这个项目全是中文，别用 `InvariantGlobalization` 绕过去。
>
> Ubuntu 22.04 若源里没有 dotnet-sdk-8.0，先加微软源：
> ```bash
> wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
> sudo dpkg -i packages-microsoft-prod.deb && sudo apt update
> ```

## 2. 建账号和目录

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin dtuserver
sudo mkdir -p /opt/dtuserver /var/lib/dtuserver /var/www/certbot
sudo chown dtuserver:dtuserver /var/lib/dtuserver
sudo chmod 750 /var/lib/dtuserver
```

## 3. 拉代码并编译

```bash
sudo mkdir -p /usr/local/src && cd /usr/local/src
sudo git clone https://github.com/To-lovely-April-days/DtuServer.git
cd DtuServer
sudo git checkout claude/device-gateway-file-generation-34y6z0

sudo dotnet publish -c Release -o /opt/dtuserver
sudo chmod +x /opt/dtuserver/MaxChemical.DtuServer
```

编译报错就停下来看错误，别往下走。

> **不想在服务器上装 SDK**，可以在 Windows 上交叉发布再上传：
> ```bat
> dotnet publish -c Release -r linux-x64 --self-contained true -o D:\Publish\DtuServer-linux
> ```
> 自包含产物不需要服务器装 .NET，但**仍然需要 libicu**。上传后照样要 `chmod +x`。

## 4. 配密钥

```bash
sudo cp /usr/local/src/DtuServer/deploy/appsettings.Production.json.example \
        /opt/dtuserver/appsettings.Production.json
sudo nano /opt/dtuserver/appsettings.Production.json      # 按注释填

sudo chown root:dtuserver /opt/dtuserver/appsettings.Production.json
sudo chmod 640 /opt/dtuserver/appsettings.Production.json
```

`640` = root 可改、服务账号可读、其他人看不见。里面有阿里云 AK/SK。

⚠️ **`Mqtt:ClientSuffix` 必须和 Windows 那台不一样**。阿里云同一个 clientId 只允许一个连接，撞了两边互相踢，表现是「连上又断、断了又连」。

## 5. 迁数据库

Windows 那台**先停服务**，再拷：

```bat
net stop DtuServer
```

拷 `maxchemic.db` 到服务器（`-wal` / `-shm` 不用拷，停机时数据已经并回主库）。然后：

```bash
sudo mv ~/maxchemic.db /var/lib/dtuserver/
sudo chown dtuserver:dtuserver /var/lib/dtuserver/maxchemic.db
sudo chmod 640 /var/lib/dtuserver/maxchemic.db
```

全新部署跳过这步，程序首启会自己建库并播种管理员（临时密码打在日志里，`journalctl -u dtuserver | grep 临时管理员密码`）。

## 6. 装服务

```bash
sudo cp /usr/local/src/DtuServer/deploy/dtuserver.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now dtuserver
sudo systemctl status dtuserver
journalctl -u dtuserver -f
```

日志里应该看到 Kestrel 监听 5000、DTU 监听 9116；`Mqtt:Enabled=true` 的话还有 `MQTT 已连接` + 四条订阅。

> 卡在 `Starting...` 直到超时 → `UseSystemd()` 没生效。把 unit 里的 `Type=notify` 改成 `Type=simple`，`daemon-reload` 后重启。

## 7. 证书和 nginx

```bash
sudo apt install -y certbot
sudo cp /usr/local/src/DtuServer/deploy/nginx-dtuserver.conf /etc/nginx/sites-available/dtuserver
sudo nano /etc/nginx/sites-available/dtuserver          # 把域名换成你的
sudo ln -sf /etc/nginx/sites-available/dtuserver /etc/nginx/sites-enabled/
sudo rm -f /etc/nginx/sites-enabled/default
```

首签证书要先让 80 能通。**先把 conf 里 443 那整个 server 块注释掉**（证书还不存在，nginx 会起不来），然后：

```bash
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/certbot -d cloud.sh-htlab.com
```

拿到证书后取消 443 块的注释：

```bash
sudo nginx -t && sudo systemctl reload nginx
```

续期自动跑（certbot 装了 systemd timer），但要让 nginx 重载新证书：

```bash
echo -e '#!/bin/sh\nsystemctl reload nginx' | sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh
sudo certbot renew --dry-run
```

## 8. 防火墙

```bash
sudo ufw allow 80,443,9116/tcp
# 桌面端直连 9080 才需要
sudo ufw allow 9080/tcp
sudo ufw enable
```

**云厂商安全组要单独再放一遍**，ufw 放行不等于云上放行。出站要允许到阿里云 MQTT 的 1883。

## 9. 时区

```bash
sudo timedatectl set-timezone Asia/Shanghai
```

代码里时间戳基本都是 UTC，但日志和 DTU 连接时长用的是本地时间，时区不对看日志会很别扭。

---

## 验证清单

| 查什么 | 命令 / 位置 | 期望 |
|---|---|---|
| 服务活着 | `systemctl status dtuserver` | `active (running)` |
| 端口在听 | `ss -tlnp \| grep -E '5000\|9116\|9080'` | 三个都在 |
| 库可写 | `ls -la /var/lib/dtuserver/` | 有 `-wal`/`-shm`，属主 dtuserver |
| 网页 | 浏览器开 `https://域名/` | 能登录，不报证书错 |
| 转发头生效 | 登录后看地址栏 | 还是 https，没掉回 http |
| 中文正常 | 设备列表 | 不是乱码/问号 |
| 静态资源 | F12 Network | 没有 404（Linux 区分大小写） |
| 物模型模板 | `https://域名/templates/HT2000_Full_Config_v2.1.jsonc` | 能下载不是 404 |
| MQTT | `https://域名/admin/mqtt-status` | `connected: true` |
| DTU | 等设备重连 | 设备总览里变在线 |
| 优雅停机 | `systemctl stop dtuserver` | 30 秒内停掉，不是被 SIGKILL |

## 更新

```bash
cd /usr/local/src/DtuServer && sudo git pull
sudo systemctl stop dtuserver
sudo dotnet publish -c Release -o /opt/dtuserver
sudo chmod +x /opt/dtuserver/MaxChemical.DtuServer
sudo systemctl start dtuserver
```

`appsettings.Production.json` 和 `/var/lib/dtuserver` 不在发布产物里，不会被覆盖。

## 备份

```bash
sudo tee /etc/cron.daily/dtuserver-backup >/dev/null <<'EOF'
#!/bin/sh
D=/var/backups/dtuserver; mkdir -p $D
sqlite3 /var/lib/dtuserver/maxchemic.db ".backup '$D/maxchemic-$(date +%F).db'"
find $D -name 'maxchemic-*.db' -mtime +30 -delete
EOF
sudo chmod +x /etc/cron.daily/dtuserver-backup
sudo apt install -y sqlite3
```

用 `.backup` 不是 `cp` —— 运行中的库直接 cp 会拷到不一致的状态。

---

## RHEL / CentOS / 阿里云 Linux 差异

```bash
sudo dnf install -y dotnet-sdk-8.0 nginx git libicu
sudo firewall-cmd --permanent --add-port={80,443,9116}/tcp && sudo firewall-cmd --reload
# SELinux 默认禁止 nginx 对外连接，不开这个反代会 502
sudo setsebool -P httpd_can_network_connect 1
```

---

## ⚠️ 切换前必须确认：DTU 里配的是 IP 还是域名

- **域名** → 改 DNS 解析即可，设备会自己重连到新服务器。
- **写死的公网 IP** → **每台 DTU 都要现场改配置**，这是整个迁移最大的工作量和风险。

第二种情况建议：新服务器先跑起来，用一台设备改配置试通，再分批改；或者把公网 IP 迁到新服务器（同云同可用区通常支持弹性 IP 解绑重绑），设备完全无感。

切换期间 Windows 那台建议保留一周再下线，随时能改回去。
