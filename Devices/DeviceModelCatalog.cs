namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 设备型号:一个命名的「通讯档案(ProfileKey) + 控制面板(PanelUrl)」搭配。
    /// 设备表的 DeviceType 字段存的就是型号 Key(因此不需要新增数据库列)。
    /// </summary>
    public record DeviceModel(string Key, string Name, string ProfileKey, string PanelUrl);

    /// <summary>
    /// 预定义型号目录:加/改型号 = 改下面这张表一行。
    ///  - 同一 ProfileKey 可被多个型号复用 → 同通讯、不同面板。
    ///  - 同一 PanelUrl 可被多个型号复用 → 不同通讯、同面板。
    /// 前后端共用同一份真相源:后端据 ProfileKey 选读写档案,前端据 PanelUrl 选弹出页面。
    /// </summary>
    public class DeviceModelCatalog
    {
        private static readonly DeviceModel[] Models =
        {
            new("HighPreactor_ModbusRTU", "高压反应釜(标准面板)", "HighPreactor_ModbusRTU", "/reactor.html"),
            new("TwoLiquidOneGasFeedSystem_ModbusRTU", "两液一气进料系统", "TwoLiquidOneGasFeedSystem_ModbusRTU", "/feedsys.html"),
            new("HighTempFurnace_ModbusRTU", "高温炉(管式·宇电AI)", "HighTempFurnace_ModbusRTU", "/furnace.html"),
            new("SiliconCarbideChip_ModbusRTU", "碳化硅反应器(六联罐进料)", "SiliconCarbideChip_ModbusRTU", "/sicchip.html"),
            // 占位型号:尚未集成控制面板的设备选它 —— 不进控制面板(PanelUrl 为空 = 列表里不可点)。
            new("__nopanel__", "暂未集成面板", "", ""),

            // —— 扩展示例(取消注释即生效)——
            // 同样的反应釜通讯,换一套面板:
            // new("HighPreactor_Compact", "高压反应釜(精简面板)", "HighPreactor_ModbusRTU", "/reactor-compact.html"),
            // 另一种设备:新通讯 + 新面板:
            // new("Dryer_ModbusRTU", "真空干燥箱", "Dryer_ModbusRTU", "/dryer.html"),
        };

        private readonly Dictionary<string, DeviceModel> _byKey =
            new(StringComparer.OrdinalIgnoreCase);

        public DeviceModelCatalog()
        {
            foreach (var m in Models) _byKey[m.Key] = m;
        }

        /// <summary>按型号 Key 取型号;未登记返回 null。</summary>
        public DeviceModel? Get(string? key) =>
            !string.IsNullOrEmpty(key) && _byKey.TryGetValue(key!, out var m) ? m : null;

        public IReadOnlyList<DeviceModel> All => Models;
    }
}
