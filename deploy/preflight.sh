#!/usr/bin/env bash
# ============================================================
#  DtuServer 部署前预检 —— 只读，不改系统任何东西。
#
#  用途：这台服务器可能已经跑着别的服务，先摸清占了哪些端口、
#        装了什么、防火墙什么状态，再定我们用哪几个端口。
#
#  用法：bash preflight.sh          （能 sudo 的话加 sudo，信息更全）
#        sudo bash preflight.sh 2>&1 | tee preflight.txt
# ============================================================
set -u

# 我们要用的端口：
#   9116 固定 —— DTU 设备里写死了，改它要每台设备现场改配置
#   其余是候选，脚本会告诉你哪些空着
FIXED_PORTS="9116"
CANDIDATE_HTTP="5000 5080 8080 8088 8090 18080 18088"
CANDIDATE_HUB="9080 9081 9090 19080"

hr() { printf '\n──────── %s ────────\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }

hr "系统"
if [ -r /etc/os-release ]; then . /etc/os-release; echo "发行版 : ${PRETTY_NAME:-未知}"; fi
echo "内核   : $(uname -r)  架构: $(uname -m)"
echo "主机名 : $(hostname)"
echo "时区   : $( (timedatectl show -p Timezone --value 2>/dev/null) || cat /etc/timezone 2>/dev/null || echo 未知)"
echo "内存   : $(free -h 2>/dev/null | awk '/^Mem:/{print $2" 总 / "$7" 可用"}')"
echo "磁盘   : $(df -h / 2>/dev/null | awk 'NR==2{print $2" 总 / "$4" 可用 / 已用 "$5}')"
echo "当前用户: $(id -un)  $( [ "$(id -u)" -eq 0 ] && echo '(root)' || echo '(非 root，部分信息可能看不到)')"

hr "所有监听端口"
if have ss; then
  ss -tlnp 2>/dev/null | awk 'NR==1{print;next}{print}' || ss -tln
elif have netstat; then
  netstat -tlnp 2>/dev/null || netstat -tln
else
  echo "！ss / netstat 都没有，装一个：apt install iproute2  或  dnf install iproute"
fi

hr "UDP 监听（少见但也可能撞）"
if have ss; then ss -ulnp 2>/dev/null | head -20; fi

# 取一份「已占用端口」清单，后面比对用。
# 关键：拿不到清单时必须说「判断不了」，绝不能默认报空闲 —— 那是最危险的假阴性。
CAN_SCAN=0
USED=""
if have ss || have netstat; then
  USED=$( { ss -tlnH 2>/dev/null || netstat -tln 2>/dev/null; } \
          | grep -oE '[:.]([0-9]{1,5})[[:space:]]' | tr -d ': \t' | sort -un )
  # 一个监听端口都没有 = 几乎不可能（至少有 sshd），说明命令没真正跑起来
  if [ -n "$USED" ]; then CAN_SCAN=1; fi
fi

check_port() {
  local p="$1"
  if [ "$CAN_SCAN" -eq 0 ]; then
    printf '  %-6s ？判断不了 —— 读不到监听端口列表，先装 iproute2 再跑一遍\n' "$p"
    return 0
  fi
  if echo "$USED" | grep -qx "$p"; then
    local who
    who=$( { ss -tlnpH 2>/dev/null || true; } | grep -E "[:.]$p " | grep -oE 'users:\(\("[^"]+"' | head -1 | sed 's/.*"//' )
    printf '  %-6s 【占用】 %s\n' "$p" "${who:-进程名看不到，用 sudo 再跑一次}"
    return 1
  fi
  printf '  %-6s  空闲\n' "$p"
  return 0
}

if [ "$CAN_SCAN" -eq 0 ]; then
  hr "！！端口占用无法判断！！"
  echo "  读不到监听端口列表，下面的端口检查全部无效。先装工具再跑一遍："
  echo "    Debian/Ubuntu: apt install -y iproute2"
  echo "    RHEL 系:       dnf install -y iproute"
fi

hr "必须用的端口（改不了）"
for p in $FIXED_PORTS; do
  check_port "$p" || echo "        ⚠️ 9116 被占了！DTU 设备里写死的就是这个端口，" \
                          "要么让占用它的服务挪窝，要么每台 DTU 现场改配置。"
done

hr "网页/API 端口候选"
for p in $CANDIDATE_HTTP; do check_port "$p"; done

hr "桌面端 SignalR 端口候选"
for p in $CANDIDATE_HUB; do check_port "$p"; done

hr "已装的 Web / 容器服务"
for s in nginx apache2 httpd caddy haproxy traefik docker containerd podman; do
  if have "$s"; then
    ver=$("$s" -v 2>&1 | head -1 || echo '')
    printf '  %-12s 已安装  %s\n' "$s" "$ver"
  fi
done
if have docker; then
  echo "  ── docker 容器及端口映射 ──"
  docker ps --format '    {{.Names}}  {{.Image}}  {{.Ports}}' 2>/dev/null || echo "    （读不到，需要 sudo 或用户不在 docker 组）"
fi

hr "正在运行的 systemd 服务"
if have systemctl; then
  systemctl list-units --type=service --state=running --no-pager --no-legend 2>/dev/null \
    | awk '{print "  "$1}' | head -40
  echo "  ── 是否已存在同名服务 ──"
  systemctl list-unit-files 2>/dev/null | grep -iE 'dtu|maxchem' || echo "    无（干净）"
fi

hr "防火墙"
if have ufw; then
  echo "── ufw ──"; ufw status verbose 2>/dev/null || echo "  （需要 sudo）"
fi
if have firewall-cmd; then
  echo "── firewalld ──"
  firewall-cmd --state 2>/dev/null
  firewall-cmd --list-all 2>/dev/null || echo "  （需要 sudo）"
fi
if ! have ufw && ! have firewall-cmd; then
  echo "── iptables ──"
  iptables -S 2>/dev/null | head -20 || echo "  （需要 sudo 或没装）"
fi
echo "⚠️ 云服务器还有一层「安全组」，和上面这些是独立的，两边都要放行。"

hr "SELinux / AppArmor"
if have getenforce; then echo "SELinux : $(getenforce)"; else echo "SELinux : 未安装"; fi
if have aa-status; then echo "AppArmor: $(aa-status --enabled 2>/dev/null && echo 启用 || echo 未启用)"; fi

hr ".NET 运行时"
if have dotnet; then
  echo "dotnet  : $(dotnet --version 2>/dev/null)"
  echo "── 已装 SDK ──";      dotnet --list-sdks 2>/dev/null | sed 's/^/  /'
  echo "── 已装运行时 ──";    dotnet --list-runtimes 2>/dev/null | grep -E 'AspNetCore|NETCore' | sed 's/^/  /'
else
  echo "未安装。装法见 deploy/README.md"
fi

hr "ICU（中文/全球化，硬依赖）"
if ldconfig -p 2>/dev/null | grep -q libicuuc; then
  ldconfig -p | grep libicuuc | head -2 | sed 's/^/  /'
else
  echo "  ！没有 libicu —— 程序会崩在 Couldn't find a valid ICU package"
  echo "   Debian/Ubuntu: apt install -y libicu-dev     RHEL 系: dnf install -y libicu"
fi

hr "会撞车的既有痕迹"
for p in /opt/dtuserver /var/lib/dtuserver /etc/systemd/system/dtuserver.service; do
  [ -e "$p" ] && echo "  已存在: $p" || true
done
id dtuserver >/dev/null 2>&1 && echo "  已存在用户: dtuserver" || echo "  用户 dtuserver 不存在（干净）"

hr "出站连通性（MQTT 要能连阿里云 1883）"
if have timeout && have bash; then
  for hp in "post-cn-cta4vnojp01.mqtt.aliyuncs.com 1883" "github.com 443"; do
    set -- $hp
    if timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null; then
      echo "  ✓ $1:$2 通"
    else
      echo "  ✗ $1:$2 不通（云安全组出站规则 / DNS / 该实例地址不对）"
    fi
  done
fi

hr "公网地址"
echo "本机网卡地址："
ip -4 -o addr show scope global 2>/dev/null | awk '{print "  "$2"  "$4}' || hostname -I
echo "出口公网 IP（没外网就取不到，正常）："
timeout 5 curl -s https://api.ipify.org 2>/dev/null | sed 's/^/  /' || echo "  取不到"

printf '\n════════ 预检结束，把以上全部内容发回 ════════\n'
