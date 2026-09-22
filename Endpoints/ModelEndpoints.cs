using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Devices;
using MaxChemical.DtuServer.Services;
using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>
    /// 物模型(产品)管理:在线配置 → 生成设备接入网关 + 手机APP 共用的 JSONC 规范文件。
    ///
    /// 三种交付方式都在这里:
    ///   1. 网页下载   GET  /admin/models/{key}/export.jsonc
    ///   2. HTTP 拉取  GET  /api/config/device/{code}  (网关按序列号拉)
    ///                 GET  /api/v1/config/{productKey} (APP 带 Bearer 拉)
    ///   3. MQTT 下发  POST /admin/models/{key}/publish  → config/update/{productKey}
    ///
    /// 与原有 DTU 透传链路无任何交集。
    /// </summary>
    public static class ModelEndpoints
    {
        public record CreateModelReq(string ProductKey, string? ModelName, string? ModelVersion,
            string? Description, string? Manufacturer, string? Icon, string? Photo);

        public static void MapModels(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/admin").RequireAuthorization("AdminOnly");

            // ---------- 列表 ----------
            g.MapGet("/models", async (AppDbContext db) =>
            {
                var models = await db.ProductModels.OrderByDescending(m => m.UpdatedAt).ToListAsync();
                // 每个型号下挂了多少台设备(删除前要提醒)
                var counts = await db.Devices
                    .Where(d => d.ProductKey != null && d.ProductKey != "")
                    .GroupBy(d => d.ProductKey!)
                    .Select(x => new { Key = x.Key, N = x.Count() })
                    .ToDictionaryAsync(x => x.Key, x => x.N);

                return Results.Ok(models.Select(m => new
                {
                    m.Id, m.ProductKey, m.ModelName, m.ModelVersion, m.Description, m.Manufacturer,
                    m.Icon, m.Photo, m.Revision, m.Status, m.PublishedAt, m.PublishedRevision,
                    m.CreatedAt, m.UpdatedAt,
                    deviceCount = counts.TryGetValue(m.ProductKey, out var n) ? n : 0,
                    // 草稿改动比已发布版本新 → 界面上提示"有未发布的改动"
                    hasUnpublished = m.Revision > m.PublishedRevision,
                    stats = Stats(m.ConfigJson),
                }));
            });

            // ---------- 详情(含完整配置) ----------
            g.MapGet("/models/{key}", async (string key, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                var cfg = ModelSpec.TryParse(m.ConfigJson, out _) ?? ModelSpec.BlankConfig(m.ProductKey, m.ModelName);
                var v = ModelSpec.Validate(cfg);
                return Results.Ok(new
                {
                    m.Id, m.ProductKey, m.ModelName, m.ModelVersion, m.Description, m.Manufacturer,
                    m.Icon, m.Photo, m.Revision, m.Status, m.PublishedAt, m.PublishedRevision,
                    m.CreatedAt, m.UpdatedAt,
                    config = cfg,
                    issues = v.Issues,
                });
            });

            // ---------- 新建(空白骨架) ----------
            g.MapPost("/models", async (CreateModelReq req, AppDbContext db, HttpContext ctx,
                MqttOptions mqttOpt, IConfiguration cfgRoot) =>
            {
                var key = (req.ProductKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key))
                    return Results.BadRequest(new { error = "productKey 不能为空" });
                if (!ProductKeyPattern.IsMatch(key))
                    return Results.BadRequest(new { error = "productKey 只能用字母/数字/下划线/短横线,长度 2-64" });
                if (await db.ProductModels.AnyAsync(x => x.ProductKey == key))
                    return Results.BadRequest(new { error = $"productKey '{key}' 已存在" });

                var name = string.IsNullOrWhiteSpace(req.ModelName) ? key : req.ModelName!.Trim();
                var version = string.IsNullOrWhiteSpace(req.ModelVersion) ? "1.0.0" : req.ModelVersion!.Trim();

                var cfg = ModelSpec.BlankConfig(key, name);
                var meta = cfg["meta"]!.AsObject();
                meta["modelVersion"] = version;
                meta["description"] = req.Description ?? "";
                meta["manufacturer"] = req.Manufacturer ?? "";
                meta["icon"] = req.Icon ?? "";
                meta["photo"] = req.Photo ?? "";

                // 接入点、Topic、扫码链接直接用平台自己的配置填好 —— 这些平台本来就知道,
                // 不该让人照着 mqtt-cn-xxxxx 这种占位符猜着填
                ApplyPlatformDefaults(cfg, mqttOpt, cfgRoot, overwrite: true);

                var m = new ProductModel
                {
                    ProductKey = key,
                    ModelName = name,
                    ModelVersion = version,
                    Description = req.Description ?? "",
                    Manufacturer = req.Manufacturer ?? "",
                    Icon = req.Icon ?? "",
                    Photo = req.Photo ?? "",
                    ConfigJson = cfg.ToJsonString(ModelSpec.WriteOptions),
                    CreatedByUserId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                };
                db.ProductModels.Add(m);
                await db.SaveChangesAsync();
                return Results.Ok(new { m.Id, m.ProductKey, m.ModelName, m.ModelVersion, m.Revision, m.Status });
            });

            // ---------- 保存配置 ----------
            // 请求体直接就是完整配置对象(允许带 // 注释)。meta 是唯一真相源,
            // 列里的 modelName/modelVersion 等都从 meta 里抽,productKey 以 URL 为准不可改。
            g.MapPut("/models/{key}", async (string key, HttpContext ctx, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                var raw = await ReadBodyAsync(ctx);
                var parsed = ModelSpec.TryParse(raw, out var perr);
                if (parsed is null) return Results.BadRequest(new { error = perr });

                var cfg = ModelSpec.Normalize(parsed);
                SyncMeta(cfg, m);
                ModelSpec.SyncQrFields(cfg);   // 携带字段跟着链接模板走,不用人维护

                var v = ModelSpec.Validate(cfg);
                // 结构性错误(identifier 重名/非法)会让下游全乱,直接拒;
                // 交叉引用错误允许存草稿,发布时再卡。
                if (v.HasFatal)
                    return Results.BadRequest(new { error = "配置存在必须先修正的错误", issues = v.Issues });

                m.ConfigJson = cfg.ToJsonString(ModelSpec.WriteOptions);
                m.Revision++;
                m.UpdatedAt = DateTime.UtcNow;
                ApplyMetaToColumns(cfg, m);
                await db.SaveChangesAsync();

                return Results.Ok(new { m.Revision, m.UpdatedAt, m.ModelName, m.ModelVersion, issues = v.Issues });
            });

            // ---------- 只校验不保存(编辑器实时提示) ----------
            g.MapPost("/models/validate", async (HttpContext ctx) =>
            {
                var raw = await ReadBodyAsync(ctx);
                var parsed = ModelSpec.TryParse(raw, out var perr);
                if (parsed is null) return Results.BadRequest(new { error = perr });
                var v = ModelSpec.Validate(ModelSpec.Normalize(parsed));
                return Results.Ok(new { issues = v.Issues, hasError = v.HasError, hasFatal = v.HasFatal });
            });

            // ---------- 删除 ----------
            g.MapDelete("/models/{key}", async (string key, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound();

                // 还在被设备引用就不能删,否则那些设备会变成孤儿。把设备名列出来,方便去处理。
                var users = await db.Devices.Where(d => d.ProductKey == m.ProductKey)
                    .Select(d => d.Name).Take(6).ToListAsync();
                if (users.Count > 0)
                {
                    var total = await db.Devices.CountAsync(d => d.ProductKey == m.ProductKey);
                    var names = string.Join("、", users.Take(5));
                    if (total > 5) names += $" 等 {total} 台";
                    return Results.BadRequest(new { error = $"还有设备在用这个物模型（{names}），请先到「网关设备」页把它们改到别的物模型或删除" });
                }

                db.ProductModels.Remove(m);
                await db.SaveChangesAsync();
                return Results.Ok();
            });

            // ---------- 导入现成的 .jsonc ----------
            // 手上已经有配置文件的,直接传进来就能接着在平台上改。
            g.MapPost("/models/import", async (HttpContext ctx, AppDbContext db,
                MqttOptions mqttOpt, IConfiguration cfgRoot) =>
            {
                string raw;
                // 支持两种传法:表单上传文件,或者直接把文件内容当请求体 POST
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var file = form.Files["file"];
                    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "没有上传文件" });
                    if (file.Length > 4 * 1024 * 1024) return Results.BadRequest(new { error = "配置文件不能超过 4MB" });
                    using var sr = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
                    raw = await sr.ReadToEndAsync();
                }
                else raw = await ReadBodyAsync(ctx);

                var parsed = ModelSpec.TryParse(raw, out var perr);
                if (parsed is null) return Results.BadRequest(new { error = perr });

                var cfg = ModelSpec.Normalize(parsed);
                var meta = cfg["meta"] as JsonObject;

                // productKey 优先取 URL 参数,其次取文件里的 meta.productKey
                var key = (ctx.Request.Query["productKey"].ToString() is { Length: > 0 } q
                    ? q : ModelSpec.Str(meta?["productKey"]) ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key))
                    return Results.BadRequest(new { error = "文件里没有 meta.productKey,请在导入时指定 productKey" });

                // 与新建走同一套规则:含 / 之类字符的 key 会让 /admin/models/{key} 路由永远匹配不到,
                // 结果是一条打不开、改不了、也删不掉的记录。
                if (!ProductKeyPattern.IsMatch(key))
                    return Results.BadRequest(new { error = "productKey 只能用字母/数字/下划线/短横线,长度 2-64" });

                bool overwrite = ctx.Request.Query["overwrite"].ToString() == "true";
                var existing = await Find(db, key);
                if (existing is not null && !overwrite)
                    return Results.BadRequest(new { error = $"productKey '{key}' 已存在,如需覆盖请勾选覆盖导入" });

                var m = existing ?? new ProductModel
                {
                    ProductKey = key,
                    CreatedByUserId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                };
                SyncMeta(cfg, m);
                // 导入的文件以它自己的内容为准,只把空着的字段用平台配置补上
                ApplyPlatformDefaults(cfg, mqttOpt, cfgRoot, overwrite: false);

                var v = ModelSpec.Validate(cfg);
                if (v.HasFatal)
                    return Results.BadRequest(new { error = "导入的配置存在必须先修正的错误", issues = v.Issues });

                m.ConfigJson = cfg.ToJsonString(ModelSpec.WriteOptions);
                m.UpdatedAt = DateTime.UtcNow;
                ApplyMetaToColumns(cfg, m);
                if (existing is null) { db.ProductModels.Add(m); } else { m.Revision++; }
                await db.SaveChangesAsync();

                return Results.Ok(new { m.ProductKey, m.ModelName, m.ModelVersion, m.Revision, issues = v.Issues });
            });

            // ---------- 用平台配置回填 ----------
            // 导入进来的文件里往往还是 mqtt-cn-xxxxx / app.xxx.com 这类占位符,
            // 点一下就换成本平台真实的接入点、Topic 和扫码链接。
            g.MapPost("/models/{key}/apply-platform-defaults", async (string key, AppDbContext db,
                MqttOptions mqttOpt, IConfiguration cfgRoot) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                var cfg = ModelSpec.TryParse(m.ConfigJson, out var perr);
                if (cfg is null) return Results.BadRequest(new { error = perr });

                ApplyPlatformDefaults(cfg, mqttOpt, cfgRoot, overwrite: true);
                m.ConfigJson = cfg.ToJsonString(ModelSpec.WriteOptions);
                m.Revision++;
                m.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                return Results.Ok(new { m.Revision, config = cfg });
            });

            // ---------- 复制 ----------
            g.MapPost("/models/{key}/duplicate", async (string key, HttpContext ctx, AppDbContext db) =>
            {
                var src = await Find(db, key);
                if (src is null) return Results.NotFound(new { error = "物模型不存在" });

                var newKey = ctx.Request.Query["productKey"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(newKey)) newKey = src.ProductKey + "_copy";
                if (await db.ProductModels.AnyAsync(x => x.ProductKey == newKey))
                    return Results.BadRequest(new { error = $"productKey '{newKey}' 已存在" });

                var cfg = ModelSpec.TryParse(src.ConfigJson, out _) ?? ModelSpec.BlankConfig(newKey, src.ModelName);
                var copy = new ProductModel
                {
                    ProductKey = newKey,
                    ModelName = src.ModelName + " (副本)",
                    ModelVersion = src.ModelVersion,
                    Description = src.Description,
                    Manufacturer = src.Manufacturer,
                    Icon = src.Icon,
                    Photo = src.Photo,
                    CreatedByUserId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                };
                SyncMeta(cfg, copy);
                copy.ConfigJson = cfg.ToJsonString(ModelSpec.WriteOptions);
                db.ProductModels.Add(copy);
                await db.SaveChangesAsync();
                return Results.Ok(new { copy.ProductKey, copy.ModelName });
            });

            // ---------- 导出:带注释的规范文件 ----------
            g.MapGet("/models/{key}/export.jsonc", async (string key, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound();
                var text = Render(m);
                var name = $"{m.ProductKey}_Full_Config_v{m.ModelVersion}.jsonc";
                return Results.File(Encoding.UTF8.GetBytes(text), "application/octet-stream", name);
            });

            // ---------- 导出:纯 JSON(给程序读) ----------
            g.MapGet("/models/{key}/export.json", async (string key, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound();
                return Results.Text(m.ConfigJson, "application/json", Encoding.UTF8);
            });

            // ---------- 预览:在编辑器里看生成出来长什么样 ----------
            g.MapGet("/models/{key}/preview", async (string key, AppDbContext db) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound();
                return Results.Text(Render(m), "text/plain", Encoding.UTF8);
            });

            // ---------- 发布:置为已发布 + 往 MQTT 推变更通知 ----------
            g.MapPost("/models/{key}/publish", async (string key, AppDbContext db, MqttGatewayService mqtt, HttpContext ctx) =>
            {
                var m = await Find(db, key);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                var cfg = ModelSpec.TryParse(m.ConfigJson, out var perr);
                if (cfg is null) return Results.BadRequest(new { error = perr });

                // 发布这一步卡全部错误 —— 设备真要按它跑了,交叉引用不能有问题
                var v = ModelSpec.Validate(cfg);
                if (v.HasError)
                    return Results.BadRequest(new { error = "配置还有错误,修好才能发布", issues = v.Issues });

                m.Status = ModelStatuses.Published;
                m.PublishedAt = DateTime.UtcNow;
                m.PublishedRevision = m.Revision;
                await db.SaveChangesAsync();

                // MQTT 没启用/没连上不算发布失败 —— 网关和 APP 还能走 HTTP 拉取
                bool pushed = false;
                string? pushError = null;
                try { pushed = await mqtt.PublishConfigUpdateAsync(m.ProductKey, m.ModelVersion, m.Revision, ctx.RequestAborted); }
                catch (Exception ex) { pushError = ex.Message; }

                return Results.Ok(new
                {
                    m.ProductKey, m.ModelVersion, m.Revision, m.PublishedAt,
                    mqttPushed = pushed,
                    mqttNote = pushed ? null
                        : pushError ?? (mqtt.Enabled ? "平台与 MQTT Broker 未连接,变更通知未推送" : "平台未启用 MQTT 接入,变更通知未推送"),
                });
            });

            MapPublicConfig(app);
        }

        // ==========================================================
        //  公开的配置拉取接口(网关 / APP 用)
        // ==========================================================

        private static void MapPublicConfig(IEndpointRouteBuilder app)
        {
            // 网关拉配置:用设备序列号(= 设备标识码)换它所属型号的完整配置。
            // 鉴权用 GatewayConfigPull:AccessToken(经 X-Access-Token 头传),留空则不校验
            // —— 与 Hub:AccessToken 一个套路,方便先联调后收紧。
            app.MapGet("/api/config/device/{code}", async (string code, HttpContext ctx,
                AppDbContext db, IConfiguration cfgRoot) =>
            {
                if (!CheckPullToken(ctx, cfgRoot)) return Results.Json(new { error = "拉取令牌无效" }, statusCode: 401);

                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev is null) return Results.NotFound(new { error = "设备标识码不存在" });
                if (string.IsNullOrWhiteSpace(dev.ProductKey))
                    return Results.NotFound(new { error = "该设备没有绑定物模型(可能是透传模式设备)" });

                var m = await db.ProductModels.FirstOrDefaultAsync(x => x.ProductKey == dev.ProductKey);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                return ConfigResponse(ctx, m, dev.Code);
            })
            .WithSummary("网关按设备序列号拉取物模型配置");

            // APP 拉配置:走已有的 Bearer 体系(先用 API Key/Secret 换令牌)
            app.MapGet("/api/v1/config/{productKey}", async (string productKey, HttpContext ctx, AppDbContext db) =>
            {
                var m = await db.ProductModels.FirstOrDefaultAsync(x => x.ProductKey == productKey);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });
                return ConfigResponse(ctx, m, null);
            })
            .RequireAuthorization("ApiBearer")
            .WithSummary("APP 按 productKey 拉取物模型配置(用于动态渲染界面)");
        }

        /// <summary>
        /// 统一的配置响应。带 revision:客户端本地存着上次的 revision,
        /// 传 ?revision=N 且没变就回 304,省流量(对应 meta.cachePolicy.checkVersionOnLogin)。
        /// </summary>
        private static IResult ConfigResponse(HttpContext ctx, ProductModel m, string? deviceId)
        {
            if (int.TryParse(ctx.Request.Query["revision"], out var known) && known == m.Revision)
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var cfg = ModelSpec.TryParse(m.ConfigJson, out _) ?? ModelSpec.BlankConfig(m.ProductKey, m.ModelName);
            // 凭据不随配置文件下发 —— 这份配置会发到每一台设备上,按原规范 accessKey/secretKey
            // 由设备启动时单独向服务端换取。与 /web/models/{productKey} 的处理保持一致。
            if (cfg["mqtt"] is JsonObject q) { q.Remove("accessKey"); q.Remove("secretKey"); }
            return Results.Ok(new
            {
                productKey = m.ProductKey,
                modelName = m.ModelName,
                modelVersion = m.ModelVersion,
                revision = m.Revision,
                status = m.Status,
                deviceId,
                updatedAt = m.UpdatedAt,
                config = cfg,
            });
        }

        private static bool CheckPullToken(HttpContext ctx, IConfiguration cfg)
        {
            var expected = cfg["GatewayConfigPull:AccessToken"];
            if (string.IsNullOrWhiteSpace(expected)) return true;   // 没配 = 不校验
            return ctx.Request.Headers["X-Access-Token"].ToString() == expected;
        }

        // ==========================================================
        //  小工具
        // ==========================================================

        /// <summary>productKey 只允许出现在 URL 路径里安全的字符,否则整条记录会失联。</summary>
        private static readonly System.Text.RegularExpressions.Regex ProductKeyPattern =
            new(@"^[A-Za-z0-9_-]{2,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static Task<ProductModel?> Find(AppDbContext db, string key) =>
            db.ProductModels.FirstOrDefaultAsync(x => x.ProductKey == key);

        private static async Task<string> ReadBodyAsync(HttpContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        /// 把平台自己知道的东西填进配置:接入点、Topic 模板、扫码绑定链接。
        /// Topic 尤其关键 —— 与平台订阅的对不上,设备发的消息平台一条都收不到。
        /// </summary>
        private static void ApplyPlatformDefaults(JsonObject cfg, MqttOptions mqtt, IConfiguration cfgRoot, bool overwrite)
        {
            var topics = new Dictionary<string, string>
            {
                ["deviceData"] = mqtt.Topics.DeviceData,
                ["command"] = mqtt.Topics.Command,
                ["commandReply"] = mqtt.Topics.CommandReply,
                ["alarm"] = mqtt.Topics.Alarm,
                ["online"] = mqtt.Topics.Online,
                ["configUpdate"] = mqtt.Topics.ConfigUpdate,
            };
            ModelSpec.FillPlatformDefaults(cfg, mqtt.Broker, mqtt.Port, mqtt.InstanceId,
                topics, cfgRoot["PublicBaseUrl"], overwrite);
        }

        private static string Render(ProductModel m)
        {
            var cfg = ModelSpec.TryParse(m.ConfigJson, out _) ?? ModelSpec.BlankConfig(m.ProductKey, m.ModelName);
            return ModelSpec.ToJsonc(cfg, m.ProductKey, m.ModelName, m.ModelVersion, m.Revision, DateTime.UtcNow);
        }

        /// <summary>meta.productKey 强制跟数据库对齐,并把缺的 meta 字段补上。</summary>
        private static void SyncMeta(JsonObject cfg, ProductModel m)
        {
            if (cfg["meta"] is not JsonObject meta)
            {
                meta = new JsonObject();
                cfg["meta"] = meta;
            }
            meta["productKey"] = m.ProductKey;   // productKey 以数据库为准,不允许配置文件改
            if (meta["modelName"] is null) meta["modelName"] = m.ModelName;
            if (meta["modelVersion"] is null) meta["modelVersion"] = m.ModelVersion;
        }

        /// <summary>meta 是唯一真相源:保存后把它同步到列里,列只是给列表查询用的冗余。</summary>
        private static void ApplyMetaToColumns(JsonObject cfg, ProductModel m)
        {
            var meta = cfg["meta"] as JsonObject;
            if (meta is null) return;
            m.ModelName = ModelSpec.Str(meta["modelName"]) is { Length: > 0 } n ? n : m.ProductKey;
            m.ModelVersion = ModelSpec.Str(meta["modelVersion"]) is { Length: > 0 } v ? v : "1.0.0";
            m.Description = ModelSpec.Str(meta["description"]) ?? "";
            m.Manufacturer = ModelSpec.Str(meta["manufacturer"]) ?? "";
            m.Icon = ModelSpec.Str(meta["icon"]) ?? "";
            m.Photo = ModelSpec.Str(meta["photo"]) ?? "";
        }

        /// <summary>列表页展示用的规模统计:几个字段/几条指令/几种告警/映射了几个寄存器。</summary>
        private static object Stats(string configJson)
        {
            var cfg = ModelSpec.TryParse(configJson, out _);
            if (cfg is null) return new { fields = 0, commands = 0, alarms = 0, registers = 0, writes = 0 };
            var modbus = ModelSpec.Obj(cfg["modbus"]);
            return new
            {
                fields = ModelSpec.Arr(cfg["dataFields"])?.Count ?? 0,
                commands = ModelSpec.Arr(cfg["serviceCommands"])?.Count ?? 0,
                alarms = ModelSpec.Arr(cfg["alarmEvents"])?.Count ?? 0,
                registers = ModelSpec.Arr(modbus?["registerMap"])?.Count ?? 0,
                writes = ModelSpec.Arr(modbus?["writeMap"])?.Count ?? 0,
            };
        }
    }
}
