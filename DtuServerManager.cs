using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MaxChemical.DtuServer
{
    /// <summary>一台 DTU 的在线信息（推送给前端看板）。</summary>
    public class DtuOnlineInfo
    {
        public string Serial { get; set; } = "";
        public string? Remote { get; set; }
        public DateTime ConnectedAt { get; set; }
        public bool Online { get; set; } = true;
    }

    /// <summary>
    /// DTU 网关：本服务作为 TCP 服务端监听，多台 DTU 主动连入，
    /// 按登录包(序列号)路由，提供原始 Modbus 字节的一问一答透传。
    /// 上线/下线通过事件通知，供看板实时刷新。
    /// </summary>
    public class DtuServerManager
    {
        private readonly ILogger<DtuServerManager> _logger;
        private readonly ConcurrentDictionary<string, DtuConnection> _connections =
            new ConcurrentDictionary<string, DtuConnection>(StringComparer.OrdinalIgnoreCase);

        private TcpListener? _listener;
        private volatile bool _started;
        private int _loginTimeoutMs = 15000;
        private int _heartbeatTimeoutMs = 0;   // >0 时启用"长时间无收包判离线"兜底
        private readonly object _startLock = new object();

        public DtuServerManager(ILogger<DtuServerManager> logger)
        {
            _logger = logger;
        }

        /// <summary>DTU 上线事件（含连接信息）。</summary>
        public event Action<DtuOnlineInfo>? DeviceOnline;

        /// <summary>DTU 下线事件（序列号）。</summary>
        public event Action<string>? DeviceOffline;

        /// <summary>已上线的 DTU 序列号集合（去重）。</summary>
        public IReadOnlyCollection<string> OnlineSerials =>
            _connections.Values.Where(c => c.IsAlive).Select(c => c.Serial).Distinct().ToArray();

        /// <summary>在线设备快照（供前端首次加载）。</summary>
        public IReadOnlyList<DtuOnlineInfo> OnlineDevices =>
            _connections.Values.Where(c => c.IsAlive)
                .GroupBy(c => c.Serial)
                .Select(g => g.First().ToInfo())
                .ToList();

        public void Start(int port, int loginTimeoutMs = 15000, int heartbeatTimeoutMs = 0)
        {
            if (_started) return;
            lock (_startLock)
            {
                if (_started) return;
                _loginTimeoutMs = loginTimeoutMs;
                _heartbeatTimeoutMs = heartbeatTimeoutMs;
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _started = true;
                _ = Task.Run(AcceptLoopAsync);
                _ = Task.Run(LivenessLoopAsync);
                _logger.LogInformation("DTU 网关已启动，监听端口 {Port}", port);
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (_started && _listener != null)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!_started) break;
                    _logger.LogWarning("DTU 网关 Accept 异常: {Error}", ex.Message);
                    continue;
                }
                _ = Task.Run(() => HandleNewClientAsync(client));
            }
        }

        /// <summary>
        /// 探活兜底循环。主检测靠每连接的后台读循环(死亡即回调下线)+ TCP Keepalive;
        /// 这里再兜底:清理已死连接;若配置了心跳超时,长时间无任何收包也判离线。
        /// </summary>
        private async Task LivenessLoopAsync()
        {
            while (_started)
            {
                try
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    foreach (var conn in _connections.Values.Distinct().ToArray())
                    {
                        bool dead = !conn.IsAlive;
                        // 可选:心跳空闲超时(>0 才启用,避免对"安静但在线"的设备误判)
                        if (!dead && _heartbeatTimeoutMs > 0 &&
                            (now - conn.LastRxUtc).TotalMilliseconds > _heartbeatTimeoutMs)
                        {
                            _logger.LogInformation("DTU '{Serial}' 超过 {Ms}ms 无收包,判离线", conn.Serial, _heartbeatTimeoutMs);
                            dead = true;
                        }
                        if (dead) RemoveConnection(conn);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("探活循环异常: {Error}", ex.Message);
                }
            }
        }

        /// <summary>开启 TCP Keepalive:空闲 15s 起探测、每 5s 一次、3 次无应答判死(~30s 发现断电)。</summary>
        private static void ConfigureKeepAlive(Socket s)
        {
            try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
            try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15); } catch { }
            try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); } catch { }
            try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); } catch { }
        }

        private async Task HandleNewClientAsync(TcpClient client)
        {
            string? remote = null;
            try
            {
                client.NoDelay = true;
                ConfigureKeepAlive(client.Client);   // 开启 TCP Keepalive,~30s 发现断电掉线
                remote = client.Client.RemoteEndPoint?.ToString();
                var stream = client.GetStream();

                // 读登录包(序列号)，作为该连接的路由 key
                var buf = new byte[512];
                using var cts = new CancellationTokenSource(_loginTimeoutMs);
                int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                if (n <= 0)
                {
                    client.Close();
                    return;
                }

                var ascii = DecodeAsciiSerial(buf, n);
                var hex = ToHex(buf, n);
                var serial = !string.IsNullOrEmpty(ascii) ? ascii : hex;

                // 后台读循环检测到连接死亡时,立即回调下线(不必等探活轮询)
                var conn = new DtuConnection(client, stream, _logger, c => RemoveConnection(c))
                {
                    Serial = serial,
                    Remote = remote,
                    ConnectedAt = DateTime.Now,
                };
                var keys = new List<string>();
                if (!string.IsNullOrEmpty(ascii)) keys.Add(ascii);
                keys.Add(hex); // 16 进制登录包也能匹配
                conn.Keys = keys;

                foreach (var k in keys)
                {
                    if (_connections.TryGetValue(k, out var old) && old != conn)
                    {
                        old.MarkReplaced();
                        old.Dispose();
                    }
                    _connections[k] = conn;
                }

                _logger.LogInformation("DTU 上线: 序列号='{Serial}' (HEX={Hex}) 来自 {Remote}", serial, hex, remote);
                RaiseOnline(conn.ToInfo());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("处理 DTU 连接失败({Remote}): {Error}", remote, ex.Message);
                try { client.Close(); } catch { /* ignore */ }
            }
        }

        /// <summary>一问一答：把 Modbus 请求帧发到指定序列号的 DTU，读回应答帧。</summary>
        public async Task<int> SendAndReceiveAsync(string serialNo, byte[] send, byte[] recv, int timeoutMs, CancellationToken ct)
        {
            var conn = GetConnectionOrThrow(serialNo);
            try
            {
                byte expectedSlave = send.Length > 0 ? send[0] : (byte)0;
                return await conn.SendAndReceiveAsync(send, recv, timeoutMs, expectedSlave, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                RemoveConnection(conn);
                throw new IOException($"DTU [{serialNo}] 连接已断开: {ex.Message}", ex);
            }
        }

        /// <summary>只发不收（写命令场景）。</summary>
        public async Task<bool> SendAsync(string serialNo, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var conn = GetConnectionOrThrow(serialNo);
            try
            {
                await conn.SendAsync(buffer, offset, count, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                RemoveConnection(conn);
                throw new IOException($"DTU [{serialNo}] 连接已断开: {ex.Message}", ex);
            }
        }

        public bool IsOnline(string serialNo) =>
            _connections.TryGetValue(serialNo, out var c) && c.IsAlive;

        /// <summary>取指定序列号 DTU 的来源地址(IP:端口);不在线返回 null。</summary>
        public string? GetRemote(string serialNo) =>
            _connections.TryGetValue(serialNo, out var c) && c.IsAlive ? c.Remote : null;

        private DtuConnection GetConnectionOrThrow(string serialNo)
        {
            if (string.IsNullOrWhiteSpace(serialNo))
                throw new InvalidOperationException("未提供 DTU 序列号");

            if (_connections.TryGetValue(serialNo, out var conn) && conn.IsAlive)
                return conn;

            throw new InvalidOperationException($"DTU [{serialNo}] 尚未连接到服务器");
        }

        private void RemoveConnection(DtuConnection conn)
        {
            bool removedAny = false;
            foreach (var k in conn.Keys)
            {
                if (_connections.TryGetValue(k, out var c) && c == conn)
                {
                    _connections.TryRemove(k, out _);
                    removedAny = true;
                }
            }
            conn.Dispose();

            // 只有当它确实是当前注册的连接(而非被新连接替换掉的旧连接)才广播下线
            if (removedAny && !conn.WasReplaced && conn.MarkOfflineFired())
            {
                _logger.LogInformation("DTU 下线: 序列号='{Serial}'", conn.Serial);
                RaiseOffline(conn.Serial);
            }
        }

        private void RaiseOnline(DtuOnlineInfo info)
        {
            try { DeviceOnline?.Invoke(info); } catch (Exception ex) { _logger.LogDebug("DeviceOnline 通知异常: {E}", ex.Message); }
        }

        private void RaiseOffline(string serial)
        {
            try { DeviceOffline?.Invoke(serial); } catch (Exception ex) { _logger.LogDebug("DeviceOffline 通知异常: {E}", ex.Message); }
        }

        public void Stop()
        {
            lock (_startLock)
            {
                _started = false;
                try { _listener?.Stop(); } catch { /* ignore */ }
                _listener = null;
                foreach (var c in _connections.Values.Distinct().ToArray()) c.Dispose();
                _connections.Clear();
            }
        }

        #region 辅助

        private static string DecodeAsciiSerial(byte[] buf, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        private static string ToHex(byte[] buf, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(buf[i].ToString("X2"));
            return sb.ToString();
        }

        #endregion

        /// <summary>
        /// 单条 DTU 连接:后台读循环独占 socket 读(检测死亡 + 刷新收包时间戳 + 投递到有界 Channel),
        /// 命令路径在写锁内"清空缓冲 → 下发 → 读应答帧"。死亡即回调下线,不必等探活轮询。
        /// </summary>
        private sealed class DtuConnection : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly ILogger _logger;
            private readonly Action<DtuConnection>? _onDead;
            private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

            // 后台读循环 → 有界 Channel(DropOldest 防止无界增长)→ 命令路径单读消费
            private readonly Channel<byte[]> _rx = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });
            private byte[] _leftover = Array.Empty<byte>();
            private int _leftoverPos;

            private volatile bool _disposed;
            private volatile bool _dead;
            private int _offlineFired;

            public List<string> Keys { get; set; } = new List<string>();
            public string Serial { get; set; } = "";
            public string? Remote { get; set; }
            public DateTime ConnectedAt { get; set; }
            public DateTime LastRxUtc { get; private set; } = DateTime.UtcNow;
            public bool WasReplaced { get; private set; }

            public DtuConnection(TcpClient client, NetworkStream stream, ILogger logger, Action<DtuConnection>? onDead = null)
            {
                _client = client;
                _stream = stream;
                _logger = logger;
                _onDead = onDead;
                _ = Task.Run(ReadLoopAsync);
            }

            public DtuOnlineInfo ToInfo() => new DtuOnlineInfo
            {
                Serial = Serial,
                Remote = Remote,
                ConnectedAt = ConnectedAt,
                Online = true,
            };

            public void MarkReplaced() => WasReplaced = true;

            /// <summary>首次置下线返回 true，避免重复广播。</summary>
            public bool MarkOfflineFired() => Interlocked.Exchange(ref _offlineFired, 1) == 0;

            public bool IsAlive => !_disposed && !_dead;

            /// <summary>后台读循环:独占 socket 读;任何收包刷新时间戳;读到 0 或异常(含 keepalive 判死)即下线。</summary>
            private async Task ReadLoopAsync()
            {
                var buf = new byte[2048];
                string reason = "未知";
                try
                {
                    while (!_disposed)
                    {
                        int n = await _stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                        if (n <= 0) { reason = "对端主动关闭(收到 FIN,n<=0)"; break; }   // 对端优雅关闭
                        LastRxUtc = DateTime.UtcNow;
                        var arr = new byte[n];
                        Buffer.BlockCopy(buf, 0, arr, 0, n);
                        _rx.Writer.TryWrite(arr);
                    }
                    if (_disposed && reason == "未知") reason = "服务端主动释放";
                }
                catch (Exception ex) { reason = $"异常/keepalive判死: {ex.GetType().Name} - {ex.Message}"; }
                finally
                {
                    _dead = true;
                    // 诊断:断开原因 + 在线时长 + 最后一次收包距今(帮助区分 短连接/FIN、keepalive误判、NAT回收)
                    try
                    {
                        _logger.LogInformation(
                            "DTU[{Serial}] 读循环结束 → 原因={Reason};在线时长={AliveSec:F0}s;最后收包={RxAgo:F0}s前;来源={Remote}",
                            Serial, reason, (DateTime.Now - ConnectedAt).TotalSeconds,
                            (DateTime.UtcNow - LastRxUtc).TotalSeconds, Remote);
                    }
                    catch { /* ignore */ }
                    _rx.Writer.TryComplete();
                    try { _onDead?.Invoke(this); } catch { /* ignore */ }
                }
            }

            public async Task SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                await _ioLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_dead) throw new IOException("DTU 连接已断开");
                    await _stream.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally { _ioLock.Release(); }
            }

            public async Task<int> SendAndReceiveAsync(byte[] send, byte[] recv, int timeoutMs, byte expectedSlave, CancellationToken ct)
            {
                await _ioLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_dead) throw new IOException("DTU 连接已断开");
                    DrainBuffered();   // 丢弃下发前的心跳/残留,避免串扰、降低延迟

                    var sw = Stopwatch.StartNew();   // 计时:服务器↔DTU 实际往返,定位延迟到底在哪段
                    await _stream.WriteAsync(send, 0, send.Length, ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeoutMs);
                    int len = await ReadFrameAsync(recv, expectedSlave, cts.Token).ConfigureAwait(false);
                    sw.Stop();
                    if (sw.ElapsedMilliseconds > 1000)
                        _logger.LogInformation("DTU[{Serial}] 往返较慢 {Ms}ms (fc=0x{Fc:X2})",
                            Serial, sw.ElapsedMilliseconds, len > 1 ? recv[1] : (byte)0);
                    return len;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"等待 DTU 应答超时({timeoutMs}ms)");
                }
                finally { _ioLock.Release(); }
            }

            // ── 从 Channel 消费字节(命令路径,始终在 _ioLock 内单读)──

            private void DrainBuffered()
            {
                _leftover = Array.Empty<byte>();
                _leftoverPos = 0;
                while (_rx.Reader.TryRead(out _)) { }
            }

            private async ValueTask<int> ReadByteAsync(CancellationToken token)
            {
                if (_leftoverPos < _leftover.Length) return _leftover[_leftoverPos++];
                while (true)
                {
                    if (!await _rx.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return -1; // 已死/完成
                    if (_rx.Reader.TryRead(out var chunk) && chunk.Length > 0)
                    {
                        _leftover = chunk; _leftoverPos = 0;
                        return _leftover[_leftoverPos++];
                    }
                }
            }

            private async ValueTask FillAsync(byte[] dst, int offset, int count, CancellationToken token)
            {
                int got = 0;
                while (got < count)
                {
                    if (_leftoverPos >= _leftover.Length)
                    {
                        if (!await _rx.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                            throw new IOException("DTU 连接已关闭");
                        if (!_rx.Reader.TryRead(out var chunk) || chunk.Length == 0) continue;
                        _leftover = chunk; _leftoverPos = 0;
                    }
                    int avail = _leftover.Length - _leftoverPos;
                    int take = Math.Min(avail, count - got);
                    Buffer.BlockCopy(_leftover, _leftoverPos, dst, offset + got, take);
                    _leftoverPos += take; got += take;
                }
            }

            private async Task<int> ReadFrameAsync(byte[] recv, byte expectedSlave, CancellationToken token)
            {
                // 1) 跳到期望站号（顺带跳过心跳/噪声）
                int guard = 0;
                while (true)
                {
                    int b = await ReadByteAsync(token).ConfigureAwait(false);
                    if (b < 0) throw new IOException("DTU 连接已关闭");
                    if ((byte)b == expectedSlave) { recv[0] = (byte)b; break; }
                    if (++guard > 4096) throw new IOException("未找到期望站号，疑似数据异常");
                }

                // 2) 功能码
                int fcByte = await ReadByteAsync(token).ConfigureAwait(false);
                if (fcByte < 0) throw new IOException("DTU 连接已关闭");
                recv[1] = (byte)fcByte;
                byte fc = recv[1];

                int total;
                int filled = 2;

                if ((fc & 0x80) != 0)
                {
                    total = 5; // 异常应答
                }
                else if (fc == 0x01 || fc == 0x02 || fc == 0x03 || fc == 0x04)
                {
                    int lenByte = await ReadByteAsync(token).ConfigureAwait(false);
                    if (lenByte < 0) throw new IOException("DTU 连接已关闭");
                    recv[2] = (byte)lenByte; // 字节数
                    filled = 3;
                    total = 3 + recv[2] + 2;
                }
                else // 0x05/0x06/0x0F/0x10 及未知功能码,按固定 8 字节
                {
                    total = 8;
                }

                if (total > recv.Length) total = recv.Length;

                if (filled < total)
                    await FillAsync(recv, filled, total - filled, token).ConfigureAwait(false);

                return total;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _dead = true;
                try { _rx.Writer.TryComplete(); } catch { /* ignore */ }
                try { _stream?.Dispose(); } catch { /* ignore */ }
                try { _client?.Close(); } catch { /* ignore */ }
                try { _ioLock?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
