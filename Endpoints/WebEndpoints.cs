using System.Security.Claims;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Devices;
using MaxChemical.DtuServer.Services;
using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>
    /// 网页(Cookie 会话)用的端点:扫码绑定、我的设备列表。
    /// 与开放 API(/api/v1, Bearer)等价,只是给浏览器用户用 Cookie 鉴权。
    /// </summary>
    public static class WebEndpoints
    {
        public record BindReq(string Code);
        public record ControlReq(string Command, double Value);

        public static void MapWeb(this IEndpointRouteBuilder app)
        {
            // 扫码落地页的短链。规范里的模板写的是 /bind?gw=xxx&pk=yyy,平台页面却是 /bind.html?code=xxx,
            // 这里统一跳过去 —— 两种模板(自己写的、点过「用平台配置填充」的)扫出来都能打开。
            // 不加鉴权:bind.html 自己会把未登录的人送去登录页再跳回来。
            app.MapGet("/bind", (HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var code = new[] { "code", "gw", "gatewayId", "deviceId", "sn" }
                    .Select(k => q[k].ToString())
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                return Results.Redirect(string.IsNullOrWhiteSpace(code)
                    ? "/bind.html"
                    : "/bind.html?code=" + Uri.EscapeDataString(code));
            });

            var g = app.MapGroup("/web").RequireAuthorization();

            // 扫码/输码绑定(浏览器):给当前登录用户建立"可监控"授权
            g.MapPost("/bind", async (BindReq req, AppDbContext db, HttpContext ctx) =>
            {
                var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == req.Code);
                if (dev == null) return Results.NotFound(new { error = "设备标识码不存在" });

                var grant = await db.Grants.FirstOrDefaultAsync(x => x.UserId == uid && x.DeviceId == dev.Id);
                if (grant == null)
                {
                    grant = new Grant { UserId = uid, DeviceId = dev.Id, CanMonitor = true, CanControl = false };
                    db.Grants.Add(grant);
                    await db.SaveChangesAsync();
                }
                return Results.Ok(new { dev.Code, dev.Name, dev.DeviceType, grant.CanMonitor, grant.CanControl });
            });

            // 设备列表 + 在线状态。管理员看全部;普通用户看自己绑定且可监控的。
            // 两种接入方式混在一列里返回,靠 accessMode 区分:
            //   Passthrough → 在线状态查 DTU 连接表;Mqtt → 查 MQTT 上报缓存。
            g.MapGet("/devices", async (AppDbContext db, DtuServerManager mgr, MqttGatewayService mqtt, HttpContext ctx) =>
            {
                if (ctx.User.IsInRole("Admin"))
                {
                    // 用 SQL 投影出 HasImage(不把图片字节读进内存)
                    var all = await db.Devices.OrderByDescending(d => d.CreatedAt)
                        .Select(d => new { d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, d.AccessMode, d.ProductKey, HasImage = d.ImageData != null })
                        .ToListAsync();
                    return Results.Ok(all.Select(d => new
                    {
                        d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation,
                        accessMode = AccessModes.Normalize(d.AccessMode),
                        d.ProductKey,
                        online = AccessModes.IsMqtt(d.AccessMode) ? mqtt.IsOnline(d.Code) : mgr.IsOnline(d.DtuSerial),
                        remote = AccessModes.IsMqtt(d.AccessMode) ? null : mgr.GetRemote(d.DtuSerial),
                        canControl = true,
                        hasImage = d.HasImage
                    }));
                }

                var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var rows = await (from gr in db.Grants
                                  join d in db.Devices on gr.DeviceId equals d.Id
                                  where gr.UserId == uid && gr.CanMonitor
                                  select new { d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, d.AccessMode, d.ProductKey, HasImage = d.ImageData != null, gr.CanControl }).ToListAsync();
                return Results.Ok(rows.Select(r => new
                {
                    r.Id, r.Code, r.Name, r.DeviceType, r.DtuSerial, r.ModbusStation,
                    accessMode = AccessModes.Normalize(r.AccessMode),
                    r.ProductKey,
                    online = AccessModes.IsMqtt(r.AccessMode) ? mqtt.IsOnline(r.Code) : mgr.IsOnline(r.DtuSerial),
                    remote = AccessModes.IsMqtt(r.AccessMode) ? null : mgr.GetRemote(r.DtuSerial),
                    canControl = r.CanControl,
                    hasImage = r.HasImage
                }));
            });

            // 设备图片(已绑定可监控 或 管理员 可看)
            g.MapGet("/devices/{code}/image", async (string code, AppDbContext db, HttpContext ctx) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev?.ImageData == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    var ok = await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanMonitor);
                    if (!ok) return Results.Forbid();
                }
                return Results.File(dev.ImageData, dev.ImageMime ?? "image/png");
            });

            // 设备二维码。透传设备内容=序列号;MQTT 设备按物模型生成绑定链接。管理员或有监控授权可看。
            g.MapGet("/devices/{code}/qr.png", async (string code, AppDbContext db, HttpContext ctx, IConfiguration cfgRoot) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    var ok = await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanMonitor);
                    if (!ok) return Results.Forbid();
                }
                var text = await QrService.ContentForDeviceAsync(db, dev, AdminEndpoints.PublicBaseUrl(ctx, cfgRoot));
                return Results.File(QrService.PngFor(text, 10), "image/png");
            });

            // 设备型号目录(型号 → 控制面板地址)。给前端:添加设备下拉 + 按型号弹对应面板。
            g.MapGet("/device-models", (DeviceModelCatalog catalog) =>
                Results.Ok(catalog.All.Select(m => new { m.Key, m.Name, m.PanelUrl })));

            // 实时测点。需监控权限。
            //   MQTT 设备  → 直接读平台缓存的最新上报值
            //   透传设备  → 现场问 Modbus(原有逻辑,一字未动)
            g.MapGet("/devices/{code}/telemetry", async (string code, AppDbContext db, DtuServerManager mgr, MqttGatewayService mqtt, DeviceProfileRegistry profiles, DeviceModelCatalog catalog, HttpContext ctx) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    if (!await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanMonitor))
                        return Results.Forbid();
                }
                if (AccessModes.IsMqtt(dev.AccessMode))
                    return Results.Ok(MqttDeviceEndpoints.TelemetrySnapshot(mqtt, dev));

                if (!mgr.IsOnline(dev.DtuSerial)) return Results.Ok(new { online = false });
                // 型号 → 通讯档案 Key(未登记型号则回退:直接把 DeviceType 当档案 Key,兼容老设备)
                var profileKey = catalog.Get(dev.DeviceType)?.ProfileKey ?? dev.DeviceType;
                var profile = profiles.Get(profileKey);
                if (profile == null) return Results.Ok(new { online = true, unsupported = true, deviceType = dev.DeviceType });
                try
                {
                    var values = await profile.ReadTelemetryAsync(mgr, dev.DtuSerial, (byte)dev.ModbusStation, ctx.RequestAborted);
                    return Results.Ok(new { online = true, values });
                }
                catch (Exception ex) { return Results.Ok(new { online = true, error = ex.Message }); }
            });

            // 下控制(按设备型号对应的通讯档案写)。需控制权限。
            g.MapPost("/devices/{code}/control", async (string code, ControlReq req, AppDbContext db, DtuServerManager mgr, DeviceProfileRegistry profiles, DeviceModelCatalog catalog, HttpContext ctx) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    if (!await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanControl))
                        return Results.Json(new { error = "无该设备控制权限" }, statusCode: 403);
                }
                // MQTT 设备的指令是多参数的(按物模型 inputParams),这个单值端点表达不了 → 走 /command
                if (AccessModes.IsMqtt(dev.AccessMode))
                    return Results.Json(new { error = "MQTT 网关设备请使用 /web/devices/{code}/command 下发指令" }, statusCode: 400);
                if (!mgr.IsOnline(dev.DtuSerial)) return Results.Json(new { error = "设备离线" }, statusCode: 409);
                var profileKey = catalog.Get(dev.DeviceType)?.ProfileKey ?? dev.DeviceType;
                var profile = profiles.Get(profileKey);
                if (profile == null) return Results.Json(new { error = "该设备型号暂不支持控制" }, statusCode: 400);
                try
                {
                    var ok = await profile.WriteControlAsync(mgr, dev.DtuSerial, (byte)dev.ModbusStation,
                        req.Command, new Dictionary<string, double> { ["value"] = req.Value }, ctx.RequestAborted);
                    return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = "下发失败" }, statusCode: 502);
                }
                catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
            });
        }
    }
}
