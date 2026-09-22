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
    /// MQTT 网关设备:独立于原有透传设备的一套管理端点。
    ///
    /// 设备标识码(Device.Code)在这条链路上同时就是 MQTT Topic 里的 {deviceId},
    /// 所以扫码绑定、授权、二维码这些老功能原样复用,不用另起一套。
    /// </summary>
    public static class MqttDeviceEndpoints
    {
        public static void MapMqttDevices(this IEndpointRouteBuilder app)
        {
            MapAdmin(app);
            MapWeb(app);
        }

        // ==========================================================
        //  管理端(Admin)
        // ==========================================================

        private static void MapAdmin(IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/admin").RequireAuthorization("AdminOnly");

            // 列表:只列 MQTT 模式的设备
            g.MapGet("/mqtt-devices", async (AppDbContext db, MqttGatewayService mqtt) =>
            {
                var list = await db.Devices
                    .Where(d => d.AccessMode == AccessModes.Mqtt)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new { d.Id, d.Code, d.Name, d.ProductKey, d.CreatedAt, HasImage = d.ImageData != null })
                    .ToListAsync();

                var keys = list.Where(d => !string.IsNullOrEmpty(d.ProductKey)).Select(d => d.ProductKey!).Distinct().ToList();
                var models = await db.ProductModels.Where(m => keys.Contains(m.ProductKey))
                    .ToDictionaryAsync(m => m.ProductKey, m => new { m.ModelName, m.ModelVersion, m.Revision, m.Status });

                return Results.Ok(list.Select(d =>
                {
                    var st = mqtt.GetState(d.Code);
                    models.TryGetValue(d.ProductKey ?? "", out var model);
                    return new
                    {
                        d.Id, d.Code, d.Name, d.ProductKey, d.CreatedAt,
                        hasImage = d.HasImage,
                        modelName = model?.ModelName,
                        modelVersion = model?.ModelVersion,
                        modelStatus = model?.Status,
                        online = st?.Online ?? false,
                        lastSeen = st?.LastSeenUtc,
                        fieldCount = st?.Values.Count ?? 0,
                    };
                }));
            });

            // 添加 MQTT 设备:自动生成设备标识码(= MQTT deviceId,也是二维码内容)
            g.MapPost("/mqtt-devices", async (HttpContext ctx, AppDbContext db) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var name = form["name"].ToString().Trim();
                var productKey = form["productKey"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest(new { error = "设备名不能为空" });
                if (string.IsNullOrWhiteSpace(productKey))
                    return Results.BadRequest(new { error = "请选择物模型" });
                if (!await db.ProductModels.AnyAsync(m => m.ProductKey == productKey))
                    return Results.BadRequest(new { error = $"物模型 '{productKey}' 不存在" });

                // 标识码 = MQTT Topic 里的 {deviceId}。网关出厂号写死的场景要能手填,
                // 否则平台生成的码和网关自报的号对不上,消息永远落在未识别 Topic 上。
                var (serial, codeErr) = await AdminEndpoints.ResolveNewDeviceCodeAsync(db, form["code"].ToString());
                if (codeErr is not null) return Results.BadRequest(new { error = codeErr });

                var dev = new Device
                {
                    Code = serial!,
                    Name = name,
                    AccessMode = AccessModes.Mqtt,
                    ProductKey = productKey,
                    // 下面两个字段属于透传链路,MQTT 设备用不上,但列不可空,给个安全的默认值
                    DeviceType = "",
                    DtuSerial = serial!,
                    ModbusStation = 1,
                    CreatedByUserId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                };

                var err = await ApplyImageAsync(form, dev);
                if (err is not null) return Results.BadRequest(new { error = err });

                db.Devices.Add(dev);
                await db.SaveChangesAsync();
                return Results.Ok(new { dev.Id, dev.Code, dev.Name, dev.ProductKey });
            });

            // 编辑:名称/物模型/图片。标识码不可改(设备侧已经烧进去了)
            g.MapPut("/mqtt-devices/{id}", async (string id, HttpContext ctx, AppDbContext db) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev is null) return Results.NotFound();
                if (!AccessModes.IsMqtt(dev.AccessMode))
                    return Results.BadRequest(new { error = "该设备不是 MQTT 模式,请到设备管理页编辑" });

                var form = await ctx.Request.ReadFormAsync();
                var name = form["name"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest(new { error = "设备名不能为空" });

                var productKey = form["productKey"].ToString().Trim();
                if (!string.IsNullOrWhiteSpace(productKey))
                {
                    if (!await db.ProductModels.AnyAsync(m => m.ProductKey == productKey))
                        return Results.BadRequest(new { error = $"物模型 '{productKey}' 不存在" });
                    dev.ProductKey = productKey;
                }
                dev.Name = name;

                var err = await ApplyImageAsync(form, dev);
                if (err is not null) return Results.BadRequest(new { error = err });

                await db.SaveChangesAsync();
                return Results.Ok(new { dev.Id, dev.Code, dev.Name, dev.ProductKey });
            });

            // 平台 MQTT 链路诊断。topics 给编辑器用:物模型里的 Topic 模板跟平台订阅的不一致就提示。
            g.MapGet("/mqtt-status", (MqttGatewayService mqtt, MqttOptions opt) => Results.Ok(new
            {
                enabled = mqtt.Enabled,
                connected = mqtt.Connected,
                broker = opt.Broker,
                topics = opt.Topics,
                devices = mqtt.Snapshot(),
            }));
        }

        // ==========================================================
        //  网页端(登录用户)
        // ==========================================================

        private static void MapWeb(IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/web").RequireAuthorization();

            // 物模型配置(只读)。网页面板要按它渲染控件,所以普通登录用户也能读 ——
            // 里面是界面定义和数据契约,不含任何凭据(mqtt.accessKey 等在这里剥掉)。
            g.MapGet("/models/{productKey}", async (string productKey, AppDbContext db) =>
            {
                var m = await db.ProductModels.FirstOrDefaultAsync(x => x.ProductKey == productKey);
                if (m is null) return Results.NotFound(new { error = "物模型不存在" });

                var cfg = ModelSpec.TryParse(m.ConfigJson, out _) ?? ModelSpec.BlankConfig(m.ProductKey, m.ModelName);
                if (cfg["mqtt"] is JsonObject q) { q.Remove("accessKey"); q.Remove("secretKey"); }

                return Results.Ok(new
                {
                    m.ProductKey, m.ModelName, m.ModelVersion, m.Revision, m.Status,
                    config = cfg,
                });
            });

            // MQTT 设备的实时数据:直接读平台缓存的最新上报值(不像透传那样现场问 Modbus)
            g.MapGet("/mqtt/devices/{code}/telemetry", async (string code, AppDbContext db,
                MqttGatewayService mqtt, HttpContext ctx) =>
            {
                var (dev, err) = await Resolve(db, ctx, code, needControl: false);
                if (err is not null) return err;
                return Results.Ok(TelemetrySnapshot(mqtt, dev!));
            });

            // 下发指令(多参数)。MQTT 设备走 MQTT,透传设备回落到原来的档案写入 —— 一个入口两条链路。
            g.MapPost("/devices/{code}/command", async (string code, HttpContext ctx, AppDbContext db,
                MqttGatewayService mqtt, DtuServerManager dtu, DeviceProfileRegistry profiles, DeviceModelCatalog catalog) =>
            {
                var (dev, err) = await Resolve(db, ctx, code, needControl: true);
                if (err is not null) return err;

                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var raw = await reader.ReadToEndAsync();
                var body = ModelSpec.TryParse(raw, out var perr);
                if (body is null) return Results.BadRequest(new { error = perr });

                var command = ModelSpec.Str(body["command"]);
                if (string.IsNullOrWhiteSpace(command))
                    return Results.BadRequest(new { error = "缺少 command" });
                var parameters = body["params"] as JsonObject ?? new JsonObject();

                if (AccessModes.IsMqtt(dev!.AccessMode))
                {
                    if (!mqtt.Enabled) return Results.Json(new { error = "平台未启用 MQTT 接入" }, statusCode: 503);
                    if (!mqtt.IsOnline(dev.Code)) return Results.Json(new { error = "设备离线" }, statusCode: 409);

                    var r = await mqtt.SendCommandAsync(dev.Code, command!, parameters, ctx.RequestAborted);
                    return r.Ok
                        ? Results.Ok(new { ok = true, commandId = r.CommandId })
                        : Results.Json(new { error = string.IsNullOrEmpty(r.Message) ? "指令执行失败" : r.Message, result = r.Result, commandId = r.CommandId }, statusCode: 502);
                }

                // ---- 透传设备:原样走内置档案(逻辑与 /web/devices/{code}/control 一致)----
                if (!dtu.IsOnline(dev.DtuSerial)) return Results.Json(new { error = "设备离线" }, statusCode: 409);
                var profileKey = catalog.Get(dev.DeviceType)?.ProfileKey ?? dev.DeviceType;
                var profile = profiles.Get(profileKey);
                if (profile is null) return Results.Json(new { error = "该设备型号暂不支持控制" }, statusCode: 400);

                var args = new Dictionary<string, double>();
                foreach (var kv in parameters)
                    if (ModelSpec.Num(kv.Value) is { } d) args[kv.Key] = d;
                if (!args.ContainsKey("value") && args.Count == 1) args["value"] = args.Values.First();

                try
                {
                    var ok = await profile.WriteControlAsync(dtu, dev.DtuSerial, (byte)dev.ModbusStation,
                        command!, args, ctx.RequestAborted);
                    return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = "下发失败" }, statusCode: 502);
                }
                catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
            });

            // 告警记录(倒序)
            g.MapGet("/devices/{code}/alarms", async (string code, AppDbContext db, HttpContext ctx) =>
            {
                var (dev, err) = await Resolve(db, ctx, code, needControl: false);
                if (err is not null) return err;

                int take = int.TryParse(ctx.Request.Query["take"], out var t) ? Math.Clamp(t, 1, 500) : 50;
                var rows = await db.DeviceAlarms
                    .Where(a => a.DeviceCode == dev!.Code)
                    .OrderByDescending(a => a.CreatedAt)
                    .Take(take)
                    .ToListAsync();

                return Results.Ok(rows.Select(a => new
                {
                    a.Id, a.Event, a.Level, a.Timestamp, a.CreatedAt,
                    parameters = ModelSpec.TryParse(a.ParamsJson, out _) ?? new JsonObject(),
                }));
            });

            // 当前用户能看的 MQTT 设备(含物模型和最新值),给独立页面用
            g.MapGet("/mqtt/devices", async (AppDbContext db, MqttGatewayService mqtt, HttpContext ctx) =>
            {
                List<Device> devices;
                if (ctx.User.IsInRole("Admin"))
                    devices = await db.Devices.Where(d => d.AccessMode == AccessModes.Mqtt)
                        .OrderByDescending(d => d.CreatedAt).ToListAsync();
                else
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    devices = await (from gr in db.Grants
                                     join d in db.Devices on gr.DeviceId equals d.Id
                                     where gr.UserId == uid && gr.CanMonitor && d.AccessMode == AccessModes.Mqtt
                                     select d).ToListAsync();
                }

                var keys = devices.Where(d => !string.IsNullOrEmpty(d.ProductKey)).Select(d => d.ProductKey!).Distinct().ToList();
                var models = await db.ProductModels.Where(m => keys.Contains(m.ProductKey))
                    .ToDictionaryAsync(m => m.ProductKey, m => new { m.ModelName, m.ModelVersion, m.Revision });

                return Results.Ok(devices.Select(d =>
                {
                    var st = mqtt.GetState(d.Code);
                    models.TryGetValue(d.ProductKey ?? "", out var model);
                    return new
                    {
                        d.Code, d.Name, d.ProductKey,
                        modelName = model?.ModelName,
                        modelVersion = model?.ModelVersion,
                        online = st?.Online ?? false,
                        lastSeen = st?.LastSeenUtc,
                        hasImage = d.ImageData != null,
                    };
                }));
            });
        }

        // ==========================================================
        //  共用
        // ==========================================================

        /// <summary>MQTT 设备的实时数据快照。给网页和面板用同一个形状。</summary>
        internal static object TelemetrySnapshot(MqttGatewayService mqtt, Device dev)
        {
            var st = mqtt.GetState(dev.Code);
            if (st is null)
                return new { online = false, values = new JsonObject(), lastSeen = (DateTime?)null, note = "尚未收到该设备的任何上报" };

            return new
            {
                online = st.Online,
                values = mqtt.ValuesOf(dev.Code),
                lastSeen = st.LastSeenUtc,
                lastData = st.LastDataUtc,
                timestamp = st.LastTimestamp,
                firmwareVersion = st.FirmwareVersion,
                productKey = dev.ProductKey,
            };
        }

        /// <summary>取设备 + 校验当前用户权限(管理员直通)。</summary>
        private static async Task<(Device? dev, IResult? err)> Resolve(
            AppDbContext db, HttpContext ctx, string code, bool needControl)
        {
            var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
            if (dev is null) return (null, Results.NotFound(new { error = "设备不存在" }));
            if (ctx.User.IsInRole("Admin")) return (dev, null);

            var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var grant = await db.Grants.FirstOrDefaultAsync(x => x.UserId == uid && x.DeviceId == dev.Id);
            if (grant is null || !grant.CanMonitor)
                return (null, Results.Json(new { error = "无该设备访问权限" }, statusCode: 403));
            if (needControl && !grant.CanControl)
                return (null, Results.Json(new { error = "无该设备控制权限" }, statusCode: 403));
            return (dev, null);
        }

        /// <summary>表单里带了新图就存下来。返回非 null 表示出错。</summary>
        private static async Task<string?> ApplyImageAsync(IFormCollection form, Device dev)
        {
            var file = form.Files["image"];
            if (file is null || file.Length == 0) return null;
            if (file.Length > 2 * 1024 * 1024) return "图片不能超过 2MB";
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            dev.ImageData = ms.ToArray();
            dev.ImageMime = string.IsNullOrEmpty(file.ContentType) ? "image/png" : file.ContentType;
            return null;
        }
    }
}
