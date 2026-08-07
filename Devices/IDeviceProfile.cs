using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 设备类型"档案":封装某一类设备的读测点 / 下控制逻辑(协议细节)。
    /// 新增一种设备 = 实现一个 IDeviceProfile 并注册;API 按 deviceType 自动分发。
    /// </summary>
    public interface IDeviceProfile
    {
        /// <summary>类型键,与设备的 DeviceType 对应(如 HighPreactor_ModbusRTU)。</summary>
        string TypeKey { get; }

        /// <summary>读取实时测点。返回 测点键→值 的字典(已按工程单位缩放)。</summary>
        Task<Dictionary<string, object>> ReadTelemetryAsync(
            DtuServerManager mgr, string dtuSerial, byte station, CancellationToken ct);

        /// <summary>下发控制命令。command=命令键,args=参数。成功返回 true。</summary>
        Task<bool> WriteControlAsync(
            DtuServerManager mgr, string dtuSerial, byte station,
            string command, Dictionary<string, double> args, CancellationToken ct);
    }

    /// <summary>按 deviceType 查找设备档案。</summary>
    public class DeviceProfileRegistry
    {
        private readonly Dictionary<string, IDeviceProfile> _byType =
            new(StringComparer.OrdinalIgnoreCase);

        public DeviceProfileRegistry(IEnumerable<IDeviceProfile> profiles)
        {
            foreach (var p in profiles) _byType[p.TypeKey] = p;
        }

        public IDeviceProfile? Get(string? deviceType)
            => !string.IsNullOrEmpty(deviceType) && _byType.TryGetValue(deviceType, out var p) ? p : null;
    }
}
