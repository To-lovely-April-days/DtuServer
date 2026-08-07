using System.Collections.Concurrent;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>
    /// 工艺组态的内存快照存储:桌面「上云」把导出的 SVG 和实时值 POST 上来,
    /// 网页 process.html 从这里取。按工艺名(name)存,name 空则用 "default"。
    /// 进程内存即可(掉电即失);要持久化可换成写文件/DB。
    /// </summary>
    public class ProcessStore
    {
        private readonly ConcurrentDictionary<string, (string Svg, DateTime Ts)> _svg = new();
        private readonly ConcurrentDictionary<string, (string Json, DateTime Ts)> _tel = new();
        private readonly ConcurrentDictionary<string, (string Json, DateTime Ts)> _doe = new();
        // 云端远程填结果:key = "name|batchId|runIndex" → 用户在网页提交的响应值 JSON,桌面轮询取走。
        private readonly ConcurrentDictionary<string, string> _pendingResp = new();
        // 实时曲线:name → (指标key → 环形缓冲的时间序列)。桌面批量上报每个样本,不丢数据。
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Ring>> _series = new();
        private const int SeriesCap = 600;

        public string? LatestName { get; private set; }

        public void PutSvg(string? name, string svg)
        {
            var k = Key(name);
            _svg[k] = (svg, DateTime.UtcNow);
            LatestName = k;
        }

        public (string Svg, DateTime Ts)? GetSvg(string? name)
        {
            var k = Key(name);
            if (_svg.TryGetValue(k, out var v)) return v;
            // 名字对不上时,退回最近一次上传的
            if (LatestName != null && _svg.TryGetValue(LatestName, out var l)) return l;
            return null;
        }

        /// <summary>SVG 版本号(最后上传时间的 Ticks);网页轮询它来判断是否需要自动重载。0=还没上传。</summary>
        public long SvgVersion(string? name) => GetSvg(name)?.Ts.Ticks ?? 0;

        /// <summary>
        /// 解析出网页实际会拿到的那份 SVG 的名字:请求名存在就用它,否则退回最近一次上传的名字。
        /// 网页据此显示真实项目名(而不是前端写死的样例名),并用它作后续查询键。null=还没上传。
        /// </summary>
        public string? ResolveName(string? name)
        {
            var k = Key(name);
            if (_svg.ContainsKey(k)) return k;
            return LatestName;
        }

        public void PutTelemetry(string? name, string json) => _tel[Key(name)] = (json, DateTime.UtcNow);

        public (string Json, DateTime Ts)? GetTelemetry(string? name)
        {
            var k = Key(name);
            if (_tel.TryGetValue(k, out var v)) return v;
            if (LatestName != null && _tel.TryGetValue(LatestName, out var l)) return l;
            return null;
        }

        // ── DOE 快照:桌面把「运行表 + 当前组 + 状态」推上来,网页 DOE 面板轮询展示 ──
        public void PutDoe(string? name, string json) => _doe[Key(name)] = (json, DateTime.UtcNow);

        public (string Json, DateTime Ts)? GetDoe(string? name)
        {
            var k = Key(name);
            if (_doe.TryGetValue(k, out var v)) return v;
            if (LatestName != null && _doe.TryGetValue(LatestName, out var l)) return l;
            return null;
        }

        /// <summary>DOE 快照版本号;网页轮询它决定是否刷新面板。0=还没有。</summary>
        public long DoeVersion(string? name) => GetDoe(name)?.Ts.Ticks ?? 0;

        // ── 云端远程填结果:网页提交 → 桌面 DOEBatchExecutor 轮询取走 ──
        /// <summary>网页为某一组(batchId+runIndex)提交测量结果 JSON。</summary>
        public void PutResponse(string? name, string batchId, int runIndex, string valuesJson)
            => _pendingResp[RespKey(name, batchId, runIndex)] = valuesJson;

        /// <summary>桌面取走并清除该组的远程结果;没有则返回 null。</summary>
        public string? TakeResponse(string? name, string batchId, int runIndex)
            => _pendingResp.TryRemove(RespKey(name, batchId, runIndex), out var v) ? v : null;

        private static string RespKey(string? name, string batchId, int runIndex)
            => $"{Key(name)}|{batchId}|{runIndex}";

        // ── 实时曲线:批量追加样本(不丢数据)+ 增量取历史 ──
        /// <summary>追加一帧样本(同一时刻多个指标的值)。桌面 1s 发一批,批里含这一秒的所有帧。</summary>
        public void AppendSeries(string? name, long t, IReadOnlyDictionary<string, double> values)
        {
            if (values == null || values.Count == 0) return;
            var perName = _series.GetOrAdd(Key(name), _ => new ConcurrentDictionary<string, Ring>());
            foreach (var kv in values)
                perName.GetOrAdd(kv.Key, _ => new Ring(SeriesCap)).Add(t, kv.Value);
        }

        /// <summary>取某指标 key 在 sinceT 之后的点(sinceT&lt;=0 = 全部)。用于网页画曲线 + 增量刷新。</summary>
        public IReadOnlyList<(long T, double V)> GetHistory(string? name, string key, long sinceT)
        {
            var perName = ResolveSeries(name);
            if (perName != null && perName.TryGetValue(key, out var ring)) return ring.Since(sinceT);
            return System.Array.Empty<(long, double)>();
        }

        private ConcurrentDictionary<string, Ring>? ResolveSeries(string? name)
        {
            var k = Key(name);
            if (_series.TryGetValue(k, out var v)) return v;
            if (LatestName != null && _series.TryGetValue(LatestName, out var l)) return l;
            return null;
        }

        /// <summary>固定容量的时间序列环形缓冲(超出丢最旧)。</summary>
        private sealed class Ring
        {
            private readonly int _cap;
            private readonly List<(long T, double V)> _buf;
            private readonly object _lock = new();
            public Ring(int cap) { _cap = cap; _buf = new List<(long, double)>(cap); }
            public void Add(long t, double v)
            {
                lock (_lock)
                {
                    _buf.Add((t, v));
                    if (_buf.Count > _cap) _buf.RemoveRange(0, _buf.Count - _cap);
                }
            }
            public IReadOnlyList<(long T, double V)> Since(long sinceT)
            {
                lock (_lock)
                {
                    if (sinceT <= 0) return _buf.ToList();
                    var r = new List<(long, double)>();
                    for (int i = _buf.Count - 1; i >= 0 && _buf[i].T > sinceT; i--) r.Add(_buf[i]);
                    r.Reverse();
                    return r;
                }
            }
        }

        private static string Key(string? n) => string.IsNullOrWhiteSpace(n) ? "default" : n.Trim();
    }
}
