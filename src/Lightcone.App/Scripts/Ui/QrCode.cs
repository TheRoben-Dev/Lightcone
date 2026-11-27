using Godot;
using QRCoder;

namespace Lightcone.App.Ui;

/// <summary>QR codes (phone remote address) as textures: dark modules on white, with the quiet zone.</summary>
public static class QrCode
{
    public static ImageTexture? Texture(string text, int scale)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var m = data.ModuleMatrix;
            var n = m.Count;
            var img = Image.CreateEmpty(n * scale, n * scale, false, Image.Format.Rgb8);
            img.Fill(Colors.White);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    if (m[y][x])
                    {
                        img.FillRect(new Rect2I(x * scale, y * scale, scale, scale), Colors.Black);
                    }
                }
            }
            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
