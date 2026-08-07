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

    /// <summary>逻辑设备。一台设备 = 一个对外标识码 + 后端(DTU序列号 + Modbus站号 + 设备类型)。</summary>
    public class Device
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>对外设备标识码(贴在设备上的二维码内容),如 MX-7K3Q-9F2A。</summary>
        public string Code { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>设备类型(对应 P3 的寄存器模板 key,如 HighPreactor_ModbusRTU)。</summary>
        public string DeviceType { get; set; } = "";

        /// <summary>该设备所在 DTU 的序列号(登录包)。</summary>
        public string DtuSerial { get; set; } = "";

        /// <summary>RS485 总线上的 Modbus 站号。</summary>
        public int ModbusStation { get; set; } = 1;

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
