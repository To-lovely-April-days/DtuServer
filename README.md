# MaxChemical.DtuServer（自建云服务器 / DTU 网关）

服务端并行跑**两条互不影响的设备接入链路**：

| | 透传模式（原有） | MQTT 网关模式（新增） |
|---|---|---|
| 设备怎么连 | DTU 用**登录包(序列号)**连 TCP 9116 | 接入网关连阿里云 MQTT |
| 服务端干什么 | **只做字节透传 + 帧组装** | 按物模型解析上报、下发指令 |
| 协议逻辑在哪 | MaxChemic 桌面端 | 网关本地（按 registerMap 跑 Modbus）|
| 设备类型怎么加 | 改代码（`DeviceModelCatalog` + `IDeviceProfile` + 面板 HTML）| 网页上配物模型，零代码 |
| 网页入口 | `/devices.html` 设备管理 | `/gateways.html` 网关设备 |

> 透传链路一行代码都没动。`Mqtt:Enabled=false`（默认）时 MQTT 服务空转，老部署升级上来行为完全不变。

## 职责
- **DTU 网关**：TCP 监听（默认 9116），多台 DTU 主动连入，按**登录包(序列号)**路由。
- **SignalR 隧道**（`/dtuhub`）：桌面 MaxChemic 连进来，把某设备的 Modbus 字节经云转发到对应 DTU，原路返回应答。
- 设备协议逻辑（CRC/解析）仍在 MaxChemic 桌面端，本服务只做**字节透传 + 帧组装**。
- **物模型平台**：网页上配置设备的数据字段/指令/告警/Modbus 映射/APP 界面，生成网关与 APP 共用的 `.jsonc` 规范文件。
- **MQTT 接入**：订阅设备上报（数据/告警/上下线/指令回执），下发控制指令与配置变更。

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

---

# 物模型 + MQTT 网关（新增链路）

## 一句话流程

```
网页配物模型 ──► 生成 .jsonc ──┬─► 网页下载，手动烧进网关
   /models.html               ├─► 网关 HTTP 拉:  GET /api/config/device/{设备标识码}
                              ├─► APP  HTTP 拉:  GET /api/v1/config/{productKey}  (Bearer)
                              └─► 发布时 MQTT 推: config/update/{productKey}
```

## 物模型（`/models.html`）

一个 **productKey** = 一份完整配置，结构与导出的 `.jsonc` 1:1：

| 区块 | 内容 | 谁看 |
|---|---|---|
| Part A | `mqtt` 接入点、`mqttTopics` 六个 Topic 模板 | 两端 |
| Part B | `dataFields` / `serviceCommands` / `alarmEvents` —— **identifier 是贯穿全文的关联 key** | 两端 |
| Part C | `meta`、`modbus`（ports / registerMap / writeMap）、`gateway` | 网关 |
| Part D | `display`（分组+控件）、`commands`（分组+按钮） | APP |
| Part E | 每种消息的收发示例 | 两端 |

**Part E 不入库**，导出时按 Part B 现推导 —— 数据契约一改，示例自动跟着变，不会留旧值。

编辑器提供：
- 导入现成的 `.jsonc`（手上已有的配置直接传进来接着改）
- **按数据契约自动生成**：一键铺出整套 APP 界面配置（按 dataType 选控件、枚举生成状态灯配色、指令生成表单）
- **按字段/指令补齐**：registerMap、writeMap 缺哪条补哪条，只剩地址要人填
- 实时校验（见下）
- 生成文件预览 + 原始 JSON 直改

## 校验

校验的重点是**交叉引用** —— 手写 JSON 最容易错的地方：

- `modbus.registerMap[].identifier` 必须在 `dataFields` 里存在
- `modbus.writeMap[].serviceIdentifier` 必须在 `serviceCommands` 里存在；`writes[].inputIdentifier(s)` 必须是那条指令的入参
- `display` / `commands` 里绑的每个 identifier 都要能对上 Part B
- 告警的 `triggerConditions.field`、`portId` 引用、功能码取值、`registerCount` 与批量入参个数是否一致…

三档严重级别：

| 级别 | 含义 | 行为 |
|---|---|---|
| `fatal` | identifier 重名/非法、JSON 解析不了 | **存不进去**，必须先修 |
| `error` | 交叉引用对不上 | 能存草稿，**不许发布** |
| `warn` | 字段没配采集地址、指令入参没有输入控件… | 只提示 |

