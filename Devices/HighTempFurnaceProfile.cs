using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// 高温炉(HighTempFurnace_ModbusRTU,宇电 AI 温控器)设备档案。
    /// 寄存器(Base-0,与通讯表一致;仪表仅支持 FC03 读 / FC06 单寄存器写):
    ///   0 给定SV | 1 HIAL | 2 LoAL | 3 HdAL | 4 LdAL | 5 AHYS | 6 CtrL(0~4)
    ///   7 P | 8 I(秒) | 9 d(0.1秒) | 0x0A CtI(0.1秒) | 0x0B InP | 0x0C dPt | 0x0F AOP(1111输出/3333不输出)
    ///   0x16 Addr | 0x1B Srun(0运行/1停止/2保持) | 0x1D At(0~3) | 0x2B Pno | 0x2E STEP
    ///   0x4A PV(只读) | 0x4B SV(只读) | 0x50~0x8B SP1,t1,SP2,t2…SP30,t30(交错 60 寄存器)
    /// 标度:dPt=1 → 温度类(SV/PV/报警限/段SP)与 P 比例带、段时间 t 均为 显示值×10;
    ///       I/d/CtI/CtrL/At/Srun/Pno/STEP/InP/dPt 原始整数。
    /// 对外遥测/命令全部使用工程值,×10/÷10 在本档案内处理。
    /// </summary>
    public class HighTempFurnaceProfile : IDeviceProfile
    {
        public string TypeKey => "HighTempFurnace_ModbusRTU";
        private const int TimeoutMs = 4000;
        private const double Scale = 10.0;

        public async Task<Dictionary<string, object>> ReadTelemetryAsync(
            DtuServerManager mgr, string serial, byte station, CancellationToken ct)
        {
            var recv = new byte[512];
            var r = new Dictionary<string, object>();

            // 读1:0x00~0x2E(47 寄存器)→ 设定/报警/PID/控制/运行/程序状态
            int n1 = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0x0000, 47), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (ModbusFrameUtil.TryParseRead(recv, n1, station, out var a) && a.Length >= 47)
            {
                r["sv"]   = (short)a[0x00] / Scale;
                r["hial"] = (short)a[0x01] / Scale;
                r["loal"] = (short)a[0x02] / Scale;
                r["hdal"] = (short)a[0x03] / Scale;
                r["ldal"] = (short)a[0x04] / Scale;
                r["ahys"] = (short)a[0x05] / Scale;
                r["ctrl"] = (int)a[0x06];
                r["p"]    = (short)a[0x07] / Scale;
                r["i"]    = (int)a[0x08];
                r["d"]    = (int)a[0x09];
                r["ct"]   = (int)a[0x0A];
                r["inp"]  = (int)a[0x0B];
                r["dpt"]  = (int)a[0x0C];
                r["aop"]  = a[0x0F] == 1111;
                r["addr"] = (int)a[0x16];
                r["srun"] = (int)a[0x1B];   // 0运行 1停止 2保持
                r["at"]   = (int)a[0x1D];
                r["pno"]  = (int)a[0x2B];
                r["step"] = (int)a[0x2E];
            }

            // 读2:0x4A~0x4B → PV / SV(只读)
            int n2 = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0x004A, 2), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (ModbusFrameUtil.TryParseRead(recv, n2, station, out var b) && b.Length >= 2)
            {
                r["pv"]   = (short)b[0] / Scale;
                r["svRo"] = (short)b[1] / Scale;
            }

            // 读3:0x50~0x8B(60 寄存器)→ 30 段 SP/t(交错)
            int n3 = await mgr.SendAndReceiveAsync(serial,
                ModbusFrameUtil.BuildReadHolding(station, 0x0050, 60), recv, TimeoutMs, ct).ConfigureAwait(false);
            if (ModbusFrameUtil.TryParseRead(recv, n3, station, out var g) && g.Length >= 60)
            {
                var segs = new double[30][];
                for (int i = 0; i < 30; i++)
                    segs[i] = new[] { (short)g[i * 2] / Scale, (short)g[i * 2 + 1] / Scale };
                r["segs"] = segs;   // [[sp,t]×30],工程值
            }
            return r;
        }

        public async Task<bool> WriteControlAsync(
            DtuServerManager mgr, string serial, byte station,
            string command, Dictionary<string, double> args, CancellationToken ct)
        {
            double v = args != null && args.TryGetValue("value", out var val) ? val : 0;

            // 程序段:setSegSp{n} / setSegT{n},n=1..30 → SP=0x50+(n-1)*2,t=0x51+(n-1)*2(×10)
            if (command.StartsWith("setSegSp", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(command.Substring(8), out int spn) && spn >= 1 && spn <= 30)
                return await W(mgr, serial, station, (ushort)(0x50 + (spn - 1) * 2), X10(v), ct);
            if (command.StartsWith("setSegT", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(command.Substring(7), out int tn) && tn >= 1 && tn <= 30)
                return await W(mgr, serial, station, (ushort)(0x51 + (tn - 1) * 2), X10(v), ct);

            switch (command)
            {
                case "setSv":   return await W(mgr, serial, station, 0x00, X10(v), ct);
                case "setHial": return await W(mgr, serial, station, 0x01, X10(v), ct);
                case "setLoal": return await W(mgr, serial, station, 0x02, X10(v), ct);
                case "setHdal": return await W(mgr, serial, station, 0x03, X10(v), ct);
                case "setLdal": return await W(mgr, serial, station, 0x04, X10(v), ct);
                case "setAhys": return await W(mgr, serial, station, 0x05, X10(v), ct);
                case "setCtrl": return await W(mgr, serial, station, 0x06, (ushort)v, ct);
                case "setP":    return await W(mgr, serial, station, 0x07, X10(v), ct);
                case "setI":    return await W(mgr, serial, station, 0x08, (ushort)v, ct);
                case "setD":    return await W(mgr, serial, station, 0x09, (ushort)v, ct);
                case "setCt":   return await W(mgr, serial, station, 0x0A, (ushort)v, ct);
                case "setAop":  return await W(mgr, serial, station, 0x0F, (ushort)(v != 0 ? 1111 : 3333), ct);
                case "srun":    return await W(mgr, serial, station, 0x1B, (ushort)v, ct);   // 0运行 1停止 2保持
                case "at":      return await W(mgr, serial, station, 0x1D, (ushort)v, ct);   // 0~3
                case "setPno":  return await W(mgr, serial, station, 0x2B, (ushort)v, ct);   // 0点动 / 30程序
                default: return false;
            }
        }

        private static ushort X10(double v) => (ushort)Math.Clamp((int)Math.Round(v * Scale), 0, 0xFFFF);

        /// <summary>FC06 写单寄存器 + 写+回读校验(短等回显,超时不算失败,回读比对兜底)。</summary>
        private static async Task<bool> W(
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
                catch (TimeoutException) { /* 仪表不回写 ACK → 回读校验 */ }

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
    }
}
