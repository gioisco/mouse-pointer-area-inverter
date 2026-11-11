using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Keeps Windows responsible for pointer movement and trails.
internal static partial class CursorTrial
{
    const uint Arrow = 32512;
    [StructLayout(LayoutKind.Sequential)] struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool Icon;
        public uint HotX, HotY; public IntPtr Mask, Color;
    }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public int Type, Width, Height, Stride; public ushort Planes, Bits; public IntPtr Data;
    }
    [StructLayout(LayoutKind.Sequential)] struct Header
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CopyImage(IntPtr image, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr cursor);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetObject(IntPtr bitmap, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] data, ref Header header, uint usage);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref Header info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);

    static readonly ManualResetEvent Stop = new ManualResetEvent(false);

    static void Check(bool success, string operation)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
    static IntPtr Copy(IntPtr cursor)
    {
        IntPtr copy = CopyImage(cursor, 2, 0, 0, 0);
        Check(copy != IntPtr.Zero, "CopyImage");
        return copy;
    }
    static void Apply(IntPtr cursor, uint id = Arrow)
    {
        IntPtr copy = Copy(cursor);
        // SetSystemCursor consumes the handle. Never pass our retained backup.
        if (!SetSystemCursor(copy, id))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyCursor(copy);
            throw new Win32Exception(error, "SetSystemCursor");
        }
    }
    static uint Trails()
    {
        uint value = 0;
        Check(SystemParametersInfo(0x005E, 0, ref value, 0), "SPI_GETMOUSETRAILS");
        return value;
    }
    static byte[] ReadBitmap(IntPtr bitmap, IntPtr dc, out Header header)
    {
        BitmapInfo info;
        Check(GetObject(bitmap, Marshal.SizeOf(typeof(BitmapInfo)), out info) != 0, "GetObject");
        header = new Header { Size = 40, Width = info.Width, Height = -Math.Abs(info.Height), Planes = 1, Bits = 32 };
        byte[] data = new byte[checked(info.Width * Math.Abs(info.Height) * 4)];
        Check(GetDIBits(dc, bitmap, 0, (uint)Math.Abs(info.Height), data, ref header, 0) == Math.Abs(info.Height), "GetDIBits");
        return data;
    }
    static void Invert(byte[] color, byte[] mask, bool alpha)
    {
        for (int p = 0; p < color.Length; p += 4)
        {
            // Preserve transparent and XOR-mask pixels in legacy cursors.
            if (!alpha && mask[p] != 0) continue;
            int maximum = alpha ? color[p + 3] : 255;
            for (int c = 0; c < 3; c++)
            {
                if (color[p + c] > maximum) throw new InvalidOperationException("Unsupported non-premultiplied cursor pixel.");
                color[p + c] = (byte)(maximum - color[p + c]);
            }
        }
    }
    static void SelfTest()
    {
        byte[] color = { 0, 20, 128, 128, 0, 0, 0, 0 };
        byte[] original = (byte[])color.Clone();
        Invert(color, null, true);
        if (color[0] != 128 || color[1] != 108 || color[2] != 0 || color[3] != 128 || color[4] != 0)
            throw new Exception("Alpha inversion self-test failed.");
        Invert(color, null, true);
        if (!Equal(original, color)) throw new Exception("Double inversion self-test failed.");
        byte[] legacy = { 255, 255, 255, 0, 255, 255, 255, 0 };
        Invert(legacy, new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 }, false);
        if (legacy[0] != 0 || legacy[4] != 255) throw new Exception("Legacy mask self-test failed.");
        // AND/XOR states: opaque black, opaque white, transparent, screen invert.
        byte[] monoMask = new byte[32];
        monoMask[8] = monoMask[12] = monoMask[20] = monoMask[28] = 255;
        byte[] mono = MonochromePixels(monoMask, 4);
        if (mono[0] != 0 || mono[3] != 255 || mono[4] != 255 || mono[7] != 255 ||
            mono[11] != 0 || mono[12] != 255 || mono[15] != 255)
            throw new Exception("Monochrome cursor expansion self-test failed.");
        byte[] straight = { 0, 0, 219, 18, 200, 200, 200, 0 };
        NormalizeColor(straight, new byte[8]);
        if (straight[2] != 15 || straight[3] != 18 || straight[4] != 0)
            throw new Exception("Straight-alpha normalization self-test failed.");
        byte[] xorColor = { 255, 255, 255, 0, 0, 0, 0, 0 };
        NormalizeColor(xorColor, new byte[] { 255, 255, 255, 0, 255, 255, 255, 0 });
        if (xorColor[3] != 255 || xorColor[7] != 0)
            throw new Exception("Legacy color XOR normalization self-test failed.");
    }
    static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static IntPtr Inverted(IntPtr original)
    {
        IconInfo info;
        Check(GetIconInfo(original, out info), "GetIconInfo");
        try { return info.Color == IntPtr.Zero ? MonochromeAppearance(info, true) : ColorAppearance(info, true); }
        finally
        {
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
        }
    }
    static void NormalizeColor(byte[] color, byte[] mask)
    {
        bool alpha = false, straight = false;
        for (int p = 0; p < color.Length; p += 4)
        {
            int a = color[p + 3];
            alpha |= a != 0;
            if (a > 0 && (color[p] > a || color[p + 1] > a || color[p + 2] > a)) straight = true;
        }
        for (int p = 0; p < color.Length; p += 4)
        {
            if (!alpha)
            {
                bool transparent = mask[p] != 0 && color[p] == 0 && color[p + 1] == 0 && color[p + 2] == 0;
                color[p + 3] = transparent ? (byte)0 : (byte)255;
            }
            for (int c = 0; c < 3; c++)
                if (color[p + 3] == 0) color[p + c] = 0;
                else if (straight) color[p + c] = (byte)((color[p + c] * color[p + 3] + 127) / 255);
        }
    }
    static IntPtr ColorAppearance(IconInfo info, bool inverse)
    {
        IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            Check(dc != IntPtr.Zero, "GetDC");
            Header header, maskHeader;
            byte[] color = ReadBitmap(info.Color, dc, out header);
            byte[] mask = ReadBitmap(info.Mask, dc, out maskHeader);
            if (color.Length != mask.Length) throw new NotSupportedException("Unexpected cursor mask dimensions.");
            NormalizeColor(color, mask);
            if (inverse) Invert(color, null, true);
            IntPtr bits;
            bitmap = CreateDIBSection(dc, ref header, 0, out bits, IntPtr.Zero, 0);
            Check(bitmap != IntPtr.Zero && bits != IntPtr.Zero, "CreateDIBSection");
            Marshal.Copy(color, 0, bits, color.Length);
            IconInfo replacement = info;
            replacement.Color = bitmap;
            IntPtr result = CreateIconIndirect(ref replacement);
            Check(result != IntPtr.Zero, "CreateIconIndirect");
            return result;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        }
    }
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, byte[] pixels);

    static byte[] MonochromePixels(byte[] mask, int count)
    {
        if (mask.Length != count * 8) throw new ArgumentException("Invalid monochrome planes.");
        byte[] color = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            bool and = mask[i * 4] != 0, xor = mask[(count + i) * 4] != 0;
            if (and && !xor) continue;
            byte light = xor ? (byte)255 : (byte)0;
            color[i * 4] = color[i * 4 + 1] = color[i * 4 + 2] = light;
            color[i * 4 + 3] = 255;
        }
        return color;
    }

    static IntPtr MonochromeAppearance(IconInfo info, bool inverse)
    {
        IntPtr dc = GetDC(IntPtr.Zero), colorBitmap = IntPtr.Zero, maskBitmap = IntPtr.Zero;
        Check(dc != IntPtr.Zero, "GetDC monochrome cursor");
        try
        {
            Header header;
            byte[] mask = ReadBitmap(info.Mask, dc, out header);
            int width = header.Width, height = -header.Height / 2;
            if (height <= 0 || (-header.Height % 2) != 0) throw new NotSupportedException("Invalid monochrome mask height.");
            byte[] color = MonochromePixels(mask, checked(width * height));
            if (inverse) Invert(color, null, true);
            int stride = ((width + 15) / 16) * 2;
            byte[] transparent = new byte[checked(stride * height)];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (color[(y * width + x) * 4 + 3] == 0)
                    transparent[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            maskBitmap = CreateBitmap(width, height, 1, 1, transparent);
            Check(maskBitmap != IntPtr.Zero, "CreateBitmap monochrome mask");
            header.Height = -height;
            IntPtr bits;
            colorBitmap = CreateDIBSection(dc, ref header, 0, out bits, IntPtr.Zero, 0);
            Check(colorBitmap != IntPtr.Zero && bits != IntPtr.Zero, "CreateDIBSection monochrome cursor");
            Marshal.Copy(color, 0, bits, color.Length);
            IconInfo replacement = info;
            replacement.Color = colorBitmap; replacement.Mask = maskBitmap;
            IntPtr cursor = CreateIconIndirect(ref replacement);
            Check(cursor != IntPtr.Zero, "CreateIconIndirect monochrome cursor");
            return cursor;
        }
        finally
        {
            if (colorBitmap != IntPtr.Zero) DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero) DeleteObject(maskBitmap);
            ReleaseDC(IntPtr.Zero, dc);
        }
    }
    static byte[] Pixels(IntPtr cursor)
    {
        IconInfo info;
        Check(GetIconInfo(cursor, out info), "GetIconInfo verification");
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            Check(dc != IntPtr.Zero, "GetDC verification");
            Header header;
            byte[] mask = ReadBitmap(info.Mask, dc, out header);
            byte[] color = info.Color == IntPtr.Zero ? new byte[0] : ReadBitmap(info.Color, dc, out header);
            byte[] combined = new byte[mask.Length + color.Length + 8];
            Buffer.BlockCopy(BitConverter.GetBytes(info.HotX), 0, combined, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(info.HotY), 0, combined, 4, 4);
            Buffer.BlockCopy(mask, 0, combined, 8, mask.Length);
            Buffer.BlockCopy(color, 0, combined, 8 + mask.Length, color.Length);
            return combined;
        }
        finally
        {
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        }
    }
    static int Main(string[] args)
    {
        using (Mutex mutex = new Mutex(false, "Local\\MousePointerAreaInverterTrial"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { Console.Error.WriteLine("A cursor trial is already running."); return 1; }
            try { return Run(args); }
            finally { mutex.ReleaseMutex(); }
        }
    }
    static int Run(string[] args)
    {
        IntPtr original = IntPtr.Zero, inverted = IntPtr.Zero;
        bool changed = false;
        try
        {
            SelfTest();
            AreaSelfTest();
            if (args.Length == 1 && args[0] == "--self-test") { Console.WriteLine("Self-tests passed; no system changes."); return 0; }
            if (args.Length == 1 && args[0] == "--restore-system")
            {
                Check(SystemParametersInfo(0x0057, 0, IntPtr.Zero, 0), "SPI_SETCURSORS");
                Console.WriteLine("All system cursors reloaded from the configured theme.");
                return 0;
            }
            bool allShapes = args.Length >= 1 && args[0] == "--adaptive-all";
            bool adaptive = args.Length >= 1 && (args[0] == "--adaptive" || allShapes);
            int seconds = 60;
            if (adaptive && (args.Length > 2 || (args.Length == 2 && (!int.TryParse(args[1], out seconds) || seconds < 1 || seconds > 3600))))
                throw new ArgumentException("Adaptive duration must be 1-3600 seconds.");
            if (!adaptive && (args.Length != 1 || args[0] != "--trial"))
                throw new ArgumentException("Usage: CursorTrial.exe --self-test | --trial | --adaptive [seconds] | --adaptive-all [seconds] | --restore-system");
            SetProcessDPIAware();
            if (allShapes)
            {
                uint initialTrails = Trails();
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; Stop.Set(); };
                using (CursorSet shapes = new CursorSet())
                {
                    RunAdaptive(IntPtr.Zero, IntPtr.Zero, seconds, true, shapes);
                    shapes.RestoreAndVerify();
                    Check(Trails() == initialTrails, "All-shape trail verification");
                }
                Console.WriteLine("All standard cursor backups restored and verified. Trail setting unchanged.");
                return 0;
            }
            uint trails = Trails();
            IntPtr shared = LoadCursor(IntPtr.Zero, (IntPtr)Arrow);
            Check(shared != IntPtr.Zero, "LoadCursor");
            original = Copy(shared);
            byte[] before = Pixels(original);
            inverted = Inverted(original);
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; Stop.Set(); };
            if (adaptive)
            {
                // Mark before entering the loop: restore even if the first update throws.
                changed = true;
                RunAdaptive(original, inverted, seconds);
            }
            else
            {
            using (StreamWriter log = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cursor-trial.csv")))
            {
                log.WriteLine("step,state,apply_ms,mouse_trails");
                Console.WriteLine("Trails: {0}. Move the mouse over a normal desktop window. Ctrl+C stops the trial.", trails);
                if (!Stop.WaitOne(2000))
                {
                    for (int i = 0; i < 6 && !Stop.WaitOne(0); i++)
                    {
                        bool inverse = i % 2 == 0;
                        Stopwatch watch = Stopwatch.StartNew();
                        changed = true;
                        Apply(inverse ? inverted : original);
                        double ms = watch.Elapsed.TotalMilliseconds;
                        uint current = Trails();
                        log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F4},{3}", i + 1, inverse ? "inverted" : "original", ms, current));
                        log.Flush();
                        Console.WriteLine("{0}: {1:F3} ms; trails {2}", inverse ? "Inverted" : "Original", ms, current);
                        if (current != trails) throw new Exception("Trail setting changed during the trial.");
                        if (Stop.WaitOne(2000)) break;
                    }
                }
            }
            }
            Apply(original);
            changed = false;
            Check(Equal(before, Pixels(LoadCursor(IntPtr.Zero, (IntPtr)Arrow))), "Restored cursor comparison");
            Check(Trails() == trails, "Restored trail setting comparison");
            Console.WriteLine("Original cursor restored and pixels/hotspot verified. Trail setting unchanged.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally
        {
            if (changed && original != IntPtr.Zero)
            {
                try { Apply(original); Console.WriteLine("Original cursor restored after interruption/error."); }
                catch (Exception e) { Console.Error.WriteLine("RESTORE FAILED: " + e.Message + "; run CursorTrial.exe --restore-system"); }
            }
            if (inverted != IntPtr.Zero) DestroyCursor(inverted);
            if (original != IntPtr.Zero) DestroyCursor(original);
        }
    }
}
