using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;

namespace MaxChemical.DtuServer.Hubs
{
    /// <summary>
    /// 浏览器看板用的 SignalR Hub。
    /// 浏览器连到 /dashboardhub，服务器在 DTU 上线/下线时推送：
    ///   - "DeviceOnline"  (DtuOnlineInfo)
    ///   - "DeviceOffline" (string serial)
    /// 页面据此实时刷新，无需手动刷新。
    /// </summary>
    public class DashboardHub : Hub
    {
        private readonly DtuServerManager _mgr;

        public DashboardHub(DtuServerManager mgr)
        {
            _mgr = mgr;
        }

        /// <summary>页面首次加载时拉当前在线列表。</summary>
        public IReadOnlyList<DtuOnlineInfo> GetOnline() => _mgr.OnlineDevices;
    }
}
