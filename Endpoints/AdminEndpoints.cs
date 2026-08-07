using System.Security.Claims;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Services;
using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>管理员后台:设备增删查、二维码、用户列表、授权(用户×设备 权限)开关。需 Admin 角色。</summary>
    public static class AdminEndpoints
    {
        public record DeviceReq(string Name, string DeviceType, string DtuSerial, int ModbusStation);
        public record GrantReq(string UserId, string DeviceId, bool CanMonitor, bool CanControl);

        public static void MapAdmin(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/admin").RequireAuthorization("AdminOnly");

            // ---- 设备 ----
            g.MapGet("/devices", async (AppDbContext db, DtuServerManager mgr) =>
            {
                var list = await db.Devices.OrderByDescending(d => d.CreatedAt).ToListAsync();
                return Results.Ok(list.Select(d => new
                {
                    d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, d.CreatedAt,
                    online = mgr.IsOnline(d.DtuSerial)
                }));
            });

            // 添加设备:multipart 表单(name/deviceType/modbusStation + 可选 image 图片)
            g.MapPost("/devices", async (HttpContext ctx, AppDbContext db) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var name = form["name"].ToString().Trim();
                var deviceType = form["deviceType"].ToString().Trim();
                int station = int.TryParse(form["modbusStation"], out var s) ? s : 1;
                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest(new { error = "设备名不能为空" });

                // 自动生成 DTU 登录包序列号(同时作为对外标识码),用户把它手填进 DTU。
                string serial = SecurityUtil.NewDtuSerial();
                for (int i = 0; i < 5 && await db.Devices.AnyAsync(d => d.DtuSerial == serial || d.Code == serial); i++)
                    serial = SecurityUtil.NewDtuSerial();

                var dev = new Device
                {
                    Code = serial,
                    Name = name,
                    DeviceType = deviceType,
                    DtuSerial = serial,
                    ModbusStation = station <= 0 ? 1 : station,
                    CreatedByUserId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                };

                var file = form.Files["image"];
                if (file != null && file.Length > 0)
                {
                    if (file.Length > 2 * 1024 * 1024)
                        return Results.BadRequest(new { error = "图片不能超过 2MB" });
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    dev.ImageData = ms.ToArray();
                    dev.ImageMime = string.IsNullOrEmpty(file.ContentType) ? "image/png" : file.ContentType;
                }

                db.Devices.Add(dev);
                await db.SaveChangesAsync();
                return Results.Ok(new { dev.Id, dev.Code, dev.Name, dev.DeviceType, dev.DtuSerial, dev.ModbusStation });
            });

            // 编辑设备:multipart(name/deviceType/modbusStation + 可选新 image)。序列号不可改。
            g.MapPut("/devices/{id}", async (string id, HttpContext ctx, AppDbContext db) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev == null) return Results.NotFound();

                var form = await ctx.Request.ReadFormAsync();
                var name = form["name"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest(new { error = "设备名不能为空" });
                dev.Name = name;
                dev.DeviceType = form["deviceType"].ToString().Trim();
                if (int.TryParse(form["modbusStation"], out var st) && st > 0) dev.ModbusStation = st;

                var file = form.Files["image"];
                if (file != null && file.Length > 0)
                {
                    if (file.Length > 2 * 1024 * 1024)
                        return Results.BadRequest(new { error = "图片不能超过 2MB" });
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    dev.ImageData = ms.ToArray();
                    dev.ImageMime = string.IsNullOrEmpty(file.ContentType) ? "image/png" : file.ContentType;
                }

                await db.SaveChangesAsync();
                return Results.Ok(new { dev.Id, dev.Code, dev.Name, dev.DeviceType, dev.ModbusStation });
            });

            g.MapDelete("/devices/{id}", async (string id, AppDbContext db) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev == null) return Results.NotFound();
                db.Devices.Remove(dev);
                // 一并清理该设备的授权
                var grants = db.Grants.Where(x => x.DeviceId == id);
                db.Grants.RemoveRange(grants);
                await db.SaveChangesAsync();
                return Results.Ok();
            });

            // 二维码 PNG:内容就是设备序列号(=DTU 登录包),扫码即得序列号,方便填入 DTU/记录
            g.MapGet("/devices/{id}/qr.png", async (string id, AppDbContext db) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev == null) return Results.NotFound();
                var png = QrService.PngFor(dev.DtuSerial, 10);
                return Results.File(png, "image/png");
            });

            // ---- 用户(用于授权选择)----
            g.MapGet("/users", async (AppDbContext db) =>
                Results.Ok(await db.Users
                    .OrderBy(u => u.Username)
                    .Select(u => new { u.Id, u.Username, u.Email, u.Role, u.Disabled })
                    .ToListAsync()));

            // ---- 授权(用户 × 设备 权限)----
            g.MapGet("/grants", async (string deviceId, AppDbContext db) =>
            {
                var grants = await db.Grants.Where(x => x.DeviceId == deviceId).ToListAsync();
                var userIds = grants.Select(x => x.UserId).ToList();
                var users = await db.Users.Where(u => userIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.Username);
                var rows = grants.Select(x => new
                {
                    x.Id, x.UserId,
                    Username = users.TryGetValue(x.UserId, out var n) ? n : x.UserId,
                    x.CanMonitor, x.CanControl, x.CreatedAt
                });
                return Results.Ok(rows);
            });

            // 新增/更新授权(同一用户×设备唯一)
            g.MapPost("/grants", async (GrantReq req, AppDbContext db) =>
            {
                if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.DeviceId))
                    return Results.BadRequest(new { error = "缺少 userId 或 deviceId" });

                var grant = await db.Grants.FirstOrDefaultAsync(x => x.UserId == req.UserId && x.DeviceId == req.DeviceId);
                if (grant == null)
                {
                    grant = new Grant { UserId = req.UserId, DeviceId = req.DeviceId };
                    db.Grants.Add(grant);
                }
                grant.CanMonitor = req.CanMonitor;
                grant.CanControl = req.CanControl;
                await db.SaveChangesAsync();
                return Results.Ok(grant);
            });

            g.MapDelete("/grants/{id}", async (string id, AppDbContext db) =>
            {
                var grant = await db.Grants.FindAsync(id);
                if (grant == null) return Results.NotFound();
                db.Grants.Remove(grant);
                await db.SaveChangesAsync();
                return Results.Ok();
            });
        }

        /// <summary>对外可访问的基础 URL:优先用配置 PublicBaseUrl(贴二维码要稳定),否则用当前请求。</summary>
        internal static string PublicBaseUrl(HttpContext ctx, IConfiguration cfg)
        {
            var configured = cfg["PublicBaseUrl"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        }
    }
}
