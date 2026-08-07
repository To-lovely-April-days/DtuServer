using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 碳化硅反应器 · 六联罐增压进料系统(SiliconCarbideChip_ModbusRTU)设备档案。
    /// 寄存器布局与桌面驱动一致(实际下发地址,Base 0;Float 为 IEEE-754 大端 ABCD):
    ///   Float×2reg: 0~11 T1~T6(℃) | 12 背压阀压力P(Bar)
    ///   Int16:      14 背压阀模式 | 15 背压阀SV% | 16 背压阀P SV | 17 背压阀ON/OFF(0:运行,1:停止)
    ///               27 报警复位(写1) | 28 报警消音(写1) | 29 报警状态(1=报警)
    /// 仪表写全部为 FC06 单寄存器(与桌面 BuildWriteCmd 一致)。
    /// 对外命令的开关语义统一为 1=运行/0=停止,阀门寄存器的取反在本档案内处理。
    /// </summary>
    public class SiliconCarbideChipProfile : IDeviceProfile
    {
        public string TypeKey => "SiliconCarbideChip_ModbusRTU";
        private const int TimeoutMs = 4000;

        public async Task<Dictionary<string, object>> ReadTelemetryAsync(
            DtuServerManager mgr, string serial, byte station, CancellationToken ct)
        {
            var recv = new byte[512];
            var r = new Dictionary<string, object>();

            // 一次读 30 个寄存器(0~29):温度/压力/阀门 + 报警状态
            int n = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0, 30), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (!ModbusFrameUtil.TryParseRead(recv, n, station, out var g) || g.Length < 30)
                return r;

            r["t1"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 0), 1);
            r["t2"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 2), 1);
            r["t3"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 4), 1);
            r["t4"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 6), 1);
            r["t5"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 8), 1);
            r["t6"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 10), 1);
            r["press"] = Math.Round(ModbusFrameUtil.RegsToFloat(g, 12), 2);

            r["mode"]      = (int)(short)g[14];
            r["svPercent"] = (int)(short)g[15];
            r["pSv"]       = (int)(short)g[16];
            r["on"]        = g[17] == 0;      // 0:运行 1:停止(取反)
            r["almStatus"] = g[29] == 1;
            return r;
        }

        public async Task<bool> WriteControlAsync(
            DtuServerManager mgr, string serial, byte station,
            string command, Dictionary<string, double> args, CancellationToken ct)
        {
            double v = args != null && args.TryGetValue("value", out var val) ? val : 0;
            switch (command)
            {
                case "setMode":      return await WriteInt16VerifyAsync(mgr, serial, station, 14, (ushort)(short)v, ct);
                case "setSvPercent": return await WriteInt16VerifyAsync(mgr, serial, station, 15, (ushort)(short)v, ct);
                case "setPSv":       return await WriteInt16VerifyAsync(mgr, serial, station, 16, (ushort)(short)v, ct);

                // 开关(面板语义 1=运行;寄存器取反:0=运行/1=停止)
                case "onOff": return await WriteInt16VerifyAsync(mgr, serial, station, 17, (ushort)(v != 0 ? 0 : 1), ct);

                // 触发型(写1;PLC 可能自动回落,不做回读校验)
                case "alarmReset":   return await WriteTriggerAsync(mgr, serial, station, 27, ct);
                case "alarmSilence": return await WriteTriggerAsync(mgr, serial, station, 28, ct);

                default: return false;
            }
        }

        // ── 写+回读校验(与其它档案同一策略:短等回显,超时不算失败,回读比对兜底)──

        private static async Task<bool> WriteInt16VerifyAsync(
            DtuServerManager mgr, string serial, byte station, ushort reg, ushort value, CancellationToken ct)
        {
            const int EchoTimeoutMs = 1200;
            const int VerifyRetries = 3;
            const int RetryDelayMs = 200;

            var frame = ModbusFrameUtil.BuildWriteSingle(station, reg, value);
            var recv = new byte[64];
            for (int attempt = 1; attempt <= VerifyRetries; attempt++)
            {
                try
                {
                    int n = await mgr.SendAndReceiveAsync(serial, frame, recv, EchoTimeoutMs, ct).ConfigureAwait(false);
                    if (n >= 2 && recv[0] == station && (recv[1] & 0x80) == 0)
                        return true;
                }
                catch (TimeoutException) { /* 无回显 → 回读校验 */ }

                try
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    int n2 = await mgr.SendAndReceiveAsync(serial,
                        ModbusFrameUtil.BuildReadHolding(station, reg, 1), recv, TimeoutMs, ct).ConfigureAwait(false);
                    if (ModbusFrameUtil.TryParseRead(recv, n2, station, out var regs) && regs.Length >= 1 && regs[0] == value)
                        return true;
                }
                catch (TimeoutException) { }

                if (attempt < VerifyRetries)
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task<bool> WriteTriggerAsync(
            DtuServerManager mgr, string serial, byte station, ushort reg, CancellationToken ct)
        {
            var frame = ModbusFrameUtil.BuildWriteSingle(station, reg, 1);
            var recv = new byte[64];
            try
            {
                int n = await mgr.SendAndReceiveAsync(serial, frame, recv, 1500, ct).ConfigureAwait(false);
                return n >= 2 && recv[0] == station && (recv[1] & 0x80) == 0;
            }
            catch (TimeoutException)
            {
                return true;   // 无回显也视为已触发(触发位可能被 PLC 立即回落,无法回读校验)
            }
        }
    }
}