## 网关设备（`/gateways.html`）

- 添加设备 = 填名称 + 选物模型，系统生成**设备标识码**
- 这个标识码同时就是 MQTT Topic 里的 `{deviceId}`，也是二维码内容、扫码绑定用的码
- `/panel.html?code=xxx`：**按物模型动态渲染**的通用面板（实时数据 + 指令下发 + 告警记录），
  既是网页端的设备面板，也是给 APP 团队的一份参考实现

## 配置（appsettings.json）

```jsonc
"Mqtt": {
  "Enabled": false,                 // 总开关。false = MQTT 链路完全不启动
  "Broker": "mqtt-cn-xxxxx.mqtt.aliyuncs.com",
  "Port": 1883,                     // TLS 用 8883 并把 UseTls 设为 true
  "UseTls": false,
  "AuthMode": "aliyun",             // aliyun = 阿里云签名; basic = 用户名密码(自建 EMQX)
  "InstanceId": "mqtt-cn-xxxxx",
  "GroupId": "GID_SERVER",          // 平台侧 GroupID,需在阿里云控制台创建
  "ClientSuffix": "platform",       // clientId = {GroupId}@@@{ClientSuffix},多实例部署不能重
  "AccessKey": "", "SecretKey": "",
  "CommandTimeoutMs": 10000,        // 下发指令等回执的超时
  "OfflineTimeoutMs": 180000,       // 多久没收到上报判离线
  "Topics": { ... }                 // 必须与物模型里的 mqttTopics 一致,否则平台订不到
},
"GatewayConfigPull": {
  "AccessToken": ""                 // 网关拉配置的令牌,经 X-Access-Token 头传;留空=不校验
}
```

平台订阅 `device/+/data`、`device/+/alarm`、`device/+/online`、`device/+/command/reply`
（用 `Topics` 里的模板把 `{deviceId}` 换成 `+`）。阿里云侧要先创建这些 Topic 并给 GroupID 授权。

物模型编辑器的 MQTT 页会实时比对模板与平台配置，不一致会直接标红提示。

## 数据库

SQLite 单文件，启动时自动轻量迁移，**不删库、不动老表数据**：

- 新表 `ProductModels`（物模型）、`DeviceAlarms`（告警记录，落库才不会重启就丢）
- `Devices` 加两列：`AccessMode`（默认 `Passthrough`，老设备自动归到透传）、`ProductKey`（可空）

`EnsureCreated` 对已存在的库不建新表，所以 `DbSeeder` 里用 `CREATE TABLE IF NOT EXISTS` + `PRAGMA table_info` 判断后 `ALTER` —— 反复启动幂等。

## 主要端点

| 端点 | 鉴权 | 用途 |
|---|---|---|
| `GET/POST/PUT/DELETE /admin/models[/{key}]` | Admin | 物模型 CRUD |
| `POST /admin/models/import` | Admin | 导入 `.jsonc` |
| `GET /admin/models/{key}/export.jsonc` | Admin | 下载规范文件 |
| `GET /admin/models/{key}/preview` | Admin | 预览生成结果 |
| `POST /admin/models/{key}/publish` | Admin | 发布 + MQTT 推变更 |
| `GET /admin/mqtt-status` | Admin | MQTT 连接状态诊断 |
| `GET/POST/PUT /admin/mqtt-devices[/{id}]` | Admin | 网关设备管理 |
| `GET /web/models/{productKey}` | 登录 | 面板渲染用的物模型（已剥掉 accessKey/secretKey）|
| `GET /web/mqtt/devices/{code}/telemetry` | 监控权限 | 最新上报值 |
| `POST /web/devices/{code}/command` | 控制权限 | 多参数指令（MQTT 走 MQTT，透传回落到内置档案）|
| `GET /web/devices/{code}/alarms` | 监控权限 | 告警记录 |
| `GET /api/config/device/{code}` | 可选令牌 | **网关**按序列号拉配置 |
| `GET /api/v1/config/{productKey}` | Bearer | **APP** 拉配置 |

配置拉取支持 `?revision=N`：跟当前版本一致直接回 `304`，对应 `meta.cachePolicy.checkVersionOnLogin`。

## 还没做的

- 遥测历史入库与曲线查询（`display.historyChart` 的配置已经能配、能导出，平台侧只存最新值）
- 物模型版本历史与回滚（当前只有自增的 `revision`）
