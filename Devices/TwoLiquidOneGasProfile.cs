using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 两液一气进料系统(TwoLiquidOneGasFeedSystem_ModbusRTU)设备档案。
    /// 寄存器布局与桌面驱动一致(实际下发地址,Base 0;Float 为 IEEE-754 大端 ABCD):
    ///   Float×2reg: 0 MFC_FSV | 2 MFC_FLOW | 4 MFC_Add | 6 泵1FSV | 8 泵1FLOW | 10 泵1Add | 12 泵1P
    ///               14 泵2FSV | 16 泵2FLOW | 18 泵2Add | 20 泵2P | 22 MFC量程上限 | 24 量程下限
    ///               26 流量修正 | 28 Dpt | 30 泵1压力上限 | 32 泵1压力下限 | 34 泵2压力上限 | 36 泵2压力下限
    ///   Int16:      38 MFC ON/OFF(0:运行,1:停止) | 39 泵1(1:运行) | 40 泵2(1:运行)
    ///               41 泵1压力报警 | 42 泵2压力报警 | 43 MFC通信故障 | 44 泵1通信故障 | 45 泵2通信故障
    ///               46 报警复位(写1) | 47 报警消音(写1) | 48 报警状态
    /// 对外命令的开关语义统一为 1=运行/0=停止,MFC 的取反在本档案内处理。
    /// </summary>
    public class TwoLiquidOneGasProfile : IDeviceProfile
    {
        public string TypeKey => "TwoLiquidOneGasFeedSystem_ModbusRTU";
        private const int TimeoutMs = 4000;

        public async Task<Dictionary<string, object>> ReadTelemetryAsync(
            DtuServerManager mgr, string serial, byte station, CancellationToken ct)
        {
            var recv = new byte[512];
            var r = new Dictionary<string, object>();

            // 一次读全部 49 个寄存器(0~48),与桌面驱动"获取所有数据"同帧
            int n = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0, 49), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (!ModbusFrameUtil.TryParseRead(recv, n, station, out var g) || g.Length < 49)
                return r;

            r["mfcFsv"]  = ModbusFrameUtil.RegsToFloat(g, 0);
            r["mfcFlow"] = ModbusFrameUtil.RegsToFloat(g, 2);
            r["mfcAdd"]  = ModbusFrameUtil.RegsToFloat(g, 4);
            r["p1Fsv"]   = ModbusFrameUtil.RegsToFloat(g, 6);
            r["p1Flow"]  = ModbusFrameUtil.RegsToFloat(g, 8);
            r["p1Add"]   = ModbusFrameUtil.RegsToFloat(g, 10);
            r["p1P"]     = ModbusFrameUtil.RegsToFloat(g, 12);
            r["p2Fsv"]   = ModbusFrameUtil.RegsToFloat(g, 14);
            r["p2Flow"]  = ModbusFrameUtil.RegsToFloat(g, 16);
            r["p2Add"]   = ModbusFrameUtil.RegsToFloat(g, 18);
            r["p2P"]     = ModbusFrameUtil.RegsToFloat(g, 20);
            r["mfcRangeHigh"] = ModbusFrameUtil.RegsToFloat(g, 22);
            r["p1PHigh"] = ModbusFrameUtil.RegsToFloat(g, 30);
            r["p2PHigh"] = ModbusFrameUtil.RegsToFloat(g, 34);

            r["mfcOn"] = g[38] == 0;                 // 0:运行 1:停止(取反)
            r["p1On"]  = g[39] == 1;
            r["p2On"]  = g[40] == 1;
            r["almP1"]      = g[41] == 1;
            r["almP2"]      = g[42] == 1;
            r["almMfcComm"] = g[43] == 1;
            r["almP1Comm"]  = g[44] == 1;
            r["almP2Comm"]  = g[45] == 1;
            r["almStatus"]  = g[48] == 1;
            return r;
        }

        public async Task<bool> WriteControlAsync(
            DtuServerManager mgr, string serial, byte station,
            string command, Dictionary<string, double> args, CancellationToken ct)
        {
            double v = args != null && args.TryGetValue("value", out var val) ? val : 0;
            switch (command)
            {
                // —— Float 设定(FC16 双寄存器,回读校验)——
                case "setMfcFsv":       return await WriteFloatVerifyAsync(mgr, serial, station, 0,  (float)v, ct);
                case "setP1Fsv":        return await WriteFloatVerifyAsync(mgr, serial, station, 6,  (float)v, ct);
                case "setP2Fsv":        return await WriteFloatVerifyAsync(mgr, serial, station, 14, (float)v, ct);
                case "setMfcRangeHigh": return await WriteFloatVerifyAsync(mgr, serial, station, 22, (float)v, ct);
                case "setP1PHigh":      return await WriteFloatVerifyAsync(mgr, serial, station, 30, (float)v, ct);
                case "setP2PHigh":      return await WriteFloatVerifyAsync(mgr, serial, station, 34, (float)v, ct);

                // —— 开关(FC06,面板语义 1=运行;MFC 寄存器取反)——
                case "mfcOnOff": return await WriteInt16VerifyAsync(mgr, serial, station, 38, (ushort)(v != 0 ? 0 : 1), ct);
                case "p1OnOff":  return await WriteInt16VerifyAsync(mgr, serial, station, 39, (ushort)(v != 0 ? 1 : 0), ct);
                case "p2OnOff":  return await WriteInt16VerifyAsync(mgr, serial, station, 40, (ushort)(v != 0 ? 1 : 0), ct);

                // —— 触发型(写1;PLC 可能自动回落,不做回读校验)——
                case "alarmReset":   return await WriteTriggerAsync(mgr, serial, station, 46, ct);
                case "alarmSilence": return await WriteTriggerAsync(mgr, serial, station, 47, ct);

                default: return false;
            }
        }

        // ── 写+回读校验(与 HighPreactorProfile 同一策略:短等回显,超时不算失败,回读比对兜底)──

        private static async Task<bool> WriteInt16VerifyAsync(
            DtuServerManager mgr, string serial, byte station, ushort reg, ushort value, CancellationToken ct)
        {
            var frame = ModbusFrameUtil.BuildWriteSingle(station, reg, value);
            return await WriteVerifyCoreAsync(mgr, serial, station, frame, reg, new[] { value }, ct);
        }

        private static async Task<bool> WriteFloatVerifyAsync(
            DtuServerManager mgr, string serial, byte station, ushort reg, float value, CancellationToken ct)
        {
            var words = ModbusFrameUtil.FloatToRegs(value);
            var frame = ModbusFrameUtil.BuildWriteMultiple(station, reg, words);
            return await WriteVerifyCoreAsync(mgr, serial, station, frame, reg, words, ct);
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

        private static async Task<bool> WriteVerifyCoreAsync(
            DtuServerManager mgr, string serial, byte station,
            byte[] writeFrame, ushort reg, ushort[] expect, CancellationToken ct)
        {
            const int EchoTimeoutMs = 1200;
            const int VerifyRetries = 3;
            const int RetryDelayMs = 200;

            var recv = new byte[64];
            for (int attempt = 1; attempt <= VerifyRetries; attempt++)
            {
                try
                {
                    int n = await mgr.SendAndReceiveAsync(serial, writeFrame, recv, EchoTimeoutMs, ct).ConfigureAwait(false);
                    if (n >= 2 && recv[0] == station && (recv[1] & 0x80) == 0)
                        return true;
                }
                catch (TimeoutException) { /* 无回显 → 回读校验 */ }

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
                catch (TimeoutException) { }

                if (attempt < VerifyRetries)
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
            }
            return false;
        }
    }
}
