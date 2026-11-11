using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Read-only feasibility probe. It never changes the pointer or mouse settings.
internal static class Probe
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref Header info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    static double Percentile(List<double> values, double fraction)
    {
        values.Sort();
        return values[Math.Max(0, (int)Math.Ceiling(values.Count * fraction) - 1)];
    }

    static int Main(string[] args)
    {
        try { Run(args.Length > 0 ? args[0] : "probe.csv"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    static void Run(string output)
    {
        const int size = 32, period = 50, seconds = 8;
        if (Percentile(new List<double> { 3, 1, 2 }, .95) != 3) throw new Exception("Percentile self-check failed.");
        SetProcessDPIAware();
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero, bits;
        if (screen == IntPtr.Zero) throw new Exception("Desktop DC unavailable.");
        try
        {
            memory = CreateCompatibleDC(screen);
            Header header = new Header { Size = 40, Width = size, Height = -size, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Exception("Capture buffer unavailable.");
            previous = SelectObject(memory, bitmap);
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            if (width < size || height < size) throw new Exception("Interactive desktop unavailable.");
            byte[] pixels = new byte[size * size * 4];
            long checksum = 0;
            Action capture = delegate {
                Point point;
                if (!GetCursorPos(out point)) throw new Exception("Cursor position unavailable on this desktop.");
                // Offset the probe to avoid sampling the pointer head. Trails may still cross it.
                int x = Math.Max(left, Math.Min(left + width - size, point.X + 40));
                int y = Math.Max(top, Math.Min(top + height - size, point.Y + 40));
                if (!BitBlt(memory, 0, 0, size, size, screen, x, y, 0x00CC0020) || !GdiFlush())
                    throw new Exception("Desktop capture failed.");
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                long light = 0;
                for (int p = 0; p < pixels.Length; p += 4)
                    light += 54 * pixels[p + 2] + 183 * pixels[p + 1] + 19 * pixels[p];
                checksum ^= light;
            };
            for (int i = 0; i < 10; i++) capture();
            using (Process process = Process.GetCurrentProcess())
            using (StreamWriter writer = new StreamWriter(output))
            {
                writer.WriteLine("round,mode,elapsed_s,samples,process_cpu_ms_per_s,system_busy_percent,sample_p50_ms,sample_p95_ms,sample_max_ms,working_set_bytes");
                for (int round = 1; round <= 3; round++)
                {
                    // Alternate the order to reduce a consistent warm-up/order bias.
                    string[] modes = round % 2 == 1 ? new[] { "idle", "capture" } : new[] { "capture", "idle" };
                    foreach (string mode in modes)
                    {
                        Console.WriteLine("Round {0}/3: {1}, {2}s", round, mode, seconds);
                        List<double> durations = new List<double>();
                        ulong idle0, kernel0, user0, idle1, kernel1, user1;
                        if (!GetSystemTimes(out idle0, out kernel0, out user0)) throw new Exception("System CPU counters unavailable.");
                        double cpu0 = process.TotalProcessorTime.TotalMilliseconds;
                        Stopwatch clock = Stopwatch.StartNew();
                        int count = 0;
                        while (clock.Elapsed.TotalSeconds < seconds)
                        {
                            long start = Stopwatch.GetTimestamp();
                            if (mode == "capture") capture();
                            durations.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                            count++;
                            double delay = count * period - clock.Elapsed.TotalMilliseconds;
                            if (delay > 0) Thread.Sleep((int)Math.Ceiling(delay));
                        }
                        double elapsed = clock.Elapsed.TotalSeconds;
                        double cpu = process.TotalProcessorTime.TotalMilliseconds - cpu0;
                        if (!GetSystemTimes(out idle1, out kernel1, out user1)) throw new Exception("System CPU counters unavailable.");
                        double total = (kernel1 - kernel0) + (user1 - user0);
                        double busy = total > 0 ? 100 * (1 - (idle1 - idle0) / total) : 0;
                        double p50 = Percentile(durations, .5), p95 = Percentile(durations, .95), max = durations[durations.Count - 1];
                        process.Refresh();
                        writer.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0},{1},{2:F3},{3},{4:F3},{5:F3},{6:F4},{7:F4},{8:F4},{9}",
                            round, mode, elapsed, count, cpu / elapsed, busy, p50, p95, max, process.WorkingSet64));
                        writer.Flush();
                    }
                }
            }
            Console.WriteLine("Results: {0}; checksum: {1}", Path.GetFullPath(output), checksum);
        }
        finally
        {
            if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
