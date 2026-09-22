using System.Threading.Tasks;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Devices;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>二维码生成(PNG)。二维码内容默认是设备的绑定/详情页 URL,扫码即可跳转。</summary>
    public static class QrService
    {
        /// <summary>把任意文本生成为 PNG 字节(可直接作为图片返回/下载)。</summary>
        public static byte[] PngFor(string text, int pixelsPerModule = 10)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            return png.GetGraphic(pixelsPerModule);
        }

        /// <summary>
        /// 一台设备的二维码内容。
        ///
        /// 绑了物模型(MQTT 网关设备)→ 按物模型的 meta.qrCode 生成绑定链接,
        /// 微信/系统相机扫了能直接打开 bind.html。
        /// 没绑物模型(透传设备)→ 还是纯序列号,跟以前一模一样。
        ///
        /// APP 侧要两种都认:能当 URL 解析就取 ?code=,否则整串当设备标识码用
        /// —— 这样已经贴出去的纯序列号标签继续有效。
        /// </summary>
        public static async Task<string> ContentForDeviceAsync(AppDbContext db, Device dev, string? publicBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(dev.ProductKey)) return dev.DtuSerial;

            var model = await db.ProductModels
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProductKey == dev.ProductKey);
            if (model is null) return dev.DtuSerial;

            return ModelSpec.QrContent(model.ConfigJson, dev.Code, dev.ProductKey, publicBaseUrl);
        }
    }
}
