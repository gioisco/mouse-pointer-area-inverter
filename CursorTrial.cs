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
    static void Apply(IntPtr cursor)
    {
        IntPtr copy = Copy(cursor);
        // SetSystemCursor consumes the handle. Never pass our retained backup.
        if (!SetSystemCursor(copy, Arrow))
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
        IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            // shortcut: monochrome/animated themes need a dedicated path before supporting them.
            if (info.Color == IntPtr.Zero) throw new NotSupportedException("Monochrome cursor: trial cancelled without changes.");
            dc = GetDC(IntPtr.Zero);
            Check(dc != IntPtr.Zero, "GetDC");
            Header header, maskHeader;
            byte[] color = ReadBitmap(info.Color, dc, out header);
            byte[] mask = ReadBitmap(info.Mask, dc, out maskHeader);
            if (color.Length != mask.Length) throw new NotSupportedException("Unexpected cursor mask dimensions.");
            bool alpha = false;
            for (int i = 3; i < color.Length; i += 4) alpha |= color[i] != 0;
            Invert(color, mask, alpha);
            IntPtr bits;
            bitmap = CreateDIBSection(dc, ref header, 0, out bits, IntPtr.Zero, 0);
            Check(bitmap != IntPtr.Zero && bits != IntPtr.Zero, "CreateDIBSection");
            Marshal.Copy(color, 0, bits, color.Length);
            IconInfo replacement = info;
            replacement.Color = bitmap;
            IntPtr result = CreateIconIndirect(ref replacement);
            Check(result != IntPtr.Zero, "CreateIconIndirect");
            Console.WriteLine("Cursor: {0}x{1}; hotspot {2},{3}; alpha {4}", header.Width, -header.Height, info.HotX, info.HotY, alpha);
            return result;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
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
            bool adaptive = args.Length >= 1 && args[0] == "--adaptive";
            int seconds = 60;
            if (adaptive && (args.Length > 2 || (args.Length == 2 && (!int.TryParse(args[1], out seconds) || seconds < 1 || seconds > 3600))))
                throw new ArgumentException("Adaptive duration must be 1-3600 seconds.");
            if (!adaptive && (args.Length != 1 || args[0] != "--trial"))
                throw new ArgumentException("Usage: CursorTrial.exe --self-test | --trial | --adaptive [seconds] | --restore-system");
            SetProcessDPIAware();
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
