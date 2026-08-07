using System;

namespace MaxChemical.DtuServer.Devices
{
    /// <summary>
    /// Modbus RTU 帧工具:建读/写帧(含 CRC)、解析读应答。供各设备 Profile 复用。
    /// 字节序:寄存器大端;int32 按 ABCD(高字在前)。
    /// </summary>
    public static class ModbusFrameUtil
    {
        // CRC16-Modbus(poly 0xA001, init 0xFFFF, 低字节在前)
        public static ushort Crc16(byte[] data, int offset, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        private static byte[] WithCrc(byte[] body)
        {
            var f = new byte[body.Length + 2];
            Array.Copy(body, f, body.Length);
            ushort crc = Crc16(body, 0, body.Length);
            f[body.Length] = (byte)(crc & 0xFF);
            f[body.Length + 1] = (byte)(crc >> 8);
            return f;
        }

        /// <summary>FC03 读保持寄存器。</summary>
        public static byte[] BuildReadHolding(byte slave, ushort start, ushort count)
            => WithCrc(new byte[] { slave, 0x03, (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count });

        /// <summary>FC06 写单个寄存器。</summary>
        public static byte[] BuildWriteSingle(byte slave, ushort reg, ushort value)
            => WithCrc(new byte[] { slave, 0x06, (byte)(reg >> 8), (byte)reg, (byte)(value >> 8), (byte)value });

        /// <summary>FC10 写多个寄存器(words 为各寄存器值,大端写入)。</summary>
        public static byte[] BuildWriteMultiple(byte slave, ushort start, ushort[] words)
        {
            var body = new byte[7 + words.Length * 2];
            body[0] = slave; body[1] = 0x10;
            body[2] = (byte)(start >> 8); body[3] = (byte)start;
            body[4] = (byte)(words.Length >> 8); body[5] = (byte)words.Length;
            body[6] = (byte)(words.Length * 2);
            for (int i = 0; i < words.Length; i++)
            {
                body[7 + i * 2] = (byte)(words[i] >> 8);
                body[8 + i * 2] = (byte)(words[i] & 0xFF);
            }
            return WithCrc(body);
        }

        /// <summary>int32(ABCD)拆成两个寄存器(高字、低字)。</summary>
        public static ushort[] Int32ToRegs(int value)
            => new[] { (ushort)((value >> 16) & 0xFFFF), (ushort)(value & 0xFFFF) };

        /// <summary>
        /// 解析 FC03 读应答:校验站号/功能码/CRC,提取寄存器(大端)。失败返回 false。
        /// </summary>
        public static bool TryParseRead(byte[] buf, int len, byte expectedSlave, out ushort[] regs)
        {
            regs = Array.Empty<ushort>();
            if (len < 5) return false;
            if (buf[0] != expectedSlave) return false;
            if ((buf[1] & 0x80) != 0) return false;     // 异常应答
            if (buf[1] != 0x03) return false;
            int byteCount = buf[2];
            if (3 + byteCount + 2 > len) return false;
            // CRC 校验
            ushort crc = Crc16(buf, 0, 3 + byteCount);
            if (buf[3 + byteCount] != (byte)(crc & 0xFF) || buf[3 + byteCount + 1] != (byte)(crc >> 8))
                return false;

            int n = byteCount / 2;
            regs = new ushort[n];
            for (int i = 0; i < n; i++)
                regs[i] = (ushort)((buf[3 + i * 2] << 8) | buf[3 + i * 2 + 1]);
            return true;
        }

        /// <summary>两个寄存器(高字、低字)合成 int32(ABCD)。</summary>
        public static int RegsToInt32(ushort[] regs, int hiIndex)
            => (regs[hiIndex] << 16) | regs[hiIndex + 1];

        /// <summary>两个寄存器(高字在前,ABCD)合成 IEEE-754 float。</summary>
        public static float RegsToFloat(ushort[] regs, int hiIndex)
        {
            var bytes = new byte[4]
            {
                (byte)(regs[hiIndex] >> 8), (byte)(regs[hiIndex] & 0xFF),
                (byte)(regs[hiIndex + 1] >> 8), (byte)(regs[hiIndex + 1] & 0xFF),
            };
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>IEEE-754 float 拆成两个寄存器(高字在前,ABCD)。</summary>
        public static ushort[] FloatToRegs(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return new[]
            {
                (ushort)((bytes[0] << 8) | bytes[1]),
                (ushort)((bytes[2] << 8) | bytes[3]),
            };
        }
    }
}
