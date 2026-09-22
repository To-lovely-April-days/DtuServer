using System;
using System.Text.RegularExpressions;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>
    /// 平台接入 MQTT 的配置(appsettings.json 的 "Mqtt" 节)。
    /// Enabled=false(默认)时整个 MQTT 链路不启动 —— 原有 DTU 透传链路完全不受影响。
    /// </summary>
    public class MqttOptions
    {
        /// <summary>总开关。不填 = 关,老部署升级上来什么都不会变。</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>接入点,如 mqtt-cn-xxxxx.mqtt.aliyuncs.com。</summary>
        public string Broker { get; set; } = "";

        /// <summary>1883 明文 / 8883 TLS。</summary>
        public int Port { get; set; } = 1883;

        public bool UseTls { get; set; } = false;

        /// <summary>aliyun = 阿里云消息队列MQTT版签名模式;basic = 普通用户名密码(自建 EMQX/Mosquitto)。</summary>
        public string AuthMode { get; set; } = "aliyun";

        // ---- aliyun 模式 ----
        /// <summary>阿里云 MQTT 实例 ID,如 mqtt-cn-xxxxx。</summary>
        public string InstanceId { get; set; } = "";

        /// <summary>平台侧使用的 GroupID(需在阿里云控制台创建),如 GID_SERVER。</summary>
        public string GroupId { get; set; } = "GID_SERVER";

        /// <summary>clientId 的后半段:完整 clientId = {GroupId}@@@{ClientSuffix}。多实例部署要各起各的,不能重。</summary>
        public string ClientSuffix { get; set; } = "platform";

        public string AccessKey { get; set; } = "";
        public string SecretKey { get; set; } = "";

        // ---- basic 模式 ----
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public int KeepAliveSeconds { get; set; } = 60;

        /// <summary>下发指令后等回执的超时(毫秒)。</summary>
        public int CommandTimeoutMs { get; set; } = 10000;

        /// <summary>多久没收到任何上报就判离线(毫秒)。对应物模型里的 modbus.offlineTimeoutMs。</summary>
        public int OfflineTimeoutMs { get; set; } = 180000;

        /// <summary>断线重连退避:起始/上限(毫秒)。</summary>
        public int ReconnectInitialDelayMs { get; set; } = 5000;
        public int ReconnectMaxDelayMs { get; set; } = 120000;

        /// <summary>Topic 模板。必须与物模型 mqttTopics 保持一致,否则平台订不到设备发的消息。</summary>
        public MqttTopicTemplates Topics { get; set; } = new();

        /// <summary>
        /// 排障用:额外订阅一层通配(如 device/#)并把收到的每条消息的 Topic 打进日志。
        /// 设备明明发了、平台却收不到时打开它,就能看见消息实际落在哪个 Topic 上
        /// —— 通常是多了或少了一层前缀。查完记得关掉,不然日志会很吵。
        /// </summary>
        public bool DebugLogAllTopics { get; set; } = false;
    }

    /// <summary>Topic 模板。{deviceId} / {productKey} 是占位符。</summary>
    public class MqttTopicTemplates
    {
        public string DeviceData { get; set; } = "device/{deviceId}/data";
        public string Command { get; set; } = "device/{deviceId}/command";
        public string CommandReply { get; set; } = "device/{deviceId}/command/reply";
        public string Alarm { get; set; } = "device/{deviceId}/alarm";
        public string Online { get; set; } = "device/{deviceId}/online";
        public string ConfigUpdate { get; set; } = "config/update/{productKey}";

        /// <summary>把模板里的占位符换成实际值。</summary>
        public static string Fill(string template, string? deviceId = null, string? productKey = null) =>
            template.Replace("{deviceId}", deviceId ?? "+").Replace("{productKey}", productKey ?? "+");

        /// <summary>
        /// 把模板变成订阅用的通配符 Topic:device/{deviceId}/data → device/+/data。
        /// </summary>
        public static string ToWildcard(string template) => Fill(template);

        /// <summary>
        /// 取模板里第一个占位符之前的固定前缀,拼成兜底通配。
        /// device/{deviceId}/data → device/#   排障时用它把这一支下面的消息全收上来看。
        /// </summary>
        public static string ToRootWildcard(string template)
        {
            var i = template.IndexOf('{');
            var head = (i < 0 ? template : template[..i]).Trim('/');
            return string.IsNullOrEmpty(head) ? "#" : head + "/#";
        }

        /// <summary>
        /// 由模板生成"从实际 Topic 里抠出 deviceId"的正则。
        /// device/{deviceId}/data → ^device/([^/]+)/data$
        /// 占位符以外的部分全部转义,避免 Topic 里的特殊字符被当成正则。
        /// </summary>
        public static Regex ToExtractor(string template, string placeholder = "{deviceId}")
        {
            var parts = template.Split(new[] { placeholder }, StringSplitOptions.None);
            var pattern = string.Join("([^/]+)", Array.ConvertAll(parts, Regex.Escape));
            return new Regex("^" + pattern + "$", RegexOptions.Compiled);
        }
    }
}
