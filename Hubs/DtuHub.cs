using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MaxChemical.DtuServer.Hubs
{
    /// <summary>
    /// 桌面端(MaxChemic)与本服务的 SignalR 隧道。
    /// 桌面把某设备的 Modbus 字节发进来，按 DTU 序列号路由到对应连接，原路返回应答。
    /// </summary>
    public class DtuHub : Hub
    {
        private readonly DtuServerManager _mgr;
        private readonly ILogger<DtuHub> _logger;
        private readonly string _token;

        public DtuHub(DtuServerManager mgr, IConfiguration config, ILogger<DtuHub> logger)
        {
            _mgr = mgr;
            _logger = logger;
            _token = config["Hub:AccessToken"] ?? "";
        }

        public override async Task OnConnectedAsync()
        {
            // Phase 1 简单令牌校验（Phase 3 换成正式鉴权）。
            // 令牌经自定义头 X-Access-Token 传入，与桌面端 RemoteGatewayClient 一致。
            // Hub:AccessToken 配空则不校验（仅供本地联调）。
            if (!string.IsNullOrEmpty(_token))
            {
                var http = Context.GetHttpContext();
                var provided = http?.Request.Headers["X-Access-Token"].ToString();
                if (provided != _token)
                {
                    _logger.LogWarning("拒绝未授权的隧道连接 {Conn}", Context.ConnectionId);
                    Context.Abort();
                    return;
                }
            }
            _logger.LogInformation("桌面端隧道已连接 {Conn}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 一问一答：把 Modbus 请求帧发到 serial 对应的 DTU，取回应答帧。
        /// DTU 超时/离线/出错时返回空数组，客户端据此按超时重试。
        /// </summary>
        public async Task<byte[]> SendAndReceive(string serial, byte[] data, int timeoutMs)
        {
            try
            {
                var recv = new byte[512];
                int len = await _mgr.SendAndReceiveAsync(serial, data, recv, timeoutMs, Context.ConnectionAborted);
                if (len <= 0) return Array.Empty<byte>();
                var result = new byte[len];
                Array.Copy(recv, result, len);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SendAndReceive 失败 serial={Serial}: {Err}", serial, ex.Message);
                return Array.Empty<byte>();
            }
        }

        /// <summary>只发不收（写命令场景）。</summary>
        public async Task<bool> Send(string serial, byte[] data)
        {
            try
            {
                return await _mgr.SendAsync(serial, data, 0, data.Length, Context.ConnectionAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Send 失败 serial={Serial}: {Err}", serial, ex.Message);
                return false;
            }
        }

        /// <summary>当前在线的 DTU 序列号列表（诊断用）。</summary>
        public string[] OnlineSerials() => _mgr.OnlineSerials.ToArray();

        /// <summary>
        /// 指定序列号的 DTU 当前是否在线（连接时校验用：
        /// 用与 SendAndReceive 完全相同的键查找，确保"能连上"=="发得出命令"）。
        /// </summary>
        public bool IsOnline(string serial) => _mgr.IsOnline(serial);
    }
}
