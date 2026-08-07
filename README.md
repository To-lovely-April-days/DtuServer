# MaxChemical.DtuServer（自建云服务器 / DTU 网关）

Phase 1：把 DTU 接入 + 桌面 MaxChemic 经云中转控制设备打通。
（看板/历史库=Phase 2；正式鉴权/断线告警/写幂等=Phase 3）

## 职责
- **DTU 网关**：TCP 监听（默认 9116），多台 DTU 主动连入，按**登录包(序列号)**路由。
- **SignalR 隧道**（`/dtuhub`）：桌面 MaxChemic 连进来，把某设备的 Modbus 字节经云转发到对应 DTU，原路返回应答。
- 设备协议逻辑（CRC/解析）仍在 MaxChemic 桌面端，本服务只做**字节透传 + 帧组装**。

## 配置（appsettings.json）
- `Kestrel:Endpoints:Http:Url`：HTTP/SignalR 监听地址（默认 `http://0.0.0.0:5000`）
- `DtuServer:ListenPort`：DTU 连入端口（默认 9116，需与 DTU 的「服务器端口号」一致）
- `Hub:AccessToken`：桌面接入令牌（**务必改掉**，桌面 `RemoteServerSettings.AccessToken` 要与之一致）
- `Dashboard:Password`：网页看板登录密码。**留空=不登录**（仅本地联调）；填非空则访问看板需登录。

## 运行
```bash
cd MaxChemical.DtuServer
dotnet run            # 或发布后部署到云服务器(Linux/Windows均可)
```
- DTU 配置：服务器地址=本服务公网IP、端口=9116、登录包=该设备唯一序列号。
- 浏览器访问 `http://服务器:5000/` 看在线 DTU；`/api/online` 返回在线序列号列表。

## 桌面端(MaxChemic)对接
- `DeviceConnectionConfig.RemoteServerSettings.ServerUrl = "http://服务器:5000/dtuhub"`
- `DeviceConnectionConfig.RemoteServerSettings.AccessToken = "与服务器一致的令牌"`
- 设备属性：通信方式选 `RemoteServer`，`DTU序列号` 填该设备 DTU 的登录包序列号。

## 端口
- `5000`：SignalR（桌面端连）
- `9116`：DTU 连入（蜂窝/公网）

两个端口都要在云服务器防火墙放行。
