using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.DtuServer.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>某台 MQTT 设备的运行时快照(内存)。</summary>
    public class MqttDeviceState
    {
        public string DeviceId { get; init; } = "";
        public string? ProductKey { get; set; }
        public bool Online { get; set; }

        /// <summary>最近一次收到任何消息的时间。用来兜底判离线。</summary>
        public DateTime? LastSeenUtc { get; set; }

        /// <summary>最近一次收到数据上报的时间。</summary>
        public DateTime? LastDataUtc { get; set; }

        /// <summary>设备上报的时间戳(毫秒),原样透传给前端。</summary>
        public long LastTimestamp { get; set; }

        public string? FirmwareVersion { get; set; }

        /// <summary>最新测点值。整体替换,不做原地修改 —— 读的时候不用加锁。</summary>
        public IReadOnlyDictionary<string, JsonNode?> Values { get; set; } =
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
    }

    /// <summary>下发指令的结果。Result=0 成功(与物模型 outputParams.result 的约定一致)。</summary>
    public record MqttCommandResult(bool Ok, int Result, string Message, string CommandId);

    /// <summary>
    /// 平台的 MQTT 接入端。订阅设备上报(数据/告警/上线/指令回执),下发控制指令和配置变更。
    ///
    /// 跟 <see cref="DtuServerManager"/> 完全平行、互不引用:
    /// DTU 透传链路(序列号登录包 + 字节透传)一行代码都没动,
    /// 这里是给「设备接入网关 + 手机APP」那条新链路用的。
    /// Mqtt:Enabled=false 时本服务直接空转,老部署升级上来零影响。
    /// </summary>
    public class MqttGatewayService : IHostedService, IDisposable
    {
        private readonly MqttOptions _opt;
        private readonly ILogger<MqttGatewayService> _logger;
        private readonly IServiceScopeFactory _scopes;

        private IMqttClient? _client;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private Task? _sweep;

        private readonly ConcurrentDictionary<string, MqttDeviceState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>commandId → 等回执的调用方。</summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MqttCommandResult>> _pending =
            new(StringComparer.Ordinal);

        // Topic 模板 → 抠 deviceId 的正则(启动时编译一次)
        private readonly System.Text.RegularExpressions.Regex _reData;
        private readonly System.Text.RegularExpressions.Regex _reAlarm;
        private readonly System.Text.RegularExpressions.Regex _reOnline;
        private readonly System.Text.RegularExpressions.Regex _reReply;

        public MqttGatewayService(MqttOptions opt, ILogger<MqttGatewayService> logger, IServiceScopeFactory scopes)
        {
            _opt = opt;
            _logger = logger;
            _scopes = scopes;

            _reData = MqttTopicTemplates.ToExtractor(_opt.Topics.DeviceData);
            _reAlarm = MqttTopicTemplates.ToExtractor(_opt.Topics.Alarm);
            _reOnline = MqttTopicTemplates.ToExtractor(_opt.Topics.Online);
            _reReply = MqttTopicTemplates.ToExtractor(_opt.Topics.CommandReply);
        }

        /// <summary>MQTT 链路是否已启用(配置开关)。</summary>
        public bool Enabled => _opt.Enabled;

        /// <summary>当前与 Broker 的连接状态。</summary>
        public bool Connected => _client?.IsConnected == true;

        /// <summary>设备数据更新事件(设备标识码)。供看板 SignalR 推送。</summary>
        public event Action<string>? DeviceDataUpdated;

        /// <summary>设备上下线事件(设备标识码, 是否在线)。</summary>
        public event Action<string, bool>? DeviceOnlineChanged;

        // ==========================================================
        //  生命周期
        // ==========================================================

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_opt.Enabled)
            {
                _logger.LogInformation("MQTT 接入未启用(Mqtt:Enabled=false),仅 DTU 透传链路运行");
                return Task.CompletedTask;
            }
            if (string.IsNullOrWhiteSpace(_opt.Broker))
            {
                _logger.LogWarning("MQTT 接入已启用但未配置 Mqtt:Broker,跳过启动");
                return Task.CompletedTask;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
            // 离线判定单独跑一条,不依赖连接状态:Broker 断开时设备本来就收不到上报,
            // 那才是最该把它们判离线的时候,不能跟着连接循环一起停。
            _sweep = Task.Run(() => SweepLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            try
            {
                if (_client is { IsConnected: true })
                    await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
            }
            catch (Exception ex) { _logger.LogDebug("MQTT 断开时异常: {E}", ex.Message); }

            foreach (var t in new[] { _loop, _sweep })
            {
                if (t is null) continue;
                try { await t.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (Exception) { /* 关服时不纠结 */ }
            }
        }

        public void Dispose()
        {
            _cts?.Dispose();
            _client?.Dispose();
        }

        /// <summary>连接 → 订阅 → 掉线退避重连,直到进程退出。</summary>
        private async Task RunAsync(CancellationToken ct)
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnMessageAsync;

            int delay = _opt.ReconnectInitialDelayMs;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _client.ConnectAsync(BuildOptions(), ct);
                    _logger.LogInformation("MQTT 已连接 {Broker}:{Port}", _opt.Broker, _opt.Port);
                    await SubscribeAllAsync(ct);
                    delay = _opt.ReconnectInitialDelayMs;   // 连上了就把退避重置

                    // 连着就待命。离线判定不放这儿 —— 断连期间才最需要它,见 SweepLoopAsync。
                    while (!ct.IsCancellationRequested && _client.IsConnected)
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);

                    if (!ct.IsCancellationRequested)
                        _logger.LogWarning("MQTT 连接已断开,准备重连");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("MQTT 连接失败: {Err},{Delay}ms 后重试", ex.Message, delay);
                    if (ex.Message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase))
                        _logger.LogWarning(
                            "NotAuthorized 是 Broker 拒绝鉴权,按这个顺序查:" +
                            " ① Mqtt:GroupId 填的 Group 在控制台创建了没" +
                            " ② AccessKey/SecretKey 有没有粘错或粘进空格" +
                            " ③ 这个 RAM 用户有没有 MQTT 的访问权限(AliyunMQTTFullAccess)" +
                            " ④ InstanceId 是不是实例详情页那个 post-cn-xxxx");
                }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = Math.Min(delay * 2, _opt.ReconnectMaxDelayMs);
            }
        }

        private MqttClientOptions BuildOptions()
        {
            // 阿里云 MQTT: clientId = {GroupId}@@@{后缀},username = Signature|{AccessKey}|{InstanceId},
            //              password = Base64(HMAC-SHA1(SecretKey, clientId))
            string clientId, username, password;
            if (string.Equals(_opt.AuthMode, "aliyun", StringComparison.OrdinalIgnoreCase))
            {
                // 从控制台复制粘贴很容易带上首尾空格/换行,带进签名就必然 NotAuthorized,统一 Trim 掉
                var group = (_opt.GroupId ?? "").Trim();
                var suffix = (_opt.ClientSuffix ?? "").Trim();
                var ak = (_opt.AccessKey ?? "").Trim();
                var sk = (_opt.SecretKey ?? "").Trim();
                var inst = (_opt.InstanceId ?? "").Trim();

                clientId = $"{group}@@@{suffix}";
                username = $"Signature|{ak}|{inst}";
                password = SignAliyun(clientId, sk);

                // 连接失败时最需要看的就是这三样。AccessKey 只露头尾,不打全。
                _logger.LogInformation("MQTT 鉴权参数 clientId={ClientId} username=Signature|{Ak}|{Inst} (secretKey {SkLen} 位)",
                    clientId, Mask(ak), inst, sk.Length);
                if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk) || string.IsNullOrEmpty(inst))
                    _logger.LogWarning("MQTT 鉴权参数不完整:GroupId/AccessKey/SecretKey/InstanceId 都必须填");
            }
            else
            {
                clientId = string.IsNullOrWhiteSpace(_opt.ClientSuffix) ? "maxchemic-platform" : _opt.ClientSuffix.Trim();
                username = _opt.Username;
                password = _opt.Password;
            }

            var b = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(_opt.Broker, _opt.Port)
                .WithCredentials(username, password)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(10, _opt.KeepAliveSeconds)))
                .WithCleanSession(true);

            if (_opt.UseTls) b = b.WithTlsOptions(o => o.UseTls());

            return b.Build();
        }

        /// <summary>日志里只露 AccessKey 的头尾,中间打码。</summary>
        private static string Mask(string s) =>
            s.Length <= 8 ? new string('*', s.Length) : $"{s[..4]}****{s[^4..]}";

        /// <summary>阿里云 MQTT 签名:Base64(HMAC-SHA1(SecretKey, ClientID))。</summary>
        private static string SignAliyun(string clientId, string secretKey)
        {
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secretKey ?? ""));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(clientId)));
        }

        private async Task SubscribeAllAsync(CancellationToken ct)
        {
            // 只订阅"设备→平台"方向。command 是平台发的,不订;configUpdate 也是平台发的,不订。
            var topics = new[]
            {
                MqttTopicTemplates.ToWildcard(_opt.Topics.DeviceData),
                MqttTopicTemplates.ToWildcard(_opt.Topics.Alarm),
                MqttTopicTemplates.ToWildcard(_opt.Topics.Online),
                MqttTopicTemplates.ToWildcard(_opt.Topics.CommandReply),
            };

            // 排障模式:再订一层 device/# 把这一支下面的消息全收上来,看实际 Topic 长什么样
            if (_opt.DebugLogAllTopics)
                topics = topics.Append(MqttTopicTemplates.ToRootWildcard(_opt.Topics.DeviceData)).Distinct().ToArray();

            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var t in topics)
                builder = builder.WithTopicFilter(f => f.WithTopic(t).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));

            await _client!.SubscribeAsync(builder.Build(), ct);
            _logger.LogInformation("MQTT 已订阅: {Topics}", string.Join(", ", topics));
            if (_opt.DebugLogAllTopics)
                _logger.LogWarning("MQTT 排障模式已开启(Mqtt:DebugLogAllTopics=true),会打印每条消息的 Topic。查完记得关掉。");
        }

        // ==========================================================
        //  收消息
        // ==========================================================

        private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var raw = e.ApplicationMessage.Topic ?? "";
            if (_opt.DebugLogAllTopics) _logger.LogInformation("MQTT ← 收到消息 Topic={Topic}", raw);

            // MQTT 里 a/b/c 和 a/b/c/ 是两个不同的 Topic —— 末尾斜杠等于多了个空层级,
            // 模板匹配不上就整条丢掉。网关拼字符串时很容易多带一个,这种没必要丢数据。
            var topic = raw.TrimEnd('/');
            if (topic.Length != raw.Length)
                _logger.LogWarning("MQTT Topic 末尾多了斜杠,已按 {Topic} 处理(建议发布端去掉,原文: {Raw})", topic, raw);
            string payload;
            try { payload = e.ApplicationMessage.ConvertPayloadToString() ?? ""; }
            catch (Exception ex) { _logger.LogWarning("MQTT 载荷解码失败 {Topic}: {Err}", topic, ex.Message); return; }

            try
            {
                var m = _reData.Match(topic);
                if (m.Success) { HandleData(m.Groups[1].Value, payload); return; }

                m = _reOnline.Match(topic);
                if (m.Success) { HandleOnline(m.Groups[1].Value, payload); return; }

                m = _reReply.Match(topic);
                if (m.Success) { HandleReply(m.Groups[1].Value, payload); return; }

                m = _reAlarm.Match(topic);
                if (m.Success) { await HandleAlarmAsync(m.Groups[1].Value, payload); return; }

                // 收到了但对不上任何模板 —— 多半是 Topic 配错了,这种必须看得见,不能埋在 Debug 里
                _logger.LogWarning("MQTT 收到未识别的 Topic: {Topic}(与 appsettings 里 Mqtt:Topics 的模板都对不上)", raw);
            }
            catch (Exception ex)
            {
                // 一条坏消息不能把订阅整挂了
                _logger.LogWarning("MQTT 处理消息失败 {Topic}: {Err}", topic, ex.Message);
            }
        }

        private void HandleData(string deviceId, string payload)
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null) return;

            var st = _states.GetOrAdd(deviceId, id => new MqttDeviceState { DeviceId = id });
            bool wasOnline = st.Online;

            // 网关可能只报变化的字段(reportMode=change),所以要合并而不是整体替换。
            // 每次生成一份新字典再整体换上去,读端永远看到完整且一致的快照,不用加锁。
            var merged = new Dictionary<string, JsonNode?>(st.Values, StringComparer.Ordinal);
            if (root["data"] is JsonObject data)
                foreach (var kv in data)
                    merged[kv.Key] = kv.Value?.DeepClone();
            else
                // 没有 data 层就没有测点可取。设备会变在线但面板一片空白,
                // 不提示的话很难想到是 payload 结构不对(常见于把告警的 JSON 发到了 data 通道)。
                _logger.LogWarning("MQTT 设备 {Device} 的数据消息里没有 data 字段,收不到任何测点。" +
                    "数据上报的 payload 形如 {{\"deviceId\":\"...\",\"timestamp\":...,\"data\":{{...测点...}}}}", deviceId);

            var now = DateTime.UtcNow;
            st.Values = merged;
            st.LastDataUtc = now;
            st.LastSeenUtc = now;
            st.Online = true;
            if (Devices.ModelSpec.Str(root["productKey"]) is { Length: > 0 } pk) st.ProductKey = pk;
            if (Devices.ModelSpec.Num(root["timestamp"]) is { } ts) st.LastTimestamp = (long)ts;
            if (merged.TryGetValue("firmwareVersion", out var fw)) st.FirmwareVersion = Devices.ModelSpec.Str(fw);

            if (!wasOnline) Raise(DeviceOnlineChanged, deviceId, true);
            try { DeviceDataUpdated?.Invoke(deviceId); }
            catch (Exception ex) { _logger.LogDebug("DeviceDataUpdated 通知异常: {E}", ex.Message); }
        }

        private void HandleOnline(string deviceId, string payload)
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            var status = Devices.ModelSpec.Str(root?["status"]) ?? "online";
            bool online = !string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase);

            var st = _states.GetOrAdd(deviceId, id => new MqttDeviceState { DeviceId = id });
            bool wasOnline = st.Online;
            st.Online = online;
            st.LastSeenUtc = DateTime.UtcNow;
            if (Devices.ModelSpec.Str(root?["productKey"]) is { Length: > 0 } pk) st.ProductKey = pk;
            if (Devices.ModelSpec.Str(root?["firmwareVersion"]) is { Length: > 0 } fw) st.FirmwareVersion = fw;

            _logger.LogInformation("MQTT 设备 {Device} {Status}", deviceId, online ? "上线" : "下线");
            if (wasOnline != online) Raise(DeviceOnlineChanged, deviceId, online);
        }

        private void HandleReply(string deviceId, string payload)
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null) return;

            var st = _states.GetOrAdd(deviceId, id => new MqttDeviceState { DeviceId = id });
            st.LastSeenUtc = DateTime.UtcNow;

            var commandId = Devices.ModelSpec.Str(root["commandId"]);
            if (string.IsNullOrWhiteSpace(commandId)) return;
            if (!_pending.TryRemove(commandId, out var tcs)) return;   // 超时后到的回执,丢掉

            int result = (int)(Devices.ModelSpec.Num(root["result"]) ?? 0);
            var message = Devices.ModelSpec.Str(root["message"]) ?? "";
            tcs.TrySetResult(new MqttCommandResult(result == 0, result, message, commandId));
        }

        private async Task HandleAlarmAsync(string deviceId, string payload)
        {
            var root = JsonNode.Parse(payload) as JsonObject;
            if (root is null) return;

            var st = _states.GetOrAdd(deviceId, id => new MqttDeviceState { DeviceId = id });
            st.LastSeenUtc = DateTime.UtcNow;
            st.Online = true;

            var alarm = new DeviceAlarm
            {
                DeviceCode = deviceId,
                Event = Devices.ModelSpec.Str(root["event"]) ?? "",
                Level = Devices.ModelSpec.Str(root["level"]) ?? "alert",
                ParamsJson = (root["params"] ?? new JsonObject()).ToJsonString(Devices.ModelSpec.WriteOptions),
                Timestamp = (long)(Devices.ModelSpec.Num(root["timestamp"]) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            };

            _logger.LogInformation("MQTT 告警 {Device} {Event} ({Level})", deviceId, alarm.Event, alarm.Level);

            // 告警必须落库 —— 只放内存的话重启就丢,等于没接告警
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DeviceAlarms.Add(alarm);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("告警入库失败 {Device} {Event}: {Err}", deviceId, alarm.Event, ex.Message);
            }
        }

        /// <summary>不管连没连上 Broker,都定期扫一遍离线。</summary>
        private async Task SweepLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { break; }
                try { SweepOffline(); }
                catch (Exception ex) { _logger.LogDebug("离线扫描异常: {E}", ex.Message); }
            }
        }

        /// <summary>超过 OfflineTimeoutMs 没收到任何消息 → 判离线(设备被拔电时不会发遗嘱以外的东西)。</summary>
        private void SweepOffline()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(-_opt.OfflineTimeoutMs);
            foreach (var st in _states.Values)
            {
                if (!st.Online) continue;
                if (st.LastSeenUtc is not null && st.LastSeenUtc > deadline) continue;
                st.Online = false;
                _logger.LogInformation("MQTT 设备 {Device} 超时未上报,判为离线", st.DeviceId);
                Raise(DeviceOnlineChanged, st.DeviceId, false);
            }
        }

        private void Raise(Action<string, bool>? handler, string deviceId, bool online)
        {
            try { handler?.Invoke(deviceId, online); }
            catch (Exception ex) { _logger.LogDebug("DeviceOnlineChanged 通知异常: {E}", ex.Message); }
        }

        // ==========================================================
        //  查询
        // ==========================================================

        public bool IsOnline(string? deviceId) =>
            !string.IsNullOrWhiteSpace(deviceId) && _states.TryGetValue(deviceId, out var st) && st.Online;

        public MqttDeviceState? GetState(string? deviceId) =>
            string.IsNullOrWhiteSpace(deviceId) ? null : _states.TryGetValue(deviceId, out var st) ? st : null;

        /// <summary>把最新测点值组装成 JsonObject(每次新建,调用方随便用)。</summary>
        public JsonObject ValuesOf(string? deviceId)
        {
            var obj = new JsonObject();
            var st = GetState(deviceId);
            if (st is null) return obj;
            foreach (var kv in st.Values) obj[kv.Key] = kv.Value?.DeepClone();
            return obj;
        }

        // ==========================================================
        //  下发
        // ==========================================================

        /// <summary>
        /// 下发控制指令并等回执。设备在 CommandTimeoutMs 内没回 → 返回超时失败。
        /// 参数按物模型 serviceCommands[].inputParams 的 identifier 组织。
        /// </summary>
        public async Task<MqttCommandResult> SendCommandAsync(
            string deviceId, string command, JsonObject? parameters, CancellationToken ct)
        {
            if (!_opt.Enabled) return new MqttCommandResult(false, -1, "平台未启用 MQTT 接入", "");
            if (_client is not { IsConnected: true }) return new MqttCommandResult(false, -1, "平台与 MQTT Broker 未连接", "");

            var commandId = "cmd_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var tcs = new TaskCompletionSource<MqttCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[commandId] = tcs;
            try
            {
                // parameters 通常是从请求体里摘出来的节点,已经挂在别的父节点下。
                // JsonNode 只允许有一个父节点,直接赋值会抛 "The node already has a parent",必须克隆。
                var payload = new JsonObject
                {
                    ["commandId"] = commandId,
                    ["deviceId"] = deviceId,
                    ["command"] = command,
                    ["params"] = parameters?.DeepClone() ?? new JsonObject(),
                };

                await PublishAsync(MqttTopicTemplates.Fill(_opt.Topics.Command, deviceId),
                    payload.ToJsonString(Devices.ModelSpec.WriteOptions), ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_opt.CommandTimeoutMs);
                using (timeout.Token.Register(() => tcs.TrySetResult(
                    new MqttCommandResult(false, -1, $"设备未在 {_opt.CommandTimeoutMs}ms 内回执", commandId))))
                {
                    return await tcs.Task;
                }
            }
            catch (Exception ex)
            {
                return new MqttCommandResult(false, -1, $"指令下发失败: {ex.Message}", commandId);
            }
            finally
            {
                _pending.TryRemove(commandId, out _);
            }
        }

        /// <summary>
        /// 推送配置变更通知。网关/APP 订阅 config/update/{productKey},
        /// 收到后比对 revision,不一致就重新拉配置。
        /// </summary>
        public async Task<bool> PublishConfigUpdateAsync(
            string productKey, string modelVersion, int revision, CancellationToken ct)
        {
            if (_client is not { IsConnected: true }) return false;
            var payload = new JsonObject
            {
                ["productKey"] = productKey,
                ["modelVersion"] = modelVersion,
                ["revision"] = revision,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["action"] = "reload",
            };
            await PublishAsync(MqttTopicTemplates.Fill(_opt.Topics.ConfigUpdate, productKey: productKey),
                payload.ToJsonString(Devices.ModelSpec.WriteOptions), ct);
            _logger.LogInformation("已推送配置变更 {ProductKey} revision={Rev}", productKey, revision);
            return true;
        }

        private Task PublishAsync(string topic, string payload, CancellationToken ct) =>
            _client!.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);

        /// <summary>诊断用:当前有过消息往来的设备一览。</summary>
        public IReadOnlyList<object> Snapshot() => _states.Values
            .OrderBy(s => s.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(s => (object)new
            {
                deviceId = s.DeviceId,
                productKey = s.ProductKey,
                online = s.Online,
                lastSeen = s.LastSeenUtc?.ToString("o", CultureInfo.InvariantCulture),
                fieldCount = s.Values.Count,
            })
            .ToList();
    }
}
