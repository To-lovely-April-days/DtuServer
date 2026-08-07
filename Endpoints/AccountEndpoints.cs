using System.Security.Claims;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Endpoints
{
    /// <summary>账号体系:自助注册 / 登录 / 登出 / 我的信息 / 重置 API 凭证。Web 用 Cookie 会话。</summary>
    public static class AccountEndpoints
    {
        public record RegisterReq(string Username, string Email, string Password);
        public record LoginReq(string Username, string Password);

        public static void MapAccount(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/auth");

            // 自助注册:创建用户 + 立即签发一对 API Key/Secret(Secret 只此一次返回)
            g.MapPost("/register", async (RegisterReq req, AppDbContext db, HttpContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3)
                    return Results.BadRequest(new { error = "用户名至少 3 个字符" });
                if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                    return Results.BadRequest(new { error = "密码至少 6 位" });
                if (await db.Users.AnyAsync(u => u.Username == req.Username))
                    return Results.Conflict(new { error = "用户名已存在" });

                var (ph, ps) = SecurityUtil.Hash(req.Password);
                var apiKey = SecurityUtil.NewApiKey();
                var apiSecret = SecurityUtil.NewApiSecret();
                var (sh, ss) = SecurityUtil.Hash(apiSecret);

                var user = new User
                {
                    Username = req.Username.Trim(),
                    Email = req.Email?.Trim() ?? "",
                    Role = "User",
                    PasswordHash = ph,
                    PasswordSalt = ps,
                    ApiKey = apiKey,
                    ApiSecretHash = sh,
                    ApiSecretSalt = ss,
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();

                await SignInCookie(ctx, user);
                return Results.Ok(new { user.Username, user.Role, apiKey, apiSecret });
            });

            g.MapPost("/login", async (LoginReq req, AppDbContext db, HttpContext ctx) =>
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
                if (user == null || user.Disabled || !SecurityUtil.Verify(req.Password, user.PasswordHash, user.PasswordSalt))
                    return Results.Json(new { error = "用户名或密码错误" }, statusCode: 401);

                await SignInCookie(ctx, user);
                return Results.Ok(new { user.Username, user.Role });
            });

            g.MapPost("/logout", async (HttpContext ctx) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Ok();
            }).RequireAuthorization();

            g.MapGet("/me", async (AppDbContext db, HttpContext ctx) =>
            {
                var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var user = await db.Users.FindAsync(id);
                if (user == null) return Results.Unauthorized();
                return Results.Ok(new { user.Username, user.Email, user.Role, user.ApiKey });
            }).RequireAuthorization();

            // 重置 API 凭证(旧的立即失效),返回新的 Key + Secret(Secret 只此一次)
            g.MapPost("/regenerate-key", async (AppDbContext db, HttpContext ctx) =>
            {
                var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var user = await db.Users.FindAsync(id);
                if (user == null) return Results.Unauthorized();

                var apiKey = SecurityUtil.NewApiKey();
                var apiSecret = SecurityUtil.NewApiSecret();
                var (sh, ss) = SecurityUtil.Hash(apiSecret);
                user.ApiKey = apiKey;
                user.ApiSecretHash = sh;
                user.ApiSecretSalt = ss;
                await db.SaveChangesAsync();
                return Results.Ok(new { apiKey, apiSecret });
            }).RequireAuthorization();
        }

        private static async Task SignInCookie(HttpContext ctx, User user)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }
    }
}
