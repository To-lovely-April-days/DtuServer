using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MaxChemical.DtuServer.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>开放 API 的访问令牌(JWT/HS256)签发与密钥。Web 看板用 Cookie,App 用此 Bearer 令牌。</summary>
    public class TokenService
    {
        private readonly SymmetricSecurityKey _key;
        private readonly int _ttlMinutes;

        public TokenService(IConfiguration cfg)
        {
            _key = ResolveKey(cfg);
            _ttlMinutes = cfg.GetValue<int?>("Jwt:TtlMinutes") ?? 720; // 默认 12 小时
        }

        /// <summary>从配置解析 HS256 密钥(Jwt:Secret),长度不足时回退到内置开发密钥(仅联调用)。</summary>
        public static SymmetricSecurityKey ResolveKey(IConfiguration cfg)
        {
            var secret = cfg["Jwt:Secret"];
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
                secret = "DEV-ONLY-INSECURE-please-set-Jwt-Secret-to-32+chars";
            return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        }

        /// <summary>用用户身份签发访问令牌,返回 (token, 有效期秒)。</summary>
        public (string token, int expiresIn) Issue(User u)
        {
            var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, u.Id),
                new Claim("uname", u.Username),
                new Claim(ClaimTypes.Role, u.Role),
            };
            var jwt = new JwtSecurityToken(
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(_ttlMinutes),
                signingCredentials: creds);
            return (new JwtSecurityTokenHandler().WriteToken(jwt), _ttlMinutes * 60);
        }
    }
}
