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
    }
}
