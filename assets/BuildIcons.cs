using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Vector source for both the executable and notification-area icons.
internal static class BuildIcons
{
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        WriteIcon(Path.Combine(args[0], "pointer.ico"), false);
        WriteIcon(Path.Combine(args[0], "pointer-paused.ico"), true);
    }
    static void WriteIcon(string path, bool paused)
    {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 256 };
        byte[][] images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        using (Bitmap bitmap = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(sizes[i] / 32f, sizes[i] / 32f);
            if (paused)
            {
                using (Pen edge = new Pen(Color.FromArgb(20, 20, 20), 2.5f))
                using (Brush fill = new SolidBrush(Color.FromArgb(255, 204, 55)))
                foreach (int x in new int[] { 4, 20 })
                {
                    graphics.FillRectangle(fill, x, 3, 8, 26);
                    graphics.DrawRectangle(edge, x, 3, 8, 26);
                }
            }
            else
            {
                PointF[] arrow = { new PointF(3, 2), new PointF(3, 27), new PointF(10, 21),
                    new PointF(15, 30), new PointF(22, 27), new PointF(17, 18), new PointF(29, 18) };
                using (Pen outline = new Pen(Color.FromArgb(20, 20, 20), 2.5f) { LineJoin = LineJoin.Round })
                {
                    graphics.FillPolygon(Brushes.White, arrow);
                    graphics.DrawPolygon(outline, arrow);
                }
            }
            graphics.Flush();
            // DIB frames also work with the .NET Framework icon bitmap decoder.
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, sizes[i], sizes[i]), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[data.Stride * sizes[i]];
            try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
            finally { bitmap.UnlockBits(data); }
            using (MemoryStream dib = new MemoryStream())
            using (BinaryWriter frame = new BinaryWriter(dib))
            {
                int size = sizes[i], maskStride = ((size + 31) / 32) * 4;
                frame.Write(40); frame.Write(size); frame.Write(size * 2);
                frame.Write((ushort)1); frame.Write((ushort)32); frame.Write(0);
                frame.Write(size * size * 4 + maskStride * size);
                frame.Write(0); frame.Write(0); frame.Write(0); frame.Write(0);
                for (int row = size - 1; row >= 0; row--) frame.Write(pixels, row * data.Stride, size * 4);
                for (int row = size - 1; row >= 0; row--)
                {
                    byte[] mask = new byte[maskStride];
                    for (int x = 0; x < size; x++) if (pixels[row * data.Stride + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    frame.Write(mask);
                }
                images[i] = dib.ToArray();
            }
        }
        using (BinaryWriter file = new BinaryWriter(File.Create(path)))
        {
            file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)32);
                file.Write(images[i].Length); file.Write(offset); offset += images[i].Length;
            }
            foreach (byte[] image in images) file.Write(image);
        }
    }
}
