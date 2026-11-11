using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class CompareNative
{
    [STAThread]
    static int Main(string[] args) { return CursorTrial.Compare(args); }
}

internal static partial class CursorTrial
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out Point point);

    static Point PathPoint(double elapsed, int width, int height)
    {
        // A four-second loop crosses the light/dark boundary twice.
        double phase = elapsed % 4.0;
        double progress = phase <= 2 ? phase / 2 : (4 - phase) / 2;
        return new Point { X = 70 + (int)((width - 140) * progress), Y = height / 2 };
    }
    static int PercentileIndex(int count) { return Math.Max(0, (int)Math.Ceiling(count * .95) - 1); }

    internal static int Compare(string[] args)
    {
        int seconds = 60, rounds = 3;
        if (args.Length > 2 || (args.Length > 0 && (!int.TryParse(args[0], out seconds) || seconds < 4 || seconds > 120)) ||
            (args.Length > 1 && (!int.TryParse(args[1], out rounds) || rounds < 1 || rounds > 8)))
        { Console.Error.WriteLine("Usage: CompareNative.exe [seconds-per-condition:4-120] [rounds:1-8]"); return 1; }
        using (Mutex mutex = new Mutex(false, "Local\\MousePointerAreaInverterTrial"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { Console.Error.WriteLine("Stop the running cursor trial first."); return 1; }
            try { return CompareWithCursor(seconds, rounds); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            finally { mutex.ReleaseMutex(); }
        }
    }

    static int CompareWithCursor(int seconds, int rounds)
    {
        SetProcessDPIAware(); SelfTest(); AreaSelfTest();
        if (PathPoint(0, 600, 300).X != 70 || PathPoint(2, 600, 300).X != 530 ||
            PathPoint(4, 600, 300).X != 70) throw new Exception("Replay self-check failed.");
        IntPtr original = IntPtr.Zero, inverse = IntPtr.Zero, native = IntPtr.Zero;
        Point beforePoint;
        Check(GetCursorPos(out beforePoint), "GetCursorPos benchmark");
        uint trails = Trails();
        Thread worker = null;
        Exception failure = null, workerFailure = null;
        string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        try
        {
            original = Copy(LoadCursor(IntPtr.Zero, (IntPtr)Arrow));
            byte[] originalPixels = Pixels(original);
            inverse = Inverted(original);
            IconInfo size;
            Check(GetIconInfo(original, out size), "Original dimensions");
            try
            {
                BitmapInfo dimensions;
                Check(GetObject(size.Color, Marshal.SizeOf(typeof(BitmapInfo)), out dimensions) != 0, "Original bitmap dimensions");
                string nativePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "arrow_i.cur");
                native = LoadImage(IntPtr.Zero, nativePath, 2, dimensions.Width, Math.Abs(dimensions.Height), 0x0010);
                Check(native != IntPtr.Zero, "Load Windows inverted cursor");
                Console.WriteLine("Native reference: {0}, scaled to {1}x{2}; trails {3}", nativePath, dimensions.Width, Math.Abs(dimensions.Height), trails);
            }
            finally { DeleteObject(size.Color); DeleteObject(size.Mask); }
            using (Form form = new Form())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            using (Process process = Process.GetCurrentProcess())
            using (StreamWriter log = new StreamWriter(report))
            {
                string[] modes = { "normal", "native-inverted", "capture-only", "adaptive" };
                int trial = 0, updates = 0;
                bool measuring = false;
                string mode = "";
                double cpuStart = 0;
                ulong idle0 = 0, kernel0 = 0, user0 = 0;
                Stopwatch phase = new Stopwatch(), measured = new Stopwatch();
                long previousTick = 0;
                System.Collections.Generic.List<double> tickDurations = new System.Collections.Generic.List<double>();
                form.Text = "Cursor comparison - Esc or close to stop";
                form.ClientSize = new Size(600, 300);
                form.StartPosition = FormStartPosition.CenterScreen;
                form.TopMost = true;
                form.KeyPreview = true;
                form.Cursor = Cursors.Arrow;
                form.Paint += delegate(object sender, PaintEventArgs e) {
                    e.Graphics.Clear(Color.White);
                    e.Graphics.FillRectangle(Brushes.DimGray, 300, 0, 300, 300);
                    for (int row = 0; row < 6; row++)
                    {
                        e.Graphics.DrawString("Text on a light background", SystemFonts.DefaultFont, Brushes.Black, 20, 30 + row * 40);
                        e.Graphics.DrawString("Text on a dark background", SystemFonts.DefaultFont, Brushes.White, 320, 30 + row * 40);
                    }
                };
                log.WriteLine("round,mode,seconds,process_cpu_ms_per_s,system_busy_percent,replay_updates,replay_tick_p95_ms,working_set_bytes,trails,logical_processors");
                Action finishWorker = delegate {
                    Stop.Set();
                    if (worker != null) { worker.Join(); worker = null; }
                    Stop.Reset();
                    if (workerFailure != null) throw new Exception("Adaptive worker failed.", workerFailure);
                };
                Action startTrial = delegate {
                    // Rotate the order so the same mode is not always measured first.
                    int round = trial / modes.Length;
                    mode = modes[(trial % modes.Length + round) % modes.Length];
                    Apply(mode == "native-inverted" ? native : original);
                    measuring = false; updates = 0; tickDurations.Clear(); previousTick = 0;
                    workerFailure = null;
                    phase.Restart();
                    if (mode == "adaptive" || mode == "capture-only")
                    {
                        bool change = mode == "adaptive";
                        worker = new Thread(delegate() {
                            try { RunAdaptive(original, inverse, seconds + 4, change); }
                            catch (Exception e) { workerFailure = e; }
                        });
                        worker.IsBackground = true;
                        worker.Start();
                    }
                    Console.WriteLine("Round {0}/{1}: {2}; 2s warm-up + {3}s measured", round + 1, rounds, mode, seconds);
                    form.Text = string.Format("Round {0}/{1}: {2} - Esc to stop", round + 1, rounds, mode);
                };
                form.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) form.Close(); };
                form.Shown += delegate {
                    try { form.Activate(); form.Refresh(); startTrial(); timer.Start(); }
                    catch (Exception e) { failure = e; form.Close(); }
                };
                timer.Interval = 16;
                timer.Tick += delegate {
                    try
                    {
                        if (!measuring && phase.Elapsed.TotalSeconds >= 2)
                        {
                            Check(Trails() == trails, "Benchmark trail setting changed");
                            Check(GetSystemTimes(out idle0, out kernel0, out user0), "System CPU start");
                            cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                            measured.Restart(); measuring = true;
                        }
                        Point local = PathPoint(measuring ? measured.Elapsed.TotalSeconds : 0, 600, 300);
                        System.Drawing.Point location = form.PointToScreen(new System.Drawing.Point(local.X, local.Y));
                        Check(SetCursorPos(location.X, location.Y), "Replay cursor position");
                        if (!measuring) return;
                        long tick = Stopwatch.GetTimestamp();
                        if (previousTick != 0) tickDurations.Add((tick - previousTick) * 1000.0 / Stopwatch.Frequency);
                        previousTick = tick; updates++;
                        if (measured.Elapsed.TotalSeconds < seconds) return;
                        double elapsed = measured.Elapsed.TotalSeconds;
                        double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                        ulong idle1, kernel1, user1;
                        Check(GetSystemTimes(out idle1, out kernel1, out user1), "System CPU end");
                        double total = (kernel1 - kernel0) + (user1 - user0);
                        double busy = total > 0 ? 100 * (1 - (idle1 - idle0) / total) : 0;
                        tickDurations.Sort();
                        double p95 = tickDurations.Count == 0 ? 0 : tickDurations[PercentileIndex(tickDurations.Count)];
                        process.Refresh();
                        log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3:F3},{4:F3},{5},{6:F3},{7},{8},{9}",
                            trial / modes.Length + 1, mode, elapsed, cpu / elapsed, busy, updates, p95, process.WorkingSet64, Trails(), Environment.ProcessorCount));
                        log.Flush();
                        Console.WriteLine("Measured {0}: process CPU {1:F3} ms/s, system busy {2:F2}%, replay updates {3}, tick p95 {4:F2} ms", mode, cpu / elapsed, busy, updates, p95);
                        finishWorker();
                        trial++;
                        if (trial == modes.Length * rounds) { timer.Stop(); form.Close(); }
                        else startTrial();
                    }
                    catch (Exception e) { failure = e; timer.Stop(); form.Close(); }
                };
                Application.Run(form);
                finishWorker();
                if (failure != null) throw failure;
                if (trial != modes.Length * rounds) throw new Exception("Comparison cancelled; partial CSV retained.");
            }
            Apply(original);
            Check(Equal(originalPixels, Pixels(LoadCursor(IntPtr.Zero, (IntPtr)Arrow))), "Benchmark original cursor verification");
            Check(Trails() == trails, "Benchmark original trail verification");
            Console.WriteLine("PASS: comparison finished; original cursor verified; trails unchanged. Results: " + report);
            Console.WriteLine("Report: " + report);
            return 0;
        }
        finally
        {
            Stop.Set();
            if (worker != null) worker.Join();
            try { if (original != IntPtr.Zero) Apply(original); }
            finally
            {
                SetCursorPos(beforePoint.X, beforePoint.Y);
                if (native != IntPtr.Zero) DestroyCursor(native);
                if (inverse != IntPtr.Zero) DestroyCursor(inverse);
                if (original != IntPtr.Zero) DestroyCursor(original);
                Stop.Reset();
            }
        }
    }
}
