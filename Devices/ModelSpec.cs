using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>一条校验问题。severity: fatal(必须先修好才能存) / error(能存不能发布) / warn(提示)。</summary>
    public record SpecIssue(string Severity, string Path, string Message);

    /// <summary>校验结果集合。</summary>
    public class SpecValidation
    {
        public List<SpecIssue> Issues { get; } = new();

        /// <summary>结构性错误:JSON 解析不了、identifier 非法或重名。这些一旦存进去后面全乱,直接拒。</summary>
        public bool HasFatal => Issues.Any(i => i.Severity == SpecSeverity.Fatal);

        /// <summary>含交叉引用错误(如 registerMap 指向了不存在的字段)。允许存草稿,但不许发布。</summary>
        public bool HasError => Issues.Any(i => i.Severity is SpecSeverity.Fatal or SpecSeverity.Error);

        public void Add(string severity, string path, string message) => Issues.Add(new SpecIssue(severity, path, message));
        public void Fatal(string path, string message) => Add(SpecSeverity.Fatal, path, message);
        public void Error(string path, string message) => Add(SpecSeverity.Error, path, message);
        public void Warn(string path, string message) => Add(SpecSeverity.Warn, path, message);
    }

    public static class SpecSeverity
    {
        public const string Fatal = "fatal";
        public const string Error = "error";
        public const string Warn = "warn";
    }

    /// <summary>
    /// 物模型规范:校验、导入(解析 .jsonc)、导出(生成带注释的 .jsonc)、空白模板。
    ///
    /// 存库的 ConfigJson 与导出文件结构 1:1,只有一个差别:
    /// Part E 的 mqttExample_* 报文示例不入库 —— 它们完全能从 Part B 推导出来,导出时现生成,
    /// 免得数据契约改了示例还停在旧值。
    /// </summary>
    public static class ModelSpec
    {
        /// <summary>identifier 命名规范:字母或下划线开头,后接字母数字下划线。两端代码里要当变量名/字典 key 用。</summary>
        private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly string[] DataTypes = { "float", "int", "bool", "enum", "string" };

        /// <summary>读功能码:1线圈 2离散量 3保持寄存器 4输入寄存器。</summary>
        private static readonly int[] ReadFunctionCodes = { 1, 2, 3, 4 };

        /// <summary>写功能码:5写单线圈 6写单寄存器 15写多线圈 16写多寄存器。</summary>
        private static readonly int[] WriteFunctionCodes = { 5, 6, 15, 16 };

        /// <summary>
        /// 导出/存库统一用这套序列化选项。
        /// UnsafeRelaxedJsonEscaping 是为了让中文和 ℃ 原样输出,不然满文件都是 温度 这种转义,人没法看。
        /// TypeInfoResolver 必须显式给:自建的 JsonSerializerOptions 一旦被首次使用就会转为只读,
        /// 此时没有解析器的话,序列化 JsonNode 会抛
        /// "JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only"。
        /// </summary>
        public static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        /// <summary>解析 .jsonc:允许 // 注释和尾逗号。</summary>
        private static readonly JsonDocumentOptions ParseOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 128,
        };

        // 顶层区块的固定顺序。导出时按这个顺序写,读起来跟规范文档对得上。
        private static readonly string[] SectionOrder =
        {
            "mqtt", "mqttTopics",                              // Part A
            "dataFields", "serviceCommands", "alarmEvents",    // Part B
            "meta", "modbus", "gateway",                       // Part C
            "display", "commands",                             // Part D
        };

        // ==========================================================
        //  解析 / 导入
        // ==========================================================

        /// <summary>
        /// 解析一份 .jsonc(或纯 .json)配置。失败返回 null 并给出原因。
        /// 用于:编辑器保存、导入现成文件。
        /// </summary>
        public static JsonObject? TryParse(string? text, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "配置内容为空"; return null; }
            try
            {
                var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: ParseOptions);
                if (node is not JsonObject obj) { error = "配置最外层必须是一个 JSON 对象 { }"; return null; }
                // JsonNode.Parse 是惰性的:重复的属性名不会在这里报错,而是等调用方第一次索引/枚举时
                // 由内部字典 Add 抛 ArgumentException —— 那时早已跑出这个 try,直接变成 500。
                // 所以返回前强制深度物化一次,把错误拦在这儿,变成正常的「解析失败」提示。
                Materialize(obj);
                return obj;
            }
            catch (JsonException ex)
            {
                error = $"JSON 解析失败: {ex.Message}";
                return null;
            }
            catch (ArgumentException ex)
            {
                // JsonObject 内部字典对重复属性名抛的就是 ArgumentException
                error = $"JSON 解析失败: 存在重复的属性名({ex.Message})。常见于复制粘贴整段后忘了改 key。";
                return null;
            }
        }

        /// <summary>深度遍历,把惰性的 JsonObject/JsonArray 全部物化,逼出重复键错误。
        /// 递归深度受 ParseOptions.MaxDepth(128) 约束,不会爆栈。</summary>
        private static void Materialize(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var kv in o) Materialize(kv.Value);   // 枚举即物化
                    break;
                case JsonArray a:
                    foreach (var item in a) Materialize(item);
                    break;
            }
        }

        /// <summary>
        /// 归一化:去掉导出时会重新生成的 Part E 示例,补齐缺失的顶层区块,并按固定顺序重排。
        /// 导入外部文件和编辑器保存都走这里,保证库里存的形状始终一致。
        /// </summary>
        public static JsonObject Normalize(JsonObject src)
        {
            var blank = BlankConfig();
            var result = new JsonObject();

            foreach (var key in SectionOrder)
            {
                var node = src[key];
                // 缺的区块用空白模板补上,省得前端到处判空
                result[key] = node is null ? blank[key]!.DeepClone() : node.DeepClone();
            }

            // 保留使用者自己加的非标准顶层字段(除了会重新生成的 mqttExample_*)
            foreach (var kv in src)
            {
                if (SectionOrder.Contains(kv.Key)) continue;
                if (kv.Key.StartsWith("mqttExample", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                result[kv.Key] = kv.Value?.DeepClone();
            }

            return result;
        }

        // ==========================================================
        //  校验
        // ==========================================================

        /// <summary>
        /// 全量校验。核心是交叉引用:Part C 的 Modbus 映射、Part D 的界面控件,
        /// 引用的 identifier 必须在 Part B 里真实存在 —— 这正是手写 JSON 最容易错的地方。
        /// </summary>
        public static SpecValidation Validate(JsonObject cfg)
        {
            var v = new SpecValidation();

            // ---------- Part B: dataFields ----------
            var fieldIds = new HashSet<string>(StringComparer.Ordinal);
            var fields = cfg["dataFields"] as JsonArray;
            if (fields is null || fields.Count == 0)
                v.Error("dataFields", "至少要定义一个数据字段");
            else
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var path = $"dataFields[{i}]";
                    var f = fields[i] as JsonObject;
                    if (f is null) { v.Fatal(path, "必须是对象"); continue; }

                    var id = Str(f["identifier"]);
                    if (string.IsNullOrWhiteSpace(id)) v.Fatal(path, "identifier 不能为空");
                    else if (!IdentifierPattern.IsMatch(id)) v.Fatal($"{path}.identifier", $"'{id}' 不合法:只能用字母/数字/下划线,且不能数字开头");
                    else if (!fieldIds.Add(id)) v.Fatal($"{path}.identifier", $"'{id}' 与前面的字段重名");

                    if (string.IsNullOrWhiteSpace(Str(f["name"]))) v.Warn($"{path}.name", $"字段 '{id}' 没填中文名,APP 上会显示 identifier");

                    var dt = Str(f["dataType"]);
                    if (string.IsNullOrWhiteSpace(dt)) v.Error($"{path}.dataType", $"字段 '{id}' 没指定数据类型");
                    else if (!DataTypes.Contains(dt)) v.Error($"{path}.dataType", $"字段 '{id}' 的数据类型 '{dt}' 不支持,可选: {string.Join(" / ", DataTypes)}");
                    else if (dt is "enum" && (f["options"] as JsonObject) is not { Count: > 0 })
                        v.Error($"{path}.options", $"枚举字段 '{id}' 必须给出 options 取值表");

                    var min = Num(f["min"]); var max = Num(f["max"]);
                    if (min is not null && max is not null && min >= max)
                        v.Error($"{path}", $"字段 '{id}' 的 min({min}) 必须小于 max({max})");
                }
            }

            // ---------- Part B: serviceCommands ----------
            var cmdIds = new HashSet<string>(StringComparer.Ordinal);
            // 命令 → 它的入参 identifier 集合,后面 writeMap / 界面配置都要拿它比对
            var cmdInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var cmds = cfg["serviceCommands"] as JsonArray ?? new JsonArray();
            for (int i = 0; i < cmds.Count; i++)
            {
                var path = $"serviceCommands[{i}]";
                var c = cmds[i] as JsonObject;
                if (c is null) { v.Fatal(path, "必须是对象"); continue; }

                var id = Str(c["identifier"]);
                if (string.IsNullOrWhiteSpace(id)) { v.Fatal(path, "identifier 不能为空"); continue; }
                if (!IdentifierPattern.IsMatch(id)) v.Fatal($"{path}.identifier", $"'{id}' 不合法:只能用字母/数字/下划线,且不能数字开头");
                else if (!cmdIds.Add(id)) v.Fatal($"{path}.identifier", $"'{id}' 与前面的指令重名");

                if (string.IsNullOrWhiteSpace(Str(c["name"]))) v.Warn($"{path}.name", $"指令 '{id}' 没填中文名");

                var inputs = new HashSet<string>(StringComparer.Ordinal);
                CheckParams(v, c["inputParams"] as JsonArray, $"{path}.inputParams", id, inputs);
                CheckParams(v, c["outputParams"] as JsonArray, $"{path}.outputParams", id, new HashSet<string>(StringComparer.Ordinal));
                cmdInputs[id] = inputs;
            }

            // ---------- Part B: alarmEvents ----------
            var alarmIds = new HashSet<string>(StringComparer.Ordinal);
            var alarms = cfg["alarmEvents"] as JsonArray ?? new JsonArray();
            for (int i = 0; i < alarms.Count; i++)
            {
                var path = $"alarmEvents[{i}]";
                var a = alarms[i] as JsonObject;
                if (a is null) { v.Fatal(path, "必须是对象"); continue; }

                var id = Str(a["identifier"]);
                if (string.IsNullOrWhiteSpace(id)) { v.Fatal(path, "identifier 不能为空"); continue; }
                if (!IdentifierPattern.IsMatch(id)) v.Fatal($"{path}.identifier", $"'{id}' 不合法");
                else if (!alarmIds.Add(id)) v.Fatal($"{path}.identifier", $"'{id}' 与前面的告警重名");

                // 触发条件绑定的字段必须存在,否则网关根本判不了
                var trigField = Str(Obj(a["triggerConditions"])?["field"]);
                if (!string.IsNullOrWhiteSpace(trigField) && !fieldIds.Contains(trigField))
                    v.Error($"{path}.triggerConditions.field", $"告警 '{id}' 的触发字段 '{trigField}' 在 dataFields 里不存在");

                CheckParams(v, a["params"] as JsonArray, $"{path}.params", id, new HashSet<string>(StringComparer.Ordinal));
            }

            // ---------- Part C: modbus ----------
            var portIds = new HashSet<string>(StringComparer.Ordinal);
            var modbus = cfg["modbus"] as JsonObject;
            if (modbus is not null)
            {
                var ports = modbus["ports"] as JsonArray ?? new JsonArray();
                for (int i = 0; i < ports.Count; i++)
                {
                    var pid = Str(Obj(ports[i])?["portId"]);
                    if (string.IsNullOrWhiteSpace(pid)) v.Error($"modbus.ports[{i}].portId", "端口必须有 portId");
                    else if (!portIds.Add(pid)) v.Error($"modbus.ports[{i}].portId", $"端口 '{pid}' 重复");
                }

                // registerMap:采集哪个寄存器 → 填到哪个 dataField
                var mapped = new HashSet<string>(StringComparer.Ordinal);
                var regMap = modbus["registerMap"] as JsonArray ?? new JsonArray();
                for (int i = 0; i < regMap.Count; i++)
                {
                    var path = $"modbus.registerMap[{i}]";
                    var r = regMap[i] as JsonObject;
                    if (r is null) { v.Error(path, "必须是对象"); continue; }

                    var id = Str(r["identifier"]);
                    if (string.IsNullOrWhiteSpace(id)) v.Error($"{path}.identifier", "没指定要写入哪个数据字段");
                    else if (!fieldIds.Contains(id)) v.Error($"{path}.identifier", $"'{id}' 在 dataFields 里不存在");
                    else if (!mapped.Add(id)) v.Warn($"{path}.identifier", $"字段 '{id}' 被映射了多次,后面的会覆盖前面的");

                    if (Num(r["address"]) is null) v.Error($"{path}.address", $"字段 '{id}' 没填寄存器地址");

                    var fc = (int?)Num(r["functionCode"]);
                    if (fc is null) v.Error($"{path}.functionCode", $"字段 '{id}' 没填功能码");
                    else if (!ReadFunctionCodes.Contains(fc.Value))
                        v.Error($"{path}.functionCode", $"字段 '{id}' 的读功能码 {fc} 不合法,应为 1/2/3/4");

                    CheckPortRef(v, Str(r["portId"]), portIds, $"{path}.portId");
                }

                // 定义了却没人采的字段:不算错(可能由别的途径填),但值得提醒
                foreach (var id in fieldIds.Where(x => !mapped.Contains(x)))
                    v.Warn("modbus.registerMap", $"字段 '{id}' 没有配置 Modbus 采集地址,网关不会上报它");

                // writeMap:APP 的一条指令 → 网关要写哪几个寄存器
                var wired = new HashSet<string>(StringComparer.Ordinal);
                var writeMap = modbus["writeMap"] as JsonArray ?? new JsonArray();
                for (int i = 0; i < writeMap.Count; i++)
                {
                    var path = $"modbus.writeMap[{i}]";
                    var w = writeMap[i] as JsonObject;
                    if (w is null) { v.Error(path, "必须是对象"); continue; }

                    var sid = Str(w["serviceIdentifier"]);
                    if (string.IsNullOrWhiteSpace(sid)) { v.Error($"{path}.serviceIdentifier", "没指定对应哪条指令"); continue; }
                    if (!cmdIds.Contains(sid)) { v.Error($"{path}.serviceIdentifier", $"指令 '{sid}' 在 serviceCommands 里不存在"); continue; }
                    if (!wired.Add(sid)) v.Error($"{path}.serviceIdentifier", $"指令 '{sid}' 配置了多条写映射,网关只会执行第一条");

                    var inputs = cmdInputs.TryGetValue(sid, out var s) ? s : new HashSet<string>(StringComparer.Ordinal);
                    var writes = w["writes"] as JsonArray;
                    if (writes is null || writes.Count == 0) { v.Error($"{path}.writes", $"指令 '{sid}' 没配置任何写操作"); continue; }

                    for (int j = 0; j < writes.Count; j++)
                    {
                        var wp = $"{path}.writes[{j}]";
                        var one = writes[j] as JsonObject;
                        if (one is null) { v.Error(wp, "必须是对象"); continue; }

                        if (Num(one["address"]) is null) v.Error($"{wp}.address", $"指令 '{sid}' 的第 {j + 1} 条写操作没填地址");

                        var fc = (int?)Num(one["functionCode"]);
                        if (fc is null) v.Error($"{wp}.functionCode", $"指令 '{sid}' 的第 {j + 1} 条写操作没填功能码");
                        else if (!WriteFunctionCodes.Contains(fc.Value))
                            v.Error($"{wp}.functionCode", $"写功能码 {fc} 不合法,应为 5/6/15/16");

                        CheckPortRef(v, Str(one["portId"]), portIds, $"{wp}.portId");

                        // 单参数写:inputIdentifier 必须是这条指令的入参
                        var inId = Str(one["inputIdentifier"]);
                        if (!string.IsNullOrWhiteSpace(inId) && !inputs.Contains(inId))
                            v.Error($"{wp}.inputIdentifier", $"'{inId}' 不是指令 '{sid}' 的入参");

                        // FC16 批量写:inputIdentifiers 逐个校验,并跟 registerCount 对齐
                        var multi = one["inputIdentifiers"] as JsonArray;
                        if (multi is not null)
                        {
                            foreach (var m in multi)
                            {
                                var mid = Str(m);
                                if (!string.IsNullOrWhiteSpace(mid) && !inputs.Contains(mid))
                                    v.Error($"{wp}.inputIdentifiers", $"'{mid}' 不是指令 '{sid}' 的入参");
                            }
                            var count = (int?)Num(one["registerCount"]);
                            if (count is not null && count != multi.Count)
                                v.Error($"{wp}.registerCount", $"registerCount({count}) 与 inputIdentifiers 个数({multi.Count})对不上");
                        }

                        // 说了取自入参,却没说取哪个
                        if (string.Equals(Str(one["valueFrom"]), "input", StringComparison.OrdinalIgnoreCase)
                            && string.IsNullOrWhiteSpace(inId) && multi is null)
                            v.Error(wp, $"指令 '{sid}' 的第 {j + 1} 条写操作 valueFrom=input,但没指定 inputIdentifier(s)");

                        // 既不取自入参也没给常量值 → 网关不知道该写什么
                        if (string.IsNullOrWhiteSpace(Str(one["valueFrom"])) && Num(one["value"]) is null
                            && string.IsNullOrWhiteSpace(inId) && multi is null)
                            v.Error(wp, $"指令 '{sid}' 的第 {j + 1} 条写操作既没有固定 value,也没有 inputIdentifier");
                    }
                }

                // 有指令但没写映射 → 网关收到也不知道干嘛
                foreach (var id in cmdIds.Where(x => !wired.Contains(x)))
                    v.Warn("modbus.writeMap", $"指令 '{id}' 没有配置 Modbus 写映射,网关收到后无法执行");
            }

            // ---------- Part D: display ----------
            var display = cfg["display"] as JsonObject;
            if (display is not null)
            {
                var groups = display["groups"] as JsonArray ?? new JsonArray();
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var gFields = Arr(Obj(groups[gi])?["fields"]) ?? new JsonArray();
                    for (int fi = 0; fi < gFields.Count; fi++)
                    {
                        var path = $"display.groups[{gi}].fields[{fi}]";
                        var f = gFields[fi] as JsonObject;
                        if (f is null) continue;

                        // "_" 开头是合成控件(如 _flags_table),本身不对应单个数据字段
                        var id = Str(f["identifier"]);
                        if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("_", StringComparison.Ordinal)
                            && !fieldIds.Contains(id))
                            v.Error($"{path}.identifier", $"控件绑定的字段 '{id}' 在 dataFields 里不存在");

                        var conf = f["config"] as JsonObject;
                        if (conf is null) continue;

                        // PV/SV 双值仪表盘
                        CheckFieldRef(v, Str(conf["pvIdentifier"]), fieldIds, $"{path}.config.pvIdentifier");
                        CheckFieldRef(v, Str(conf["svIdentifier"]), fieldIds, $"{path}.config.svIdentifier");

                        // 开关控件挂的下控指令
                        var sid = Str(conf["serviceIdentifier"]);
                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            if (!cmdIds.Contains(sid))
                                v.Error($"{path}.config.serviceIdentifier", $"控件绑定的指令 '{sid}' 在 serviceCommands 里不存在");
                            else
                            {
                                var inId = Str(conf["inputIdentifier"]);
                                if (!string.IsNullOrWhiteSpace(inId) && !cmdInputs[sid].Contains(inId))
                                    v.Error($"{path}.config.inputIdentifier", $"'{inId}' 不是指令 '{sid}' 的入参");
                            }
                        }

                        // 表格控件的每一行也绑字段
                        foreach (var (row, ri) in (Arr(conf["rows"]) ?? new JsonArray()).Select((x, ix) => (x, ix)))
                            CheckFieldRef(v, Str(Obj(row)?["identifier"]), fieldIds, $"{path}.config.rows[{ri}].identifier");
                    }
                }

                foreach (var (ch, ci) in (Arr(Obj(display["historyChart"])?["fields"]) ?? new JsonArray()).Select((x, ix) => (x, ix)))
                    CheckFieldRef(v, Str(ch), fieldIds, $"display.historyChart.fields[{ci}]");
            }

            // ---------- Part D: commands ----------
            var commands = cfg["commands"] as JsonObject;
            if (commands is not null)
            {
                var groups = commands["groups"] as JsonArray ?? new JsonArray();
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var items = Arr(Obj(groups[gi])?["items"]) ?? new JsonArray();
                    for (int ii = 0; ii < items.Count; ii++)
                    {
                        var path = $"commands.groups[{gi}].items[{ii}]";
                        var it = items[ii] as JsonObject;
                        if (it is null) continue;

                        var sid = Str(it["serviceIdentifier"]);
                        if (string.IsNullOrWhiteSpace(sid)) { v.Error($"{path}.serviceIdentifier", "按钮没绑定指令"); continue; }
                        if (!cmdIds.Contains(sid)) { v.Error($"{path}.serviceIdentifier", $"按钮绑定的指令 '{sid}' 在 serviceCommands 里不存在"); continue; }

                        var inputs = cmdInputs[sid];
                        var inputFields = it["inputFields"] as JsonArray ?? new JsonArray();
                        for (int fi = 0; fi < inputFields.Count; fi++)
                        {
                            var inId = Str(Obj(inputFields[fi])?["identifier"]);
                            if (!string.IsNullOrWhiteSpace(inId) && !inputs.Contains(inId))
                                v.Error($"{path}.inputFields[{fi}].identifier", $"'{inId}' 不是指令 '{sid}' 的入参");
                        }

                        // 界面上少配的入参,APP 发指令时会缺参数
                        foreach (var need in inputs)
                            if (!inputFields.Any(x => Str(Obj(x)?["identifier"]) == need))
                                v.Warn($"{path}.inputFields", $"指令 '{sid}' 的入参 '{need}' 在界面上没有对应输入控件");

                        CheckFieldRef(v, Str(Obj(it["disableWhen"])?["field"]), fieldIds, $"{path}.disableWhen.field");
                    }
                }
            }

            CheckQrCode(v, cfg);
            return v;
        }

        /// <summary>
        /// 二维码模板检查。模板坏了不会让保存失败,但二维码会静默退回成纯序列号
        /// (扫出来是一串字符而不是绑定链接),所以这里必须提示,否则很难发现。
        /// </summary>
        private static void CheckQrCode(SpecValidation v, JsonObject cfg)
        {
            if (Obj(Obj(cfg["meta"])?["qrCode"]) is not JsonObject qr) return;
            if (!string.Equals(Str(qr["format"]) ?? "url", "url", StringComparison.OrdinalIgnoreCase)) return;

            const string path = "meta.qrCode.urlTemplate";
            var tpl = (Str(qr["urlTemplate"]) ?? "").Trim();
            if (tpl.Length == 0)
            {
                v.Warn(path, "没填扫码链接模板,二维码会退回成纯序列号(APP 仍可用,但微信扫不出可点的链接)");
                return;
            }

            // 占位符必须能被平台填上,否则生成的链接是坏的 —— 那种情况下二维码会退回成纯序列号
            var unknown = PlaceholderPattern.Matches(tpl)
                .Select(m => m.Groups[1].Value)
                .Where(n => n is not ("deviceId" or "gatewayId" or "code" or "productKey" or "pk"))
                .Distinct().ToList();
            if (unknown.Count > 0)
                v.Warn(path, $"模板里的占位符 {string.Join("、", unknown.Select(n => "{" + n + "}"))} 平台填不上," +
                             "二维码会退回成纯序列号。可用的是 {deviceId}、{productKey}");

            if (!tpl.Contains("{deviceId}") && !tpl.Contains("{gatewayId}") && !tpl.Contains("{code}"))
                v.Warn(path, "模板里没有 {deviceId},所有设备会生成同一个二维码");

            if (Uri.TryCreate(tpl, UriKind.Absolute, out var abs) && IsSampleHost(abs.Host))
                v.Warn(path, $"还是示例域名 {abs.Host},点「用平台配置填充」换成你自己的平台地址");
        }

        private static void CheckParams(SpecValidation v, JsonArray? ps, string path, string ownerId, HashSet<string> collect)
        {
            if (ps is null) return;
            for (int i = 0; i < ps.Count; i++)
            {
                var id = Str(Obj(ps[i])?["identifier"]);
                if (string.IsNullOrWhiteSpace(id)) { v.Error($"{path}[{i}].identifier", $"'{ownerId}' 的参数 identifier 不能为空"); continue; }
                if (!IdentifierPattern.IsMatch(id)) v.Error($"{path}[{i}].identifier", $"'{ownerId}' 的参数 '{id}' 命名不合法");
                else if (!collect.Add(id)) v.Error($"{path}[{i}].identifier", $"'{ownerId}' 的参数 '{id}' 重名");
            }
        }

        private static void CheckFieldRef(SpecValidation v, string? id, HashSet<string> fieldIds, string path)
        {
            if (!string.IsNullOrWhiteSpace(id) && !fieldIds.Contains(id))
                v.Error(path, $"引用的字段 '{id}' 在 dataFields 里不存在");
        }

        private static void CheckPortRef(SpecValidation v, string? portId, HashSet<string> portIds, string path)
        {
            if (portIds.Count > 0 && !string.IsNullOrWhiteSpace(portId) && !portIds.Contains(portId))
                v.Error(path, $"端口 '{portId}' 在 modbus.ports 里没有定义");
        }

        // ==========================================================
        //  空白模板
        // ==========================================================

        /// <summary>新建模型时的空白骨架:所有顶层区块都在,值给成能直接跑的默认值。</summary>
        public static JsonObject BlankConfig(string productKey = "", string modelName = "")
        {
            var cfg = new JsonObject
            {
                ["mqtt"] = new JsonObject
                {
                    ["broker"] = "mqtt-cn-xxxxx.mqtt.aliyuncs.com",
                    ["port"] = 1883,
                    ["clientIdPrefix"] = "GID_DEVICE",
                    ["groupId"] = "GID_DEVICE",
                    ["instanceId"] = "mqtt-cn-xxxxx",
                    ["accessKey"] = "从服务端动态获取",
                    ["secretKey"] = "从服务端动态获取",
                    ["keepAlive"] = 60,
                    ["cleanSession"] = true,
                },
                ["mqttTopics"] = new JsonObject
                {
                    ["deviceData"] = "device/{deviceId}/data",
                    ["command"] = "device/{deviceId}/command",
                    ["commandReply"] = "device/{deviceId}/command/reply",
                    ["alarm"] = "device/{deviceId}/alarm",
                    ["online"] = "device/{deviceId}/online",
                    ["configUpdate"] = "config/update/{productKey}",
                },
                ["dataFields"] = new JsonArray(),
                ["serviceCommands"] = new JsonArray(),
                ["alarmEvents"] = new JsonArray(),
                ["meta"] = new JsonObject
                {
                    ["productKey"] = productKey,
                    ["modelName"] = modelName,
                    ["modelVersion"] = "1.0.0",
                    ["icon"] = "",
                    ["description"] = "",
                    ["manufacturer"] = "",
                    ["photo"] = "",
                    ["cachePolicy"] = new JsonObject { ["appCacheTTLMs"] = 86400000, ["checkVersionOnLogin"] = true },
                    ["qrCode"] = new JsonObject
                    {
                        ["format"] = "url",
                        ["urlTemplate"] = "https://app.xxx.com/bind?gw={gatewayId}&pk={productKey}",
                        ["fields"] = new JsonArray("gatewayId", "productKey"),
                    },
                },
                ["modbus"] = new JsonObject
                {
                    ["ports"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["portId"] = "port1", ["interface"] = "/dev/ttyS1", ["protocol"] = "RTU",
                            ["baudRate"] = 9600, ["dataBits"] = 8, ["stopBits"] = 1, ["parity"] = "none",
                        },
                    },
                    ["slaveId"] = 1,
                    ["pollIntervalMs"] = 1000,
                    ["reportMode"] = "both",
                    ["reportIntervalMs"] = 5000,
                    ["changeThreshold"] = 0.5,
                    ["heartbeatIntervalMs"] = 30000,
                    ["offlineTimeoutMs"] = 90000,
                    ["registerMap"] = new JsonArray(),
                    ["writeMap"] = new JsonArray(),
                },
                ["gateway"] = new JsonObject
                {
                    ["localCache"] = new JsonObject { ["storage"] = "flash", ["maxBufferSize"] = 10000, ["maxBufferSizeMB"] = 10, ["batchUploadSize"] = 50 },
                    ["reconnect"] = new JsonObject { ["initialDelayMs"] = 10000, ["maxDelayMs"] = 300000, ["backoffMultiplier"] = 2 },
                },
                ["display"] = new JsonObject
                {
                    ["groups"] = new JsonArray(),
                    ["historyChart"] = new JsonObject
                    {
                        ["defaultRange"] = "24h", ["fields"] = new JsonArray(),
                        ["chartType"] = "line", ["enableExport"] = true,
                    },
                },
                ["commands"] = new JsonObject { ["groups"] = new JsonArray() },
            };
            return cfg;
        }

        // ==========================================================
        //  平台自动填充
        // ==========================================================

        /// <summary>
        /// 用平台自己的配置填掉那些不该让人手打的字段。
        ///
        /// 只填平台确实知道的:接入点、Topic 模板、扫码绑定链接。
        /// 不碰 groupId / clientIdPrefix —— 那是设备侧用的分组,跟平台自己的 GroupID 不是一回事;
        /// 也不碰 accessKey / secretKey —— 按原规范它们由设备启动时向服务端动态换取,
        /// 烧进配置文件反而是个安全洞。
        /// </summary>
        public static void FillPlatformDefaults(JsonObject cfg, string? broker, int port, string? instanceId,
            IReadOnlyDictionary<string, string>? topics, string? publicBaseUrl, bool overwrite = true)
        {
            if (cfg["mqtt"] is not JsonObject mqtt) { mqtt = new JsonObject(); cfg["mqtt"] = mqtt; }

            // overwrite=false 时只补空的,不动用户已经改过的值
            static bool IsEmpty(JsonNode? n) => n is null || (Str(n) is { } s && string.IsNullOrWhiteSpace(s));
            void Set(JsonObject o, string key, JsonNode? val)
            {
                if (val is null) return;
                if (!overwrite && !IsEmpty(o[key])) return;
                o[key] = val;
            }

            if (!string.IsNullOrWhiteSpace(broker)) Set(mqtt, "broker", broker);
            if (port > 0) Set(mqtt, "port", port);
            if (!string.IsNullOrWhiteSpace(instanceId)) Set(mqtt, "instanceId", instanceId);

            // Topic 必须与平台订阅的一致,否则平台收不到设备上报 —— 这个最该自动填
            if (topics is not null)
            {
                if (cfg["mqttTopics"] is not JsonObject t) { t = new JsonObject(); cfg["mqttTopics"] = t; }
                foreach (var kv in topics)
                    if (!string.IsNullOrWhiteSpace(kv.Value)) Set(t, kv.Key, kv.Value);
            }

            // 扫码绑定链接:用平台自己的地址,不是 app.xxx.com 这种占位符
            if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            {
                if (cfg["meta"] is not JsonObject meta) { meta = new JsonObject(); cfg["meta"] = meta; }
                if (meta["qrCode"] is not JsonObject qr) { qr = new JsonObject(); meta["qrCode"] = qr; }
                Set(qr, "format", "url");
                Set(qr, "urlTemplate", $"{publicBaseUrl.TrimEnd('/')}/bind.html?code={{deviceId}}");
            }

            SyncQrFields(cfg);
        }

        /// <summary>
        /// 二维码的「携带字段」从链接模板里的占位符自动推导 —— 模板写了什么就带什么,
        /// 两边永远一致,不用人去对。
        /// </summary>
        public static void SyncQrFields(JsonObject cfg)
        {
            if (Obj(Obj(cfg["meta"])?["qrCode"]) is not JsonObject qr) return;
            var template = Str(qr["urlTemplate"]) ?? "";
            var arr = new JsonArray();
            foreach (Match m in PlaceholderPattern.Matches(template))
            {
                var name = m.Groups[1].Value;
                if (!arr.Any(x => Str(x) == name)) arr.Add(name);
            }
            qr["fields"] = arr;
        }

        /// <summary>匹配 {deviceId} 这类占位符。</summary>
        private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

        /// <summary>
        /// 按物模型的 meta.qrCode 规则算出二维码内容。
        ///
        /// format=url 且 urlTemplate 填了 → 生成绑定链接,微信/系统相机扫了能直接打开。
        /// 其余情况一律回落到设备标识码本身 —— 透传设备没有物模型,走的就是这条,行为跟以前一模一样。
        /// 模板里还有填不上的占位符时也回落:宁可给一个能用的序列号,也不要一个打不开的链接。
        /// </summary>
        public static string QrContent(string? configJson, string deviceCode, string? productKey, string? publicBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(configJson)) return deviceCode;
            var cfg = TryParse(configJson, out _);
            if (cfg is null) return deviceCode;

            if (Obj(Obj(cfg["meta"])?["qrCode"]) is not JsonObject qr) return deviceCode;
            if (!string.Equals(Str(qr["format"]) ?? "url", "url", StringComparison.OrdinalIgnoreCase)) return deviceCode;

            var tpl = (Str(qr["urlTemplate"]) ?? "").Trim();
            if (tpl.Length == 0) return deviceCode;

            var url = FillQrTemplate(tpl, deviceCode, productKey);
            // 还剩没填上的占位符 = 模板写错了,别生成一个打不开的链接
            if (PlaceholderPattern.IsMatch(url)) return deviceCode;

            return RebaseSampleHost(url, publicBaseUrl);
        }

        /// <summary>
        /// 把模板里的占位符换成实际值。{deviceId} 和 {gatewayId} 是同一个东西
        /// —— 规范原文用 gatewayId,平台自动填的模板用 deviceId,两种都认。
        /// </summary>
        public static string FillQrTemplate(string template, string deviceCode, string? productKey) =>
            template
                .Replace("{deviceId}", deviceCode)
                .Replace("{gatewayId}", deviceCode)
                .Replace("{code}", deviceCode)
                .Replace("{productKey}", productKey ?? "")
                .Replace("{pk}", productKey ?? "");

        /// <summary>
        /// 模板里还留着示例域名(app.xxx.com 这种)时换成平台自己的地址,否则扫出来是个不存在的站点。
        /// 用户点过「用平台配置填充」的话这里不会命中。相对路径(/bind.html?...)也在这里补上前缀。
        /// </summary>
        private static string RebaseSampleHost(string url, string? publicBaseUrl)
        {
            var baseUrl = (publicBaseUrl ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0) return url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var abs))
                return baseUrl + (url.StartsWith('/') ? url : "/" + url);

            return IsSampleHost(abs.Host) ? baseUrl + abs.PathAndQuery + abs.Fragment : url;
        }

        /// <summary>规范模板里拿 xxx.com 当占位域名(app.xxx.com / cdn.xxx.com)。</summary>
        public static bool IsSampleHost(string host) =>
            host.Equals("xxx.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".xxx.com", StringComparison.OrdinalIgnoreCase);

        // ==========================================================
        //  导出:生成带注释的 .jsonc
        // ==========================================================

        /// <summary>
        /// 把配置渲染成规范文件(.jsonc)。Part A~D 用编辑器里存的内容,
        /// Part E 的收发示例按 Part B 现推导 —— 契约改了示例自动跟着变。
        /// </summary>
        public static string ToJsonc(JsonObject cfg, string productKey, string modelName,
            string modelVersion, int revision, DateTime generatedAtUtc, string sampleDeviceId = "GW-A001")
        {
            var sb = new StringBuilder();
            var title = string.IsNullOrWhiteSpace(modelName) ? productKey : modelName;
            var stamp = generatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            sb.AppendLine("// ============================================================");
            sb.AppendLine("//");
            sb.AppendLine($"//  {title} — 完整JSON配置规范 V{modelVersion}");
            sb.AppendLine("//");
            sb.AppendLine("//  本文件由 MaxChemical 物联网平台自动生成，请勿手工编辑；");
            sb.AppendLine("//  要改请回平台的「物模型」页面改完重新导出。");
            sb.AppendLine($"//  productKey: {productKey}    revision: {revision}    生成时间: {stamp}");
            sb.AppendLine("//");
            sb.AppendLine("//  Part A: MQTT通信规范（两端共用）");
            sb.AppendLine("//  Part B: 数据契约 dataFields+serviceCommands+alarmEvents（两端共用）");
            sb.AppendLine("//  Part C: 网关端专用 — Modbus映射+本地配置");
            sb.AppendLine("//  Part D: APP端专用 — meta+display+commands");
            sb.AppendLine("//  Part E: MQTT收发完整示例（每种消息都有示例，由数据契约自动生成）");
            sb.AppendLine("//");
            sb.AppendLine("//  网关开发看: A + B + C + E");
            sb.AppendLine("//  APP开发看:  A + B + D + E");
            sb.AppendLine("//  identifier 是贯穿全文的唯一关联key");
            sb.AppendLine("//");
            sb.AppendLine("// ============================================================");
            sb.AppendLine("{");

            var parts = new List<string>();

            // ---- Part A ----
            parts.Add(Banner("Part A: MQTT通信规范（两端共用）") +
                      Section("mqtt", cfg["mqtt"]) + ",\n\n" +
                      Section("mqttTopics", cfg["mqttTopics"]));

            // ---- Part B ----
            int nf = (cfg["dataFields"] as JsonArray)?.Count ?? 0;
            int nc = (cfg["serviceCommands"] as JsonArray)?.Count ?? 0;
            int na = (cfg["alarmEvents"] as JsonArray)?.Count ?? 0;
            parts.Add(Banner("Part B: 数据契约（两端共用，identifier是唯一关联key）") +
                      $"  // ---------- {nf}个数据字段 ----------\n" +
                      Section("dataFields", cfg["dataFields"]) + ",\n\n" +
                      $"  // ---------- {nc}个控制指令 ----------\n" +
                      Section("serviceCommands", cfg["serviceCommands"]) + ",\n\n" +
                      $"  // ---------- {na}种告警事件 ----------\n" +
                      Section("alarmEvents", cfg["alarmEvents"]));

            // ---- Part C ----
            parts.Add(Banner("Part C: 网关端专用（APP端不需要看）") +
                      Section("meta", cfg["meta"]) + ",\n\n" +
                      Section("modbus", cfg["modbus"]) + ",\n\n" +
                      Section("gateway", cfg["gateway"]));

            // ---- Part D ----
            parts.Add(Banner("Part D: APP端专用 — UI配置\n//  identifier必须与Part B一致，控件类型和布局可以改\n//  网关端不需要看这部分") +
                      Section("display", cfg["display"]) + ",\n\n" +
                      Section("commands", cfg["commands"]));

            // ---- 使用者自加的非标准顶层字段,原样带出 ----
            var extras = cfg.Where(kv => !SectionOrder.Contains(kv.Key)
                                      && !kv.Key.StartsWith("mqttExample", StringComparison.OrdinalIgnoreCase)).ToList();
            if (extras.Count > 0)
                parts.Add(Banner("附加字段（平台未识别，原样保留）") +
                          string.Join(",\n\n", extras.Select(kv => Section(kv.Key, kv.Value))));

            // ---- Part E:按 Part B 现生成 ----
            parts.Add(Banner("Part E: MQTT收发完整示例（由数据契约自动生成）\n//  网关看\"网关发布\"和\"网关收到\"\n//  APP看\"APP收到\"和\"APP发布\"") +
                      BuildExamples(cfg, sampleDeviceId, productKey));

            sb.Append(string.Join(",\n\n", parts));
            sb.AppendLine();
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Banner(string text)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// ==========================================================");
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine(line.StartsWith("//", StringComparison.Ordinal) ? line : "//  " + line);
            sb.AppendLine("// ==========================================================");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>输出 `  "key": {...}`,内部每行缩进 2 空格,跟外层对象对齐。</summary>
        private static string Section(string key, JsonNode? node, int indent = 2)
        {
            var json = node is null ? "null" : node.ToJsonString(WriteOptions);
            var pad = new string(' ', indent);
            var lines = json.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            sb.Append(pad).Append('"').Append(key).Append("\": ").Append(lines[0]);
            for (int i = 1; i < lines.Length; i++) sb.Append('\n').Append(pad).Append(lines[i]);
            return sb.ToString();
        }

        // ==========================================================
        //  Part E:报文示例生成
        // ==========================================================

        /// <summary>按数据契约生成一整套收发示例:上报数据、上线、每条指令、成功/失败回复、每种告警。</summary>
        private static string BuildExamples(JsonObject cfg, string deviceId, string productKey)
        {
            var topics = cfg["mqttTopics"] as JsonObject ?? new JsonObject();
            string T(string key, string fallback) =>
                (Str(topics[key]) ?? fallback).Replace("{deviceId}", deviceId).Replace("{productKey}", productKey);

            var blocks = new List<string>();
            long ts = 1712736000000L;   // 固定时间戳:同一份配置反复导出,文件不会因为时间不同而产生无意义 diff

            // ----- 1. 实时数据 -----
            var data = new JsonObject();
            foreach (var f in (Arr(cfg["dataFields"]) ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = Str(f["identifier"]);
                if (!string.IsNullOrWhiteSpace(id)) data[id] = SampleValue(f);
            }
            blocks.Add("  // ----- 1. 网关发布 / APP收到: 设备实时数据 -----\n" +
                Section("mqttExample_data", new JsonObject
                {
                    ["_网关"] = "采集Modbus→按registerMap转换→组装→发布到此Topic",
                    ["_APP"] = "订阅此Topic→用identifier匹配display控件→渲染",
                    ["topic"] = T("deviceData", "device/{deviceId}/data"),
                    ["payload"] = new JsonObject
                    {
                        ["deviceId"] = deviceId,
                        ["productKey"] = productKey,
                        ["timestamp"] = ts,
                        ["data"] = data,
                    },
                }));

            // ----- 2. 上线通知 -----
            blocks.Add("  // ----- 2. 网关发布 / APP收到: 上线通知 -----\n" +
                Section("mqttExample_online", new JsonObject
                {
                    ["topic"] = T("online", "device/{deviceId}/online"),
                    ["payload"] = new JsonObject
                    {
                        ["deviceId"] = deviceId,
                        ["productKey"] = productKey,
                        ["timestamp"] = ts,
                        ["status"] = "online",
                    },
                }));

            // ----- 3..N 每条指令一个示例 -----
            var writeMap = Arr(Obj(cfg["modbus"])?["writeMap"]) ?? new JsonArray();
            int n = 3;
            string? firstCmd = null;
            foreach (var c in (Arr(cfg["serviceCommands"]) ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = Str(c["identifier"]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                firstCmd ??= id;

                var ps = new JsonObject();
                foreach (var p in (Arr(c["inputParams"]) ?? new JsonArray()).OfType<JsonObject>())
                {
                    var pid = Str(p["identifier"]);
                    if (!string.IsNullOrWhiteSpace(pid)) ps[pid] = SampleValue(p);
                }

                var ex = new JsonObject { ["_APP发布"] = $"用户在界面上触发「{Str(c["name"]) ?? id}」→ 发布到 command Topic" };
                var hint = DescribeWrites(writeMap, id, ps);
                if (hint is not null) ex["_网关收到"] = hint;
                ex["topic"] = T("command", "device/{deviceId}/command");
                ex["payload"] = new JsonObject
                {
                    ["commandId"] = $"cmd_{n - 2:D3}",
                    ["deviceId"] = deviceId,
                    ["command"] = id,
                    ["params"] = ps,
                };

                blocks.Add($"  // ----- {n}. APP发布 / 网关收到: {Str(c["name"]) ?? id} -----\n" +
                    Section($"mqttExample_cmd_{id}", ex));
                n++;
            }

            // ----- 指令回复(成功/失败) -----
            var replyCmd = firstCmd ?? "startStir";
            blocks.Add($"  // ----- {n}. 网关发布 / APP收到: 指令执行成功 -----\n" +
                Section("mqttExample_reply_success", new JsonObject
                {
                    ["_网关"] = "按writeMap写Modbus成功→发布到commandReply Topic",
                    ["_APP"] = "收到后显示 feedbackConfig.successText",
                    ["topic"] = T("commandReply", "device/{deviceId}/command/reply"),
                    ["payload"] = new JsonObject
                    {
                        ["commandId"] = "cmd_001", ["deviceId"] = deviceId,
                        ["command"] = replyCmd, ["result"] = 0, ["message"] = "",
                    },
                }));
            n++;

            blocks.Add($"  // ----- {n}. 网关发布 / APP收到: 指令执行失败 -----\n" +
                Section("mqttExample_reply_fail", new JsonObject
                {
                    ["_网关"] = "Modbus写失败(从站无响应)→发布失败回复",
                    ["_APP"] = "收到后显示 feedbackConfig.failText + message 内容",
                    ["topic"] = T("commandReply", "device/{deviceId}/command/reply"),
                    ["payload"] = new JsonObject
                    {
                        ["commandId"] = "cmd_001", ["deviceId"] = deviceId,
                        ["command"] = replyCmd, ["result"] = 1, ["message"] = "Modbus从站无响应，设备可能离线",
                    },
                }));
            n++;

            // ----- 每种告警一个示例 -----
            foreach (var a in (Arr(cfg["alarmEvents"]) ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = Str(a["identifier"]);
                if (string.IsNullOrWhiteSpace(id)) continue;

                var ps = new JsonObject();
                foreach (var p in (Arr(a["params"]) ?? new JsonArray()).OfType<JsonObject>())
                {
                    var pid = Str(p["identifier"]);
                    if (!string.IsNullOrWhiteSpace(pid)) ps[pid] = SampleValue(p);
                }

                var ex = new JsonObject();
                var trig = a["triggerConditions"] as JsonObject;
                if (trig is not null && Str(trig["field"]) is { Length: > 0 } tf)
                {
                    var thr = Num(trig["warningMax"]) ?? Num(trig["dangerMax"]) ?? Num(trig["warningMin"]);
                    ex["_网关"] = thr is null
                        ? $"检测到 {tf} 触发告警条件→发布告警"
                        : $"检测到 {tf} 超出阈值 {Fmt(thr.Value)}→发布告警";
                }
                ex["_APP"] = "弹出告警通知，按 params 里 alarmType 等枚举显示对应文字";
                ex["topic"] = T("alarm", "device/{deviceId}/alarm");
                ex["payload"] = new JsonObject
                {
                    ["deviceId"] = deviceId,
                    ["timestamp"] = ts,
                    ["event"] = id,
                    ["level"] = Str(a["level"]) ?? "alert",
                    ["params"] = ps,
                };

                blocks.Add($"  // ----- {n}. 网关发布 / APP收到: {Str(a["name"]) ?? id} -----\n" +
                    Section($"mqttExample_alarm_{id}", ex));
                n++;
            }

            // ----- 配置变更下发 -----
            blocks.Add($"  // ----- {n}. 服务端发布 / 网关+APP收到: 配置变更通知 -----\n" +
                Section("mqttExample_configUpdate", new JsonObject
                {
                    ["_服务端"] = "平台上物模型改动并发布后，往此Topic推变更通知",
                    ["_网关与APP"] = "收到后比对 revision，不一致就重新拉取配置",
                    ["topic"] = T("configUpdate", "config/update/{productKey}"),
                    ["payload"] = new JsonObject
                    {
                        ["productKey"] = productKey,
                        ["modelVersion"] = Str(Obj(cfg["meta"])?["modelVersion"]) ?? "1.0.0",
                        ["revision"] = 1,
                        ["timestamp"] = ts,
                        ["action"] = "reload",
                    },
                }));

            return string.Join(",\n\n", blocks);
        }

        /// <summary>把某条指令的 writeMap 翻译成人话,写进示例的 "_网关收到" 里,方便网关开发对照。</summary>
        private static string? DescribeWrites(JsonArray writeMap, string serviceId, JsonObject sampleParams)
        {
            var entry = writeMap.OfType<JsonObject>()
                .FirstOrDefault(w => Str(w["serviceIdentifier"]) == serviceId);
            var writes = entry?["writes"] as JsonArray;
            if (writes is null || writes.Count == 0) return null;

            var steps = new List<string>();
            foreach (var w in writes.OfType<JsonObject>())
            {
                var fc = (int?)Num(w["functionCode"]);
                var addr = Num(w["address"]);
                if (addr is null) continue;
                var scale = Num(w["scale"]) ?? 1;

                // FC5/15 写的是线圈,值只有通断两态,写成 ON/OFF 比写 1/0 好懂
                bool coil = fc is 5 or 15;
                string target = coil ? $"线圈{Fmt(addr.Value)}" : Fmt(addr.Value);
                string Show(double v) => coil
                    ? (v != 0 ? "ON(0xFF00)" : "OFF(0x0000)")
                    : (scale == 1 ? Fmt(v) : $"{Fmt(v)}×{Fmt(scale)}={Fmt(v * scale)}");

                var inId = Str(w["inputIdentifier"]);
                var multi = w["inputIdentifiers"] as JsonArray;

                if (multi is not null)
                {
                    var pieces = multi.Select(m => Str(m)).Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select((x, i) =>
                        {
                            var raw = Num(sampleParams[x!]);
                            var v = raw is null ? "?" : (scale == 1 ? Fmt(raw.Value) : $"{Fmt(raw.Value)}×{Fmt(scale)}={Fmt(raw.Value * scale)}");
                            return $"{x}={v}→{Fmt(addr.Value + i)}";
                        });
                    steps.Add($"FC{fc}批量写: {string.Join(", ", pieces)}");
                }
                else if (!string.IsNullOrWhiteSpace(inId))
                {
                    var raw = Num(sampleParams[inId]);
                    var shown = raw is null ? $"取自入参 {inId}" : Show(raw.Value);
                    steps.Add($"FC{fc}写{target}={shown}");
                }
                else
                {
                    var val = Num(w["value"]);
                    steps.Add($"FC{fc}写{target}={(val is null ? "?" : Show(val.Value))}");
                }
            }
            return steps.Count == 0 ? null : "匹配writeMap→" + string.Join(" → ", steps);
        }

        /// <summary>按字段定义造一个像样的示例值:落在量程内、小数位跟着 step 走、枚举取第一个取值。</summary>
        private static JsonNode SampleValue(JsonObject def)
        {
            var type = Str(def["dataType"]) ?? "float";
            var options = def["options"] as JsonObject;

            switch (type)
            {
                case "bool":
                    return JsonValue.Create(1)!;

                case "enum":
                    // 取 options 里第一个 key;是数字就用数字,否则原样用字符串
                    var firstKey = options?.FirstOrDefault().Key;
                    if (firstKey is not null && int.TryParse(firstKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev))
                        return JsonValue.Create(ev)!;
                    return JsonValue.Create(firstKey ?? "0")!;

                case "string":
                    return JsonValue.Create("V1.0.0")!;

                case "int":
                {
                    var min = Num(def["min"]); var max = Num(def["max"]);
                    if (min is not null && max is not null)
                        return JsonValue.Create((int)Math.Round(min.Value + (max.Value - min.Value) * 0.4))!;
                    return JsonValue.Create(100)!;
                }

                default:   // float
                {
                    var min = Num(def["min"]); var max = Num(def["max"]);
                    int dec = DecimalsOf(Num(def["step"]));
                    if (min is not null && max is not null)
                        return JsonValue.Create(Math.Round(min.Value + (max.Value - min.Value) * 0.4, dec))!;
                    return JsonValue.Create(Math.Round(12.3, dec))!;
                }
            }
        }

        /// <summary>step=0.01 → 2 位小数。没给 step 按 1 位。</summary>
        private static int DecimalsOf(double? step)
        {
            if (step is null || step <= 0) return 1;
            var s = step.Value.ToString("0.##########", CultureInfo.InvariantCulture);
            var dot = s.IndexOf('.');
            return dot < 0 ? 0 : s.Length - dot - 1;
        }

        // ==========================================================
        //  JsonNode 小工具
        // ==========================================================

        /// <summary>取字符串值;不是字符串或不存在返回 null(不抛)。</summary>
        public static string? Str(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        /// <summary>
        /// 取数值;取不到返回 null(不抛)。
        ///
        /// 不能只试 double:
        ///  - JsonNode.Parse 出来的节点走 JsonElement,整数也能读成 double;
        ///  - 但代码里 JsonValue.Create(3) 造出来的节点内部是 int,TryGetValue&lt;double&gt; 会返回 false
        ///    (导出 Part E 时会撞到,示例值算不出来);
        ///  - 手写的配置里还常把数值写成字符串("address": "40001"),不认的话会被校验判成"没填",
        ///    发布被永久卡死且报错误导。
        /// </summary>
        public static double? Num(JsonNode? n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<decimal>(out var m)) return (double)m;
            if (v.TryGetValue<float>(out var f)) return f;
            if (v.TryGetValue<string>(out var str)
                && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) return ps;
            return null;
        }

        /// <summary>当对象取;不是对象返回 null。
        /// 手改过或外部导入的配置里,某个区块可能写成了数组/字符串 —— 直接 node["k"] 会抛,套一层就安全了。</summary>
        public static JsonObject? Obj(JsonNode? n) => n as JsonObject;

        /// <summary>当数组取;不是数组返回 null。</summary>
        public static JsonArray? Arr(JsonNode? n) => n as JsonArray;

        /// <summary>数值格式化:整数不带小数点,小数最多 4 位,不受服务器区域设置影响。</summary>
        private static string Fmt(double d) =>
            d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
