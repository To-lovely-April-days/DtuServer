using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 微型/高压反应釜(HighPreactor_ModbusRTU)设备档案。
    /// 寄存器映射对齐 MaxChemic 桌面驱动:
    ///   釜温PV=0x0000(i32/100℃) 炉温PV=0x0004(i32/100) 釜温SV=0x0032(i32/100)
    ///   搅拌PV=0x00FB 搅拌SV=0x00FC 压力=0x0106(/100) 釜温上限=0x010E 炉温上限=0x0110 压力上限=0x0112(/100)
    /// 控制:加热=reg54(0/1) 搅拌启停=reg253(0/1) 设定温度=0x0032(i32) 搅拌SV=0x00FC 炉/釜温上限=0x0110/0x010E
    /// </summary>
    public class HighPreactorProfile : IDeviceProfile
    {
        public string TypeKey => "HighPreactor_ModbusRTU";
        private const int TimeoutMs = 4000;

        public async Task<Dictionary<string, object>> ReadTelemetryAsync(
            DtuServerManager mgr, string serial, byte station, CancellationToken ct)
        {
            var recv = new byte[512];
            var r = new Dictionary<string, object>();

            // 批量读1:0x0000..0x0036(55寄存器)→ 釜温PV / 炉温PV / 釜温SV / 加热开关(reg54)
            int n1 = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0x0000, 0x37), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (ModbusFrameUtil.TryParseRead(recv, n1, station, out var b1) && b1.Length >= 55)
            {
                r["bathTempPv"] = ModbusFrameUtil.RegsToInt32(b1, 0x00) / 100.0;
                r["furnaceTempPv"] = ModbusFrameUtil.RegsToInt32(b1, 0x04) / 100.0;
                r["bathTempSv"] = ModbusFrameUtil.RegsToInt32(b1, 0x32) / 100.0;
                r["heating"] = b1[0x36] != 0;                                   // reg54 加热开关
            }

            // 批量读2:0x00FB..0x0112(24寄存器)→ 搅拌PV/SV、压力、釜/炉温上限、压力上限
            int n2 = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0x00FB, 0x18), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (ModbusFrameUtil.TryParseRead(recv, n2, station, out var b2) && b2.Length >= 24)
            {
                r["speedPv"] = (int)b2[0x00];                                   // 0xFB
                r["speedSv"] = (int)b2[0x01];                                   // 0xFC
                r["stirring"] = b2[0x02] != 0;                                  // reg253 搅拌开关
                r["pressure"] = b2[0x0B] / 100.0;                               // 0x106
                r["bathLimit"] = ModbusFrameUtil.RegsToInt32(b2, 0x13) / 100.0; // 0x10E
                r["furnaceLimit"] = ModbusFrameUtil.RegsToInt32(b2, 0x15) / 100.0; // 0x110
                r["pressureLimit"] = b2[0x17] / 100.0;                          // 0x112
            }
            return r;
        }

        public async Task<bool> WriteControlAsync(
            DtuServerManager mgr, string serial, byte station,
            string command, Dictionary<string, double> args, CancellationToken ct)
        {
            double v = args != null && args.TryGetValue("value", out var val) ? val : 0;
            ushort reg;
            ushort[] expect;   // 期望写入的寄存器值(回读比对用)
            byte[] frame;
            switch (command)
            {
                case "setBathTempSv":   // 釜温设定(℃)→ 0x0032 i32 ×100
                    reg = 0x0032; expect = ModbusFrameUtil.Int32ToRegs((int)Math.Round(v * 100));
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                case "setSpeedSv":      // 搅拌设定(RPM)→ 0x00FC(仪表只认 FC16,单寄存器也走写多)
                    reg = 0x00FC; expect = new[] { (ushort)(int)Math.Round(v) };
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                case "setBathLimit":    // 釜温上限(℃)→ 0x010E i32 ×100
                    reg = 0x010E; expect = ModbusFrameUtil.Int32ToRegs((int)Math.Round(v * 100));
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                case "setFurnaceLimit": // 炉温上限(℃)→ 0x0110 i32 ×100
                    reg = 0x0110; expect = ModbusFrameUtil.Int32ToRegs((int)Math.Round(v * 100));
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                case "heating":         // 加热开/关 → reg54 0/1(FC16 单寄存器,与桌面驱动一致)
                    reg = 54; expect = new[] { (ushort)(v != 0 ? 1 : 0) };
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                case "stirring":        // 搅拌启停 → reg253 0/1(FC16 单寄存器,与桌面驱动一致)
                    reg = 253; expect = new[] { (ushort)(v != 0 ? 1 : 0) };
                    frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, expect);
                    break;
                default:
                    return false;
            }
            return await WriteWithVerifyAsync(mgr, serial, station, frame, reg, expect, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 写 + 回读校验(与桌面端 RemoteGatewayTransport 同一策略)。
        /// 这台仪表对 FC06/FC10 写帧常常不回 ACK:死等写回显会超时。
        /// 因此写回显只短等(收到标准 ACK 直接成功;超时不算失败),
        /// 随后回读同寄存器比对,值一致才算成功;有限重试。
        /// </summary>
        private static async Task<bool> WriteWithVerifyAsync(
            DtuServerManager mgr, string serial, byte station,
            byte[] writeFrame, ushort reg, ushort[] expect, CancellationToken ct)
        {
            const int EchoTimeoutMs = 1200;   // 写回显短等:有 ACK 的仪表几百毫秒内就回
            const int VerifyRetries = 3;
            const int RetryDelayMs = 200;

            var recv = new byte[64];
            for (int attempt = 1; attempt <= VerifyRetries; attempt++)
            {
                // 1) 下发写帧,短等回显;无回显不算失败,进入回读校验
                try
                {
                    int n = await mgr.SendAndReceiveAsync(serial, writeFrame, recv, EchoTimeoutMs, ct).ConfigureAwait(false);
                    if (n >= 2 && recv[0] == station && (recv[1] & 0x80) == 0)
                        return true;   // 标准写 ACK
                }
                catch (TimeoutException) { /* 仪表不回写 ACK → 走回读校验 */ }

                // 2) 回读同寄存器比对
                try
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    int n2 = await mgr.SendAndReceiveAsync(serial,
                        ModbusFrameUtil.BuildReadHolding(station, reg, (ushort)expect.Length),
                        recv, TimeoutMs, ct).ConfigureAwait(false);
                    if (ModbusFrameUtil.TryParseRead(recv, n2, station, out var regs) && regs.Length >= expect.Length)
                    {
                        bool match = true;
                        for (int i = 0; i < expect.Length; i++)
                            if (regs[i] != expect[i]) { match = false; break; }
                        if (match) return true;
                    }
                }
                catch (TimeoutException) { /* 回读也超时 → 重试 */ }

                if (attempt < VerifyRetries)
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
            }
            return false;
        }
    }
}
