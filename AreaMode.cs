using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

internal static partial class CursorTrial
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CursorInfo
    {
        public uint Size, Flags; public IntPtr Cursor; public Point Position;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint operation);

    // Fractions of light pixels tolerate text on a light surface better than the mean.
    static bool DesiredState(bool inverse, int bright, int total)
    {
        if (total <= 0) throw new ArgumentException("Empty area.");
        return inverse ? (long)bright * 100 > (long)total * 55 : (long)bright * 100 >= (long)total * 75;
    }
    sealed class StableState
    {
        public bool Inverse;
        int confirmations;
        public bool Observe(bool desired)
        {
            if (desired == Inverse) { confirmations = 0; return false; }
            if (++confirmations < 2) return false;
            Inverse = desired;
            confirmations = 0;
            return true;
        }
        public void ResetPending() { confirmations = 0; }
    }
    static void AreaSelfTest()
    {
        if (!DesiredState(false, 75, 100) || DesiredState(false, 74, 100) ||
            !DesiredState(true, 56, 100) || DesiredState(true, 55, 100))
            throw new Exception("Area hysteresis self-test failed.");
        StableState state = new StableState();
        if (state.Observe(true) || !state.Observe(true) || !state.Inverse || state.Observe(false) || state.Observe(true))
            throw new Exception("Area stability self-test failed.");
        if (state.Observe(false) || !state.Observe(false) || state.Inverse)
            throw new Exception("Area return self-test failed.");
        if (Brightness(255, 255, 255) != 255 || Brightness(0, 0, 0) != 0)
            throw new Exception("Brightness self-test failed.");
    }
    static int Brightness(byte r, byte g, byte b) { return (54 * r + 183 * g + 19 * b) >> 8; }

    sealed class AreaSampler : IDisposable
    {
        IntPtr screen, memory, bitmap, previous, bits;
        readonly byte[] pixels;
        readonly int[] weights;
        readonly int cursorWidth, cursorHeight, hotX, hotY;
        public AreaSampler(IntPtr original)
        {
            IconInfo info;
            Check(GetIconInfo(original, out info), "GetIconInfo sampling dimensions");
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                Check(dc != IntPtr.Zero, "Cursor mask DC");
                try
                {
                    Header shapeHeader, maskHeader;
                    byte[] mask = ReadBitmap(info.Mask, dc, out maskHeader);
                    byte[] shape;
                    if (info.Color == IntPtr.Zero)
                    {
                        shapeHeader = maskHeader;
                        shapeHeader.Height /= 2;
                        shape = MonochromePixels(mask, checked(shapeHeader.Width * -shapeHeader.Height));
                    }
                    else
                    {
                        shape = ReadBitmap(info.Color, dc, out shapeHeader);
                        NormalizeColor(shape, mask);
                    }
                    cursorWidth = shapeHeader.Width; cursorHeight = -shapeHeader.Height;
                    pixels = new byte[shape.Length];
                    weights = new int[shape.Length / 4];
                    for (int p = 0; p < weights.Length; p++)
                        weights[p] = shape[p * 4 + 3];
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                hotX = checked((int)info.HotX); hotY = checked((int)info.HotY);
            }
            finally { DeleteObject(info.Color); DeleteObject(info.Mask); }
            try
            {
                screen = GetDC(IntPtr.Zero);
                Check(screen != IntPtr.Zero, "GetDC sampler");
                memory = CreateCompatibleDC(screen);
                Check(memory != IntPtr.Zero, "CreateCompatibleDC sampler");
                Header header = new Header { Size = 40, Width = cursorWidth, Height = -cursorHeight, Planes = 1, Bits = 32 };
                bitmap = CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
                Check(bitmap != IntPtr.Zero && bits != IntPtr.Zero, "CreateDIBSection sampler");
                previous = SelectObject(memory, bitmap);
                Check(previous != IntPtr.Zero && previous != new IntPtr(-1), "SelectObject sampler");
            }
            catch { Dispose(); throw; }
        }
        public void Sample(Point point, out int bright, out int count)
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            int x = point.X - hotX, y = point.Y - hotY;
            int clipX = Math.Max(left, x), clipY = Math.Max(top, y);
            int clipWidth = Math.Min(left + width, x + cursorWidth) - clipX;
            int clipHeight = Math.Min(top + height, y + cursorHeight) - clipY;
            if (clipWidth <= 0 || clipHeight <= 0) throw new InvalidOperationException("Cursor outside the desktop.");
            Check(BitBlt(memory, clipX - x, clipY - y, clipWidth, clipHeight, screen, clipX, clipY, 0x00CC0020), "BitBlt sampler");
            Check(GdiFlush(), "GdiFlush sampler");
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            bright = count = 0;
            for (int row = 0; row < cursorHeight; row++)
            for (int col = 0; col < cursorWidth; col++)
            {
                int px = x + col, py = y + row;
                if (px < clipX || px >= clipX + clipWidth || py < clipY || py >= clipY + clipHeight) continue;
                int index = row * cursorWidth + col, p = index * 4;
                int light = Brightness(pixels[p + 2], pixels[p + 1], pixels[p]);
                if (light >= 220) bright += weights[index];
                count += weights[index];
            }
            if (count == 0) throw new InvalidOperationException("No visible cursor pixels on the desktop.");
        }
        public void Dispose()
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            previous = bitmap = memory = screen = IntPtr.Zero;
        }
    }
    static void RunAdaptive(IntPtr original, IntPtr inverted, int seconds, bool applyChanges = true, CursorSet shapes = null)
    {
        const int period = 50;
        StableState state = new StableState();
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adaptive-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        using (AreaSampler sampler = shapes == null ? new AreaSampler(original) : null)
        using (StreamWriter log = new StreamWriter(logPath, false, System.Text.Encoding.UTF8, 65536))
        using (Process process = Process.GetCurrentProcess())
        {
            List<double> samples = new List<double>(seconds * 20);
            log.WriteLine("elapsed_ms,event,bright_fraction,inverted,sample_ms,apply_ms" + (shapes == null ? "" : ",shape"));
            Console.WriteLine("Adaptive {0}, {1}s; Ctrl+C stops and restores. Trails: {2}.", shapes == null ? "arrow" : "standard cursors", seconds, Trails());
            Console.WriteLine(shapes == null ? "Only the standard arrow changes. Move between light and dark surfaces." : "Text, links and standard resize/move cursors are included; animated and application-owned cursors are unchanged.");
            Console.WriteLine("Log: {0}", logPath);
            double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            Stopwatch clock = Stopwatch.StartNew();
            int switches = 0, skips = 0, failures = 0;
            PreparedCursor previousShape = null;
            while (!Stop.WaitOne(0) && clock.Elapsed.TotalSeconds < seconds)
            {
                CursorInfo cursor = new CursorInfo { Size = (uint)Marshal.SizeOf(typeof(CursorInfo)) };
                PreparedCursor activeShape = null;
                // The input desktop can be unavailable while locked. Retain the last image and retry.
                if (!GetCursorInfo(ref cursor)) { failures++; state.ResetPending(); }
                else if (cursor.Flags != 1 || (shapes == null ? cursor.Cursor != LoadCursor(IntPtr.Zero, (IntPtr)Arrow) : !shapes.TryGet(cursor.Cursor, out activeShape)))
                { skips++; state.ResetPending(); if (previousShape != null) previousShape.State.ResetPending(); previousShape = null; }
                else
                {
                    StableState activeState = activeShape == null ? state : activeShape.State;
                    if (activeShape != previousShape)
                    {
                        if (previousShape != null) previousShape.State.ResetPending();
                        activeState.ResetPending(); previousShape = activeShape;
                    }
                    if (activeShape != null) activeShape.EnsureCoherentNormal();
                    int bright, count;
                    long sampleStart = Stopwatch.GetTimestamp();
                    (activeShape == null ? sampler : activeShape.Sampler).Sample(cursor.Position, out bright, out count);
                    double sampleMs = (Stopwatch.GetTimestamp() - sampleStart) * 1000.0 / Stopwatch.Frequency;
                    samples.Add(sampleMs);
                    double applyMs = 0;
                    if (activeState.Observe(DesiredState(activeState.Inverse, bright, count)))
                    {
                        Stopwatch update = Stopwatch.StartNew();
                        if (applyChanges)
                        {
                            if (activeShape == null) Apply(activeState.Inverse ? inverted : original);
                            else activeShape.Set(activeState.Inverse);
                        }
                        applyMs = update.Elapsed.TotalMilliseconds;
                        switches++;
                        Console.WriteLine("{0:F1}s: {1}{2}, light area {3:P0}", clock.Elapsed.TotalSeconds, activeShape == null ? "" : activeShape.Name + " ", activeState.Inverse ? "inverted" : "original", bright / (double)count);
                    }
                    log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F1},sample,{1:F4},{2},{3:F4},{4:F4}{5}",
                        clock.Elapsed.TotalMilliseconds, bright / (double)count, activeState.Inverse ? 1 : 0, sampleMs, applyMs, activeShape == null ? "" : "," + activeShape.Name));
                }
                // Never catch up with a burst of reads when a capture overruns its interval.
                int delay = Math.Max(1, period - (int)(clock.Elapsed.TotalMilliseconds % period));
                if (Stop.WaitOne(delay)) break;
            }
            double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
            double elapsed = clock.Elapsed.TotalSeconds;
            samples.Sort();
            double p95 = samples.Count == 0 ? 0 : samples[(int)Math.Ceiling(samples.Count * .95) - 1];
            Console.WriteLine("{0:F1}s; samples {1}; switches {2}; skipped ticks {3}; unavailable ticks {4}; CPU {5:F3} ms/s; capture p95 {6:F3} ms.",
                elapsed, samples.Count, switches, skips, failures, cpu / elapsed, p95);
            log.WriteLine(string.Format(CultureInfo.InvariantCulture, "# elapsed_s={0:F3};cpu_ms_per_s={1:F3};samples={2};switches={3};skips={4};unavailable={5};capture_p95_ms={6:F4}",
                elapsed, cpu / elapsed, samples.Count, switches, skips, failures, p95));
        }
    }

    sealed class PreparedCursor : IDisposable
    {
        public readonly uint Id;
        public readonly string Name;
        public readonly IntPtr Shared;
        public readonly StableState State = new StableState();
        public AreaSampler Sampler;
        IntPtr backup, normal, inverse;
        byte[] before;
        bool monochrome, touched, initialized;
        public PreparedCursor(uint id, string name)
        {
            Id = id; Name = name;
            try
            {
                Shared = LoadCursor(IntPtr.Zero, (IntPtr)id);
                Check(Shared != IntPtr.Zero, "LoadCursor " + name);
                backup = Copy(Shared); before = Pixels(backup);
                IconInfo info;
                Check(GetIconInfo(backup, out info), "GetIconInfo " + name);
                try
                {
                    monochrome = info.Color == IntPtr.Zero;
                    normal = monochrome ? MonochromeAppearance(info, false) : ColorAppearance(info, false);
                }
                finally { if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); if (info.Color != IntPtr.Zero) DeleteObject(info.Color); }
                inverse = Inverted(backup);
                Sampler = new AreaSampler(backup);
                Console.WriteLine("Prepared {0} ({1})", name, monochrome ? "monochrome" : "color");
            }
            catch { Dispose(); throw; }
        }
        public void EnsureCoherentNormal()
        {
            if (initialized) return;
            Set(false);
            initialized = true;
        }
        public void Set(bool inverted)
        {
            touched = true;
            Apply(inverted ? inverse : normal, Id);
        }
        public void RestoreAndVerify()
        {
            if (touched) { Apply(backup, Id); touched = false; }
            Check(Equal(before, Pixels(LoadCursor(IntPtr.Zero, (IntPtr)Id))), "Restore verification " + Name);
        }
        public void Dispose()
        {
            try { if (touched) Apply(backup, Id); }
            finally
            {
                if (Sampler != null) Sampler.Dispose();
                if (inverse != IntPtr.Zero) DestroyCursor(inverse);
                if (normal != IntPtr.Zero) DestroyCursor(normal);
                if (backup != IntPtr.Zero) DestroyCursor(backup);
                inverse = normal = backup = IntPtr.Zero; touched = false;
            }
        }
    }

    sealed class CursorSet : IDisposable
    {
        readonly Dictionary<IntPtr, PreparedCursor> cursors = new Dictionary<IntPtr, PreparedCursor>();
        public CursorSet()
        {
            uint[] ids = { Arrow, 32513, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649 };
            string[] names = { "arrow", "text", "cross", "up", "resize-nwse", "resize-nesw", "resize-ew", "resize-ns", "move", "unavailable", "link" };
            string[] values = { "Arrow", "IBeam", "Crosshair", "UpArrow", "SizeNWSE", "SizeNESW", "SizeWE", "SizeNS", "SizeAll", "No", "Hand" };
            try
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    string path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", values[i], "") as string;
                    if (path != null && path.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Skipped animated " + names[i]); continue; }
                    PreparedCursor cursor = new PreparedCursor(ids[i], names[i]);
                    if (cursors.ContainsKey(cursor.Shared)) { cursor.Dispose(); throw new NotSupportedException("Aliased system cursor handles are not supported."); }
                    cursors.Add(cursor.Shared, cursor);
                }
            }
            catch { Dispose(); throw; }
        }
        public bool TryGet(IntPtr handle, out PreparedCursor cursor) { return cursors.TryGetValue(handle, out cursor); }
        public void RestoreAndVerify()
        {
            Exception failure = null;
            foreach (PreparedCursor cursor in cursors.Values)
            {
                try { cursor.RestoreAndVerify(); }
                catch (Exception e) { Console.Error.WriteLine(e.Message); failure = e; }
            }
            if (failure != null) throw new Exception("Some standard cursors could not be restored/verified.", failure);
        }
        public void Dispose()
        {
            Exception failure = null;
            foreach (PreparedCursor cursor in cursors.Values)
            {
                try { cursor.Dispose(); }
                catch (Exception e) { Console.Error.WriteLine(e.Message); failure = e; }
            }
            cursors.Clear();
            if (failure != null) throw new Exception("Cursor cleanup failed; run CursorTrial.exe --restore-system.", failure);
        }
    }
}
