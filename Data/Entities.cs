using System;

namespace MaxChemical.DtuServer.Data
{
    /// <summary>平台用户(自助注册)。每个用户持有一对 API Key/Secret 供其 App 调用开放 API。</summary>
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";

        // 网站登录密码(PBKDF2 哈希,不存明文)
        public string PasswordHash { get; set; } = "";
        public string PasswordSalt { get; set; } = "";

        /// <summary>角色:Admin(平台管理员,可加设备/授权) / User(普通用户)。</summary>
        public string Role { get; set; } = "User";

        // 开放 API 凭证:ApiKey 是公开标识(可见),Secret 只在创建时显示一次,库里只存哈希。
        public string ApiKey { get; set; } = "";
        public string ApiSecretHash { get; set; } = "";
        public string ApiSecretSalt { get; set; } = "";

        public bool Disabled { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>设备接入方式。两条链路并存,互不影响。</summary>
    public static class AccessModes
    {
        /// <summary>透传模式(原有链路):DTU 用登录包(序列号)连 TCP,服务端只做字节透传+帧组装,
        /// 桌面端经 SignalR /dtuhub 中转,网页按内置型号档案(IDeviceProfile)读写。老设备默认走这条。</summary>
        public const string Passthrough = "Passthrough";

        /// <summary>MQTT 模式(新增链路):设备接入网关自己跑 Modbus 并经 MQTT 上报,
        /// 平台按物模型(ProductModel)解析数据、下发指令,APP 按同一份物模型动态渲染。</summary>
        public const string Mqtt = "Mqtt";

        public static bool IsMqtt(string? mode) =>
            string.Equals(mode, Mqtt, StringComparison.OrdinalIgnoreCase);

        /// <summary>归一化:空/未知一律当作透传,保证老数据行为不变。</summary>
        public static string Normalize(string? mode) => IsMqtt(mode) ? Mqtt : Passthrough;
    }

    /// <summary>逻辑设备。一台设备 = 一个对外标识码 + 后端(DTU序列号 + Modbus站号 + 设备类型)。</summary>
    public class Device
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>对外设备标识码(贴在设备上的二维码内容),如 MX-7K3Q-9F2A。
        /// MQTT 模式下它同时就是 MQTT Topic 里的 {deviceId}。</summary>
        public string Code { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>设备类型(对应 P3 的寄存器模板 key,如 HighPreactor_ModbusRTU)。</summary>
        public string DeviceType { get; set; } = "";

        /// <summary>该设备所在 DTU 的序列号(登录包)。</summary>
        public string DtuSerial { get; set; } = "";

        /// <summary>RS485 总线上的 Modbus 站号。</summary>
        public int ModbusStation { get; set; } = 1;

        /// <summary>接入方式:Passthrough(透传,默认) / Mqtt。见 <see cref="AccessModes"/>。</summary>
        public string AccessMode { get; set; } = AccessModes.Passthrough;

        /// <summary>MQTT 模式下所属物模型的 ProductKey(如 HT2000);透传模式为空。</summary>
        public string? ProductKey { get; set; }

        public string CreatedByUserId { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>设备图片(可选,二进制)。</summary>
        public byte[]? ImageData { get; set; }
        public string? ImageMime { get; set; }
    }

    /// <summary>授权关系:某用户对某设备的权限(监控/控制)。一对(用户,设备)唯一。</summary>
    public class Grant
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserId { get; set; } = "";
        public string DeviceId { get; set; } = "";

        /// <summary>可监控(读)。</summary>
        public bool CanMonitor { get; set; } = true;

        /// <summary>可控制(写)。默认 false,需管理员显式开。</summary>
        public bool CanControl { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
