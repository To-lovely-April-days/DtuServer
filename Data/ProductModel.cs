using System;

namespace MaxChemical.DtuServer.Data
{
    /// <summary>
    /// 物模型(产品)。一个 ProductKey = 一份完整的设备配置规范,
    /// 网关端按它跑 Modbus 采集/下控,APP 端按它动态渲染界面 —— 两端靠 identifier 对齐。
    ///
    /// ConfigJson 存的就是导出 .jsonc 的完整内容(结构 1:1),
    /// 下面那几个列只是从里面抽出来方便列表查询/展示的冗余字段。
    /// </summary>
    public class ProductModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>产品唯一标识,如 HT2000。MQTT 配置下发 Topic(config/update/{productKey})用它。</summary>
        public string ProductKey { get; set; } = "";

        /// <summary>型号名称,如 "HT-2000 双釜搅拌反应器"。</summary>
        public string ModelName { get; set; } = "";

        /// <summary>型号版本(语义化),如 2.1.0。改数据契约时手动升,给人看。</summary>
        public string ModelVersion { get; set; } = "1.0.0";

        public string Description { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Photo { get; set; } = "";

        /// <summary>完整配置(mqtt/mqttTopics/meta/dataFields/serviceCommands/alarmEvents/modbus/gateway/display/commands)。</summary>
        public string ConfigJson { get; set; } = "{}";

        /// <summary>
        /// 每次保存自增。给机器看:网关/APP 拿它跟本地缓存比,不等就重新拉配置
        /// (对应配置里的 meta.cachePolicy.checkVersionOnLogin)。
        /// </summary>
        public int Revision { get; set; } = 1;

        /// <summary>draft(草稿,可随便改) / published(已发布,设备在用)。</summary>
        public string Status { get; set; } = ModelStatuses.Draft;

        /// <summary>最近一次发布时间(发布 = 置为 published 并往 config/update 推变更通知)。</summary>
        public DateTime? PublishedAt { get; set; }

        /// <summary>发布时的 Revision。用于判断草稿相对已发布版本有没有未发布的改动。</summary>
        public int PublishedRevision { get; set; } = 0;

        public string CreatedByUserId { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class ModelStatuses
    {
        public const string Draft = "draft";
        public const string Published = "published";
    }

    /// <summary>
    /// MQTT 设备上报的告警记录。存库(而不是只放内存),否则重启就丢 —— 告警不留痕等于没接。
    /// </summary>
    public class DeviceAlarm
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>设备标识码(= MQTT Topic 里的 deviceId,= Device.Code)。</summary>
        public string DeviceCode { get; set; } = "";

        /// <summary>告警事件 identifier,对应物模型 alarmEvents[].identifier。</summary>
        public string Event { get; set; } = "";

        /// <summary>告警级别:info / warn / alert / error(取自物模型,原样透传)。</summary>
        public string Level { get; set; } = "";

        /// <summary>告警参数原文(JSON),按 alarmEvents[].params 的 identifier 组织。</summary>
        public string ParamsJson { get; set; } = "{}";

        /// <summary>设备上报的时间戳(毫秒)。设备没带就用服务器时间。</summary>
        public long Timestamp { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
