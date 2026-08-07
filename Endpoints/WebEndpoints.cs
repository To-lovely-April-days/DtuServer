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
            g.MapGet("/devices", async (AppDbContext db, DtuServerManager mgr, HttpContext ctx) =>
            {
                if (ctx.User.IsInRole("Admin"))
                {
                    // 用 SQL 投影出 HasImage(不把图片字节读进内存)
                    var all = await db.Devices.OrderByDescending(d => d.CreatedAt)
                        .Select(d => new { d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, HasImage = d.ImageData != null })
                        .ToListAsync();
                    return Results.Ok(all.Select(d => new
                    {
                        d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation,
                        online = mgr.IsOnline(d.DtuSerial),
                        remote = mgr.GetRemote(d.DtuSerial),
                        canControl = true,
                        hasImage = d.HasImage
                    }));
                }

                var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var rows = await (from gr in db.Grants
                                  join d in db.Devices on gr.DeviceId equals d.Id
                                  where gr.UserId == uid && gr.CanMonitor
                                  select new { d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, HasImage = d.ImageData != null, gr.CanControl }).ToListAsync();
                return Results.Ok(rows.Select(r => new
                {
                    r.Id, r.Code, r.Name, r.DeviceType, r.DtuSerial, r.ModbusStation,
                    online = mgr.IsOnline(r.DtuSerial),
                    remote = mgr.GetRemote(r.DtuSerial),
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

            // 设备二维码(内容=序列号)。管理员或有监控授权可看。
            g.MapGet("/devices/{code}/qr.png", async (string code, AppDbContext db, HttpContext ctx) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    var ok = await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanMonitor);
                    if (!ok) return Results.Forbid();
                }
                return Results.File(QrService.PngFor(dev.DtuSerial, 10), "image/png");
            });

            // 设备型号目录(型号 → 控制面板地址)。给前端:添加设备下拉 + 按型号弹对应面板。
            g.MapGet("/device-models", (DeviceModelCatalog catalog) =>
                Results.Ok(catalog.All.Select(m => new { m.Key, m.Name, m.PanelUrl })));

            // 实时测点(按设备型号对应的通讯档案读)。需监控权限。
            g.MapGet("/devices/{code}/telemetry", async (string code, AppDbContext db, DtuServerManager mgr, DeviceProfileRegistry profiles, DeviceModelCatalog catalog, HttpContext ctx) =>
            {
                var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
                if (dev == null) return Results.NotFound();
                if (!ctx.User.IsInRole("Admin"))
                {
                    var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                    if (!await db.Grants.AnyAsync(x => x.UserId == uid && x.DeviceId == dev.Id && x.CanMonitor))
                        return Results.Forbid();
                }
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
