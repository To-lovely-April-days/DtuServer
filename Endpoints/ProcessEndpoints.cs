using MaxChemical.DtuServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>
    /// 工艺组态上云:
    ///  - 桌面「上云」→ POST /ingest/process/svg | /ingest/process/telemetry(带 X-Upload-Key,无需 Cookie/Bearer,便于本地直传)
    ///  - 网页展示   → GET  /web/process/svg | /web/process/telemetry(浏览器 Cookie 鉴权)
    /// 上传密钥:appsettings 的 Process:UploadKey;留空=不校验(仅建议内网/本机)。
    /// </summary>
    public static class ProcessEndpoints
    {
        public record UploadSvgReq(string? Name, string Svg);
        public record UploadTelemetryReq(string? Name, object? Values);
        public record UploadDoeReq(string? Name, object? Doe);
        // 网页远程填结果:提交某一组(batchId+runIndex)的测量响应值
        public record SubmitResponseReq(string? Name, string BatchId, int RunIndex, Dictionary<string, double> Values);
        // 实时曲线:一帧 = 某时刻(epoch ms)多个指标的值;一批含多帧(这一秒的所有样本,不丢)
        public record SeriesFrame(long T, Dictionary<string, double> Values);
        public record UploadSeriesReq(string? Name, List<SeriesFrame> Frames);

        public static void MapProcess(this IEndpointRouteBuilder app, IConfiguration config)
        {
            var uploadKey = config["Process:UploadKey"] ?? "";

            bool KeyOk(HttpContext ctx) =>
                string.IsNullOrEmpty(uploadKey) ||
                (ctx.Request.Headers.TryGetValue("X-Upload-Key", out var k) && k == uploadKey);

            // ---- 桌面直传(键鉴权)----
            app.MapPost("/ingest/process/svg", (UploadSvgReq req, ProcessStore store, HttpContext ctx) =>
            {
                if (!KeyOk(ctx)) return Results.Json(new { error = "upload key 无效" }, statusCode: 401);
                if (string.IsNullOrWhiteSpace(req.Svg)) return Results.BadRequest(new { error = "svg 为空" });
                store.PutSvg(req.Name, req.Svg);
                return Results.Ok(new { ok = true, name = req.Name ?? "default", bytes = req.Svg.Length });
            });

            app.MapPost("/ingest/process/telemetry", (UploadTelemetryReq req, ProcessStore store, HttpContext ctx) =>
            {
                if (!KeyOk(ctx)) return Results.Json(new { error = "upload key 无效" }, statusCode: 401);
                var json = System.Text.Json.JsonSerializer.Serialize(req.Values ?? new { });
                store.PutTelemetry(req.Name, json);
                return Results.Ok(new { ok = true });
            });

            // 桌面推 DOE 快照(批次名 / 运行表 / 当前组 / 状态)
            app.MapPost("/ingest/process/doe", (UploadDoeReq req, ProcessStore store, HttpContext ctx) =>
            {
                if (!KeyOk(ctx)) return Results.Json(new { error = "upload key 无效" }, statusCode: 401);
                var json = System.Text.Json.JsonSerializer.Serialize(req.Doe ?? new { });
                store.PutDoe(req.Name, json);
                return Results.Ok(new { ok = true });
            });

            // 桌面批量上报实时曲线样本(这一秒的所有帧,不丢数据)
            app.MapPost("/ingest/process/series", (UploadSeriesReq req, ProcessStore store, HttpContext ctx) =>
            {
                if (!KeyOk(ctx)) return Results.Json(new { error = "upload key 无效" }, statusCode: 401);
                if (req.Frames != null)
                    foreach (var f in req.Frames)
                        if (f?.Values != null) store.AppendSeries(req.Name, f.T, f.Values);
                return Results.Ok(new { ok = true });
            });

            // 桌面轮询:某一组是否已被网页远程填了结果?有则取走(一次性),没有则 204。
            app.MapGet("/ingest/process/doe/response", (ProcessStore store, HttpContext ctx, string? name, string batchId, int runIndex) =>
            {
                if (!KeyOk(ctx)) return Results.Json(new { error = "upload key 无效" }, statusCode: 401);
                var json = store.TakeResponse(name, batchId, runIndex);
                return json == null ? Results.NoContent() : Results.Content(json, "application/json");
            });

            // ---- 网页读取(Cookie 鉴权)----
            app.MapGet("/web/process/svg", (ProcessStore store, string? name) =>
            {
                var snap = store.GetSvg(name);
                return snap == null
                    ? Results.NotFound()
                    : Results.Content(snap.Value.Svg, "image/svg+xml");
            }).RequireAuthorization();

            app.MapGet("/web/process/telemetry", (ProcessStore store, string? name) =>
            {
                var t = store.GetTelemetry(name);
                return Results.Content(t?.Json ?? "{}", "application/json");
            }).RequireAuthorization();

            // 版本号 + 真实项目名:网页轮询它,变了就自动重载;name 用于显示真实项目名并作查询键
            app.MapGet("/web/process/version", (ProcessStore store, string? name) =>
                Results.Json(new { ts = store.SvgVersion(name), name = store.ResolveName(name) ?? "" })).RequireAuthorization();

            // ---- DOE 面板(Cookie 鉴权)----
            app.MapGet("/web/process/doe", (ProcessStore store, string? name) =>
            {
                var d = store.GetDoe(name);
                return Results.Content(d?.Json ?? "{}", "application/json");
            }).RequireAuthorization();

            app.MapGet("/web/process/doe/version", (ProcessStore store, string? name) =>
                Results.Json(new { ts = store.DoeVersion(name) })).RequireAuthorization();

            // 网页画曲线:取某指标 key 的历史点(since>0 只取增量)。points=[[t,v],...]
            app.MapGet("/web/process/history", (ProcessStore store, string? name, string key, long? since) =>
            {
                var pts = store.GetHistory(name, key, since ?? 0);
                var arr = new object[pts.Count];
                for (int i = 0; i < pts.Count; i++) arr[i] = new object[] { pts[i].T, pts[i].V };
                return Results.Json(new { key, points = arr });
            }).RequireAuthorization();

            // 网页远程填结果:提交某一组的测量值,桌面 DOEBatchExecutor 会轮询取走并完成该组
            app.MapPost("/web/process/doe/response", (SubmitResponseReq req, ProcessStore store) =>
            {
                if (string.IsNullOrWhiteSpace(req.BatchId) || req.Values == null || req.Values.Count == 0)
                    return Results.BadRequest(new { error = "batchId/values 不能为空" });
                store.PutResponse(req.Name, req.BatchId, req.RunIndex, System.Text.Json.JsonSerializer.Serialize(req.Values));
                return Results.Ok(new { ok = true });
            }).RequireAuthorization();
        }
    }
}
