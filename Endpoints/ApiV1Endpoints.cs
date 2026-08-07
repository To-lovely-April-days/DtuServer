using System.Security.Claims;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Services;
using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>
    /// 对外开放 API(第三方 App 调用)。鉴权:先用 API Key/Secret 换 Bearer 访问令牌,
    /// 之后所有请求带 Authorization: Bearer xxx。权限按"用户×设备"授权关系强制校验。
    /// </summary>
    public static class ApiV1Endpoints
    {
        public record TokenReq(string ApiKey, string ApiSecret);
        public record BindReq(string Code);
        public record CommandReq(string Command, Dictionary<string, object>? Parameters);

        public static void MapApiV1(this IEndpointRouteBuilder app)
        {
            var pub = app.MapGroup("/api/v1");

            // 用 API Key/Secret 换访问令牌(唯一无需 Bearer 的端点)
            pub.MapPost("/token", async (TokenReq req, AppDbContext db, TokenService tokens) =>
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.ApiKey == req.ApiKey);
                if (user == null || user.Disabled ||
                    !SecurityUtil.Verify(req.ApiSecret ?? "", user.ApiSecretHash, user.ApiSecretSalt))
                    return Results.Json(new { error = "API Key 或 Secret 无效" }, statusCode: 401);

                var (token, expiresIn) = tokens.Issue(user);
                return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresIn });
            })
            .WithSummary("用 API Key/Secret 换取访问令牌");

            // 以下端点都需要 Bearer 令牌
            var api = app.MapGroup("/api/v1").RequireAuthorization("ApiBearer");

            // 绑定设备:扫码/输码后调用,给当前用户建立"可监控"授权
            api.MapPost("/devices/bind", async (BindReq req, AppDbContext db, HttpContext ctx) =>
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
            })
            .WithSummary("绑定设备(扫码/输码),默认获得只读权限");

            // 我能访问的设备列表(含在线状态)
            api.MapGet("/devices", async (AppDbContext db, DtuServerManager mgr, HttpContext ctx) =>
            {
                var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var rows = await (from gr in db.Grants
                                  join d in db.Devices on gr.DeviceId equals d.Id
                                  where gr.UserId == uid && gr.CanMonitor
                                  select new { d, gr }).ToListAsync();

                return Results.Ok(rows.Select(r => new
                {
                    r.d.Code, r.d.Name, r.d.DeviceType,
                    online = mgr.IsOnline(r.d.DtuSerial),
                    canControl = r.gr.CanControl
                }));
            })
            .WithSummary("列出当前令牌可访问的设备");

            // 设备详情
            api.MapGet("/devices/{code}", async (string code, AppDbContext db, DtuServerManager mgr, HttpContext ctx) =>
            {
                var (dev, grant, err) = await Resolve(db, ctx, code, needControl: false);
                if (err != null) return err;
                return Results.Ok(new
                {
                    dev!.Code, dev.Name, dev.DeviceType,
                    online = mgr.IsOnline(dev.DtuSerial),
                    canMonitor = grant!.CanMonitor, canControl = grant.CanControl
                });
            })
            .WithSummary("设备信息与在线状态");

            // 实时/最新数据 —— P3 接入寄存器模板后返回真实测点;当前先返回在线状态占位
            api.MapGet("/devices/{code}/telemetry", async (string code, AppDbContext db, DtuServerManager mgr, HttpContext ctx) =>
            {
                var (dev, _, err) = await Resolve(db, ctx, code, needControl: false);
                if (err != null) return err;
                return Results.Ok(new
                {
                    dev!.Code,
                    online = mgr.IsOnline(dev.DtuSerial),
                    values = new { },                 // P3: 釜温/压力/转速 等真实测点
                    note = "telemetry 测点将在 P3(设备类型模板)接入后返回"
                });
            })
            .WithSummary("设备实时数据(P3 接入测点)");

            // 下控制命令 —— 需要控制权限;P3 接入寄存器模板 + 写后回读 + 限值钳制后真正执行
            api.MapPost("/devices/{code}/commands", async (string code, CommandReq req, AppDbContext db, HttpContext ctx) =>
            {
                var (dev, _, err) = await Resolve(db, ctx, code, needControl: true);
                if (err != null) return err;
                return Results.Json(new
                {
                    error = "控制执行将在 P3(寄存器模板 + 写后回读 + 限值校验)接入后开放",
                    requested = new { dev!.Code, req.Command, req.Parameters }
                }, statusCode: 501);
            })
            .WithSummary("下发控制命令(需控制权限,P3 接入执行)");
        }

        /// <summary>解析设备 + 校验当前用户对其的权限。needControl=true 时要求控制权限。</summary>
        private static async Task<(Device? dev, Grant? grant, IResult? err)> Resolve(
            AppDbContext db, HttpContext ctx, string code, bool needControl)
        {
            var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var dev = await db.Devices.FirstOrDefaultAsync(d => d.Code == code);
            if (dev == null) return (null, null, Results.NotFound(new { error = "设备不存在" }));

            var grant = await db.Grants.FirstOrDefaultAsync(x => x.UserId == uid && x.DeviceId == dev.Id);
            if (grant == null || !grant.CanMonitor)
                return (null, null, Results.Json(new { error = "无该设备访问权限" }, statusCode: 403));
            if (needControl && !grant.CanControl)
                return (null, null, Results.Json(new { error = "无该设备控制权限" }, statusCode: 403));

            return (dev, grant, null);
        }
    }
}
