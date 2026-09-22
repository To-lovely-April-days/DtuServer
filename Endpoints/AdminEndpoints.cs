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
            // mode 可选:passthrough(仅透传) / mqtt(仅MQTT网关) / 不传=全部。
            // 不传时行为与以前一致,老前端不受影响。
            g.MapGet("/devices", async (AppDbContext db, DtuServerManager mgr, MqttGatewayService mqtt, string? mode) =>
            {
                var q = db.Devices.AsQueryable();
                if (string.Equals(mode, "passthrough", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(d => d.AccessMode != AccessModes.Mqtt);
                else if (string.Equals(mode, "mqtt", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(d => d.AccessMode == AccessModes.Mqtt);

                var list = await q.OrderByDescending(d => d.CreatedAt).ToListAsync();
                return Results.Ok(list.Select(d => new
                {
                    d.Id, d.Code, d.Name, d.DeviceType, d.DtuSerial, d.ModbusStation, d.CreatedAt,
                    accessMode = AccessModes.Normalize(d.AccessMode),
                    d.ProductKey,
                    // 在线状态按接入方式各查各的
                    online = AccessModes.IsMqtt(d.AccessMode) ? mqtt.IsOnline(d.Code) : mgr.IsOnline(d.DtuSerial)
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

                // 序列号(同时作为对外标识码):表单填了就用手填的,没填就自动生成。
                var (serial, codeErr) = await ResolveNewDeviceCodeAsync(db, form["code"].ToString());
                if (codeErr is not null) return Results.BadRequest(new { error = codeErr });

                var dev = new Device
                {
                    Code = serial!,
                    Name = name,
                    DeviceType = deviceType,
                    DtuSerial = serial!,
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
                // MQTT 设备有自己的编辑入口(型号=物模型,不是内置型号),别让老表单把它的 DeviceType 冲掉
                if (AccessModes.IsMqtt(dev.AccessMode))
                    return Results.BadRequest(new { error = "该设备是 MQTT 网关设备,请到「网关设备」页面编辑" });

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
                // 以及它的告警记录(MQTT 设备才会有)
                db.DeviceAlarms.RemoveRange(db.DeviceAlarms.Where(a => a.DeviceCode == dev.Code));
                await db.SaveChangesAsync();
                return Results.Ok();
            });

            // 二维码 PNG。透传设备:内容仍是序列号(=DTU 登录包),扫码即得序列号,方便填入 DTU。
            // MQTT 设备:按物模型 meta.qrCode 生成绑定链接,微信扫了能直接打开 bind.html。
            g.MapGet("/devices/{id}/qr.png", async (string id, HttpContext ctx, AppDbContext db, IConfiguration cfgRoot) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev == null) return Results.NotFound();
                var text = await QrService.ContentForDeviceAsync(db, dev, PublicBaseUrl(ctx, cfgRoot));
                return Results.File(QrService.PngFor(text, 10), "image/png");
            });

            // 二维码里到底写了什么 —— 前端弹窗要显示这串文本,不能自己猜
            g.MapGet("/devices/{id}/qr-content", async (string id, HttpContext ctx, AppDbContext db, IConfiguration cfgRoot) =>
            {
                var dev = await db.Devices.FindAsync(id);
                if (dev == null) return Results.NotFound();
                var text = await QrService.ContentForDeviceAsync(db, dev, PublicBaseUrl(ctx, cfgRoot));
                // isUrl 直接看内容本身,不靠"跟序列号不一样"去反推 —— 那个前提以后可能不成立
                var isUrl = Uri.TryCreate(text, UriKind.Absolute, out var u) &&
                            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
                return Results.Ok(new { dev.Code, content = text, isUrl });
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

        /// <summary>设备标识码只允许出现在 URL 路径/查询里安全的字符,否则二维码链接和拉配置的路由都会歪。</summary>
        internal static readonly System.Text.RegularExpressions.Regex DeviceCodePattern =
            new(@"^[A-Za-z0-9_-]{2,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 决定新设备的标识码。表单填了就用手填的 —— 网关序列号出厂就烧死了(比如 00110)的场景,
        /// 平台这边必须能对上那个号;没填就照旧自动生成。透传设备和 MQTT 设备共用这一套规则。
        /// </summary>
        internal static async Task<(string? code, string? error)> ResolveNewDeviceCodeAsync(AppDbContext db, string? requested)
        {
            var code = (requested ?? "").Trim();
            if (code.Length == 0)
            {
                var gen = SecurityUtil.NewDtuSerial();
                for (int i = 0; i < 5 && await db.Devices.AnyAsync(d => d.DtuSerial == gen || d.Code == gen); i++)
                    gen = SecurityUtil.NewDtuSerial();
                return (gen, null);
            }

            if (!DeviceCodePattern.IsMatch(code))
                return (null, "设备标识码只能用字母、数字、下划线、短横线,长度 2-64");
            if (await db.Devices.AnyAsync(d => d.Code == code || d.DtuSerial == code))
                return (null, $"设备标识码 “{code}” 已被占用");
            return (code, null);
        }
    }
}
