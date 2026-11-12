using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class PerformanceGate
{
    [STAThread] static int Main(string[] args) { return CursorTrial.MeasureRuntime(args); }
}

internal static partial class CursorTrial
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    sealed class DwmCpuReader : IDisposable
    {
        sealed class Counter
        {
            public int Pid;
            public PerformanceCounter Id, Cpu;
        }
        readonly List<Counter> counters = new List<Counter>();
        public string Status = "available";
        public DwmCpuReader(Process[] compositors)
        {
            try
            {
                HashSet<int> ids = new HashSet<int>();
                foreach (Process process in compositors) ids.Add(process.Id);
                foreach (string instance in new PerformanceCounterCategory("Process").GetInstanceNames())
                {
                    if (!instance.StartsWith("dwm", StringComparison.OrdinalIgnoreCase)) continue;
                    PerformanceCounter id = new PerformanceCounter("Process", "ID Process", instance, true);
                    int pid = (int)id.RawValue;
                    if (!ids.Contains(pid)) { id.Dispose(); continue; }
                    PerformanceCounter cpu = null;
                    try
                    {
                        cpu = new PerformanceCounter("Process", "% Processor Time", instance, true);
                        if (cpu.CounterType != PerformanceCounterType.Timer100Ns) throw new Exception("Unexpected compositor CPU counter type.");
                        counters.Add(new Counter { Pid = pid, Id = id, Cpu = cpu });
                    }
                    catch { id.Dispose(); if (cpu != null) cpu.Dispose(); throw; }
                }
                if (counters.Count != ids.Count || counters.Count == 0) Status = "incomplete";
            }
            catch (Exception e) { Status = "unavailable: " + e.GetType().Name; }
        }
        public double ReadMilliseconds()
        {
            if (Status != "available") return double.NaN;
            double sum = 0;
            foreach (Counter counter in counters)
            {
                if (counter.Id.RawValue != counter.Pid) throw new Exception("Compositor process changed during measurement.");
                sum += counter.Cpu.RawValue / 10000.0;
            }
            return sum;
        }
        public void Dispose() { foreach (Counter counter in counters) { counter.Id.Dispose(); counter.Cpu.Dispose(); } counters.Clear(); }
    }
    sealed class GpuMeter : IDisposable
    {
        sealed class Engine
        {
            public string Name, Scope;
            public PerformanceCounter Counter;
        }
        readonly List<Engine> engines = new List<Engine>();
        readonly int childId;
        public int DwmCount, AppCount, ReadErrors;
        public double DwmTotal, AppTotal;
        public int DwmSamples, AppSamples;
        public string Status = "available";
        public GpuMeter(int applicationId, Process[] compositors)
        {
            childId = applicationId;
            try
            {
                HashSet<int> dwm = new HashSet<int>();
                foreach (Process process in compositors) dwm.Add(process.Id);
                PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
                foreach (string name in category.GetInstanceNames())
                {
                    int end = name.IndexOf('_', 4), pid;
                    if (!name.StartsWith("pid_") || end < 0 || !int.TryParse(name.Substring(4, end - 4), out pid)) continue;
                    string scope = pid == childId ? "application" : dwm.Contains(pid) ? "dwm" : null;
                    if (scope == null) continue;
                    PerformanceCounter counter = null;
                    try
                    {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                        counter.NextValue();
                        engines.Add(new Engine { Name = name, Scope = scope, Counter = counter });
                        if (scope == "dwm") DwmCount++; else AppCount++;
                    }
                    catch { if (counter != null) counter.Dispose(); ReadErrors++; }
                }
            }
            catch (Exception e) { Status = "unavailable: " + e.GetType().Name; }
        }
        public void Sample(StreamWriter writer, int round, string mode, string scenario, double elapsed)
        {
            double dwm = double.NaN, app = childId == 0 ? 0 : double.NaN;
            string dwmName = "no-engine-counter-reported", appName = childId == 0 ? "process-not-running" : "no-engine-counter-reported";
            foreach (Engine engine in engines)
            {
                try
                {
                    double value = engine.Counter.NextValue();
                    if (double.IsNaN(value) || value < 0) { ReadErrors++; continue; }
                    if (engine.Scope == "dwm" && (double.IsNaN(dwm) || value > dwm)) { dwm = value; dwmName = engine.Name; }
                    if (engine.Scope == "application" && (double.IsNaN(app) || value > app)) { app = value; appName = engine.Name; }
                }
                catch { ReadErrors++; }
            }
            if (!double.IsNaN(dwm)) { DwmTotal += dwm; DwmSamples++; }
            if (!double.IsNaN(app)) { AppTotal += app; AppSamples++; }
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},dwm,{4},{5:F6},{6}", round, mode, scenario, elapsed, DwmCount, dwm, dwmName));
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},application,{4},{5:F6},{6}", round, mode, scenario, elapsed, AppCount, app, appName));
        }
        public void Dispose() { foreach (Engine engine in engines) engine.Counter.Dispose(); engines.Clear(); }
    }

    static double Cpu(Process process)
    {
        try { return process.TotalProcessorTime.TotalMilliseconds; }
        catch { return double.NaN; }
    }
    internal static int MeasureRuntime(string[] args)
    {
        int seconds = 20, rounds = 3;
        if (args.Length > 2 || (args.Length > 0 && (!int.TryParse(args[0], out seconds) || seconds < 8 || seconds > 60)) ||
            (args.Length > 1 && (!int.TryParse(args[1], out rounds) || rounds < 1 || rounds > 4)))
        { Console.Error.WriteLine("Usage: PerformanceGate.exe [seconds-per-condition:8-60] [rounds:1-4]"); return 1; }
        using (Mutex gate = new Mutex(false, "Local\\MousePointerAreaInverterPerformanceGate"))
        using (Mutex trial = new Mutex(false, "Local\\MousePointerAreaInverterTrial"))
        {
            bool owned;
            try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) { Console.Error.WriteLine("A performance comparison is already running."); return 1; }
            try
            {
                bool idle;
                try { idle = trial.WaitOne(0); } catch (AbandonedMutexException) { idle = true; }
                if (!idle) throw new Exception("Stop the running adaptive application before measuring.");
                trial.ReleaseMutex();
                return MeasureRuntimeCore(seconds, rounds);
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            finally { gate.ReleaseMutex(); }
        }
    }

    static int MeasureRuntimeCore(int seconds, int rounds)
    {
        SetProcessDPIAware(); SelfTest(); AreaSelfTest();
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string report = Path.Combine(root, "runtime-gate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
        string gpuReport = Path.ChangeExtension(report, ".gpu.csv");
        IntPtr native = IntPtr.Zero, original = Copy(LoadCursor(IntPtr.Zero, (IntPtr)Arrow));
        Point originalPosition;
        Check(GetCursorPos(out originalPosition), "GetCursorPos runtime comparison");
        uint trails = Trails();
        Process child = null;
        GpuMeter gpu = null;
        Exception failure = null;
        Process[] compositors = Process.GetProcessesByName("dwm");
        using (DwmCpuReader dwmCpuReader = new DwmCpuReader(compositors))
        using (CursorSet backups = new CursorSet())
        {
        try
        {
            IconInfo icon;
            Check(GetIconInfo(original, out icon), "Arrow metadata");
            try
            {
                BitmapInfo dimensions;
                Check(GetObject(icon.Color == IntPtr.Zero ? icon.Mask : icon.Color, System.Runtime.InteropServices.Marshal.SizeOf(typeof(BitmapInfo)), out dimensions) != 0, "Arrow dimensions");
                int height = Math.Abs(dimensions.Height) / (icon.Color == IntPtr.Zero ? 2 : 1);
                native = LoadImage(IntPtr.Zero, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "arrow_i.cur"), 2, dimensions.Width, height, 0x0010);
                Check(native != IntPtr.Zero, "Load native inverted cursor");
            }
            finally { if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); DeleteObject(icon.Mask); }
            using (Form form = new Form())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            using (Process host = Process.GetCurrentProcess())
            using (StreamWriter log = new StreamWriter(report))
            using (StreamWriter gpuLog = new StreamWriter(gpuReport))
            using (ManualResetEvent readySignal = new ManualResetEvent(false))
            {
                string[] modes = { "normal", "native-inverted", "adaptive" };
                int index = 0, replayUpdates = 0;
                string mode = "", scenario = "";
                System.Drawing.Point stationaryPosition = System.Drawing.Point.Empty;
                bool ready = false, measuring = false;
                double appStart = 0, hostStart = 0, dwmStart = 0, nextGpu = 1;
                ulong idle0 = 0, kernel0 = 0, user0 = 0;
                Stopwatch phase = new Stopwatch(), measurement = new Stopwatch();
                StringBuilder childOutput = new StringBuilder();
                long lastTick = 0;
                List<double> intervals = new List<double>();
                form.ClientSize = new Size(600, 300); form.StartPosition = FormStartPosition.CenterScreen;
                form.TopMost = true; form.KeyPreview = true; form.Cursor = Cursors.Arrow;
                form.Paint += delegate(object sender, PaintEventArgs e) {
                    e.Graphics.Clear(Color.White); e.Graphics.FillRectangle(Brushes.DimGray, 300, 0, 300, 300);
                    for (int row = 0; row < 6; row++)
                    {
                        e.Graphics.DrawString("Text on a light background", SystemFonts.DefaultFont, Brushes.Black, 20, 30 + row * 40);
                        e.Graphics.DrawString("Text on a dark background", SystemFonts.DefaultFont, Brushes.White, 320, 30 + row * 40);
                    }
                };
                log.WriteLine("round,scenario,mode,seconds,application_cpu_ms_per_s,replay_cpu_ms_per_s,dwm_cpu_ms_per_s,system_busy_percent,application_gpu_busiest_engine_mean_percent,dwm_gpu_busiest_engine_mean_percent,application_gpu_counters,dwm_gpu_counters,gpu_errors,gpu_status,replay_updates,replay_tick_p95_ms,application_working_set_bytes,application_private_bytes,application_samples,application_switches,trails,logical_processors,dwm_cpu_status");
                gpuLog.WriteLine("round,mode,scenario,seconds,scope,counter_count,busiest_engine_percent,busiest_engine_instance");
                Action finishChild = delegate {
                    if (child == null) return;
                    if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); throw new Exception("Adaptive process exceeded its finite trial; backups will be restored."); }
                    child.WaitForExit();
                    string errors = child.StandardError.ReadToEnd();
                    if (child.ExitCode != 0) throw new Exception("Adaptive process failed: " + errors);
                    string output;
                    lock (childOutput) output = childOutput.ToString();
                    if (!output.Contains("All standard cursor backups restored and verified.")) throw new Exception("Adaptive restoration was not verified.");
                    child.Dispose(); child = null;
                };
                Action start = delegate {
                    int round = index / 6, within = index % 6;
                    scenario = within / 3 == 0 ? "stationary" : "moving";
                    mode = modes[(within % 3 + round) % 3];
                    backups.RestoreAndVerify(true);
                    Apply(mode == "native-inverted" ? native : original);
                    form.Text = string.Format("{0}/{1}: {2}, {3} - Esc to stop", round + 1, rounds, scenario, mode);
                    Point point = scenario == "stationary" ? new Point { X = 70, Y = 265 } : PathPoint(0, 600, 300);
                    System.Drawing.Point location = form.PointToScreen(new System.Drawing.Point(point.X, point.Y));
                    stationaryPosition = location;
                    Check(SetCursorPos(location.X, location.Y), "Initial replay position");
                    intervals.Clear(); lastTick = 0; replayUpdates = 0; measuring = false; ready = mode != "adaptive";
                    readySignal.Reset(); lock (childOutput) childOutput.Clear();
                    phase.Restart();
                    if (mode == "adaptive")
                    {
                        child = new Process { StartInfo = new ProcessStartInfo(Path.Combine(root, "CursorTrial.exe"), "--adaptive-all " + (seconds + 5)) {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
                        }};
                        child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                            if (e.Data == null) return;
                            lock (childOutput) childOutput.AppendLine(e.Data);
                            if (e.Data.StartsWith("Log: "))
                            {
                                try { readySignal.Set(); } catch (ObjectDisposedException) { }
                            }
                            if (e.Data.Contains("s: ")) Console.WriteLine("[actual adaptive process] " + e.Data);
                        };
                        child.Start(); child.BeginOutputReadLine();
                    }
                    Console.WriteLine("Round {0}/{1}, {2}, {3}: real adaptation {4}; 3s warm-up + {5}s measured", round + 1, rounds, scenario, mode, mode == "adaptive" ? "ON" : "OFF", seconds);
                };
                form.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) form.Close(); };
                form.Shown += delegate {
                    try { form.Activate(); form.Refresh(); start(); timer.Start(); }
                    catch (Exception e) { failure = e; form.Close(); }
                };
                timer.Interval = 16;
                timer.Tick += delegate {
                    try
                    {
                        if (!ready)
                        {
                            if (readySignal.WaitOne(0)) { ready = true; phase.Restart(); }
                            else { if (phase.Elapsed.TotalSeconds > 10 || child.HasExited) throw new Exception("Adaptive startup failed or timed out."); return; }
                        }
                        if (!measuring && scenario == "stationary")
                        {
                            // Setup/input settling is excluded; enforce immobility only during measurement.
                            Point initial;
                            Check(GetCursorPos(out initial), "Warm-up cursor position");
                            if (initial.X != stationaryPosition.X || initial.Y != stationaryPosition.Y)
                                Check(SetCursorPos(stationaryPosition.X, stationaryPosition.Y), "Warm-up recenter");
                        }
                        if (!measuring && phase.Elapsed.TotalSeconds >= 3)
                        {
                            Check(Trails() == trails, "Runtime comparison trail setting changed");
                            gpu = new GpuMeter(child == null ? 0 : child.Id, compositors);
                            if (scenario == "stationary") Check(SetCursorPos(stationaryPosition.X, stationaryPosition.Y), "Stationary measurement start");
                            appStart = child == null ? 0 : Cpu(child); hostStart = Cpu(host); dwmStart = dwmCpuReader.ReadMilliseconds();
                            Check(GetSystemTimes(out idle0, out kernel0, out user0), "System CPU start");
                            nextGpu = 1; measurement.Restart(); measuring = true;
                        }
                        if (scenario == "moving")
                        {
                            Point point = PathPoint(measuring ? measurement.Elapsed.TotalSeconds : 0, 600, 300);
                            System.Drawing.Point location = form.PointToScreen(new System.Drawing.Point(point.X, point.Y));
                            Check(SetCursorPos(location.X, location.Y), "Moving replay position");
                        }
                        else
                        {
                            Point actual;
                            Check(GetCursorPos(out actual), "Stationary cursor position");
                            if (actual.X != stationaryPosition.X || actual.Y != stationaryPosition.Y)
                                throw new Exception(string.Format(CultureInfo.InvariantCulture, "Mouse input interfered at {0:F3}s: expected {1},{2}, actual {3},{4}. Measurements invalidated.", measurement.Elapsed.TotalSeconds, stationaryPosition.X, stationaryPosition.Y, actual.X, actual.Y));
                        }
                        Point current;
                        Check(GetCursorPos(out current), "Fixture cursor position");
                        if (WindowFromPoint(current) != form.Handle)
                            throw new Exception("The cursor is outside the controlled test window; comparison invalidated.");
                        if (!measuring) return;
                        long tick = Stopwatch.GetTimestamp();
                        if (lastTick != 0) intervals.Add((tick - lastTick) * 1000.0 / Stopwatch.Frequency);
                        lastTick = tick; replayUpdates++;
                        if (measurement.Elapsed.TotalSeconds >= nextGpu)
                        {
                            gpu.Sample(gpuLog, index / 6 + 1, mode, scenario, measurement.Elapsed.TotalSeconds);
                            nextGpu = measurement.Elapsed.TotalSeconds + 1;
                        }
                        if (measurement.Elapsed.TotalSeconds < seconds) return;
                        double elapsed = measurement.Elapsed.TotalSeconds;
                        double appCpu = child == null ? 0 : (Cpu(child) - appStart) / elapsed;
                        double hostCpu = (Cpu(host) - hostStart) / elapsed, dwmCpu = (dwmCpuReader.ReadMilliseconds() - dwmStart) / elapsed;
                        ulong idle1, kernel1, user1;
                        Check(GetSystemTimes(out idle1, out kernel1, out user1), "System CPU end");
                        double total = (kernel1 - kernel0) + (user1 - user0);
                        double busy = total > 0 ? 100 * (1 - (idle1 - idle0) / total) : 0;
                        long memory = 0, privateBytes = 0;
                        if (child != null) { child.Refresh(); memory = child.WorkingSet64; privateBytes = child.PrivateMemorySize64; }
                        finishChild();
                        int samples = 0, switches = 0;
                        if (mode == "adaptive")
                        {
                            string output;
                            lock (childOutput) output = childOutput.ToString();
                            var match = System.Text.RegularExpressions.Regex.Match(output, @"samples (\d+); switches (\d+)");
                            if (!match.Success) throw new Exception("Adaptive sample counts missing.");
                            samples = int.Parse(match.Groups[1].Value); switches = int.Parse(match.Groups[2].Value);
                            if (switches < (scenario == "moving" ? 2 : 1)) throw new Exception("The adaptive process did not actually invert the cursor.");
                            string trace = null;
                            foreach (string line in output.Split('\n')) if (line.StartsWith("Log: ")) trace = line.Substring(5).Trim();
                            if (trace == null) throw new Exception("Adaptive trace missing.");
                            int controlledSamples = 0;
                            foreach (string line in File.ReadAllLines(trace))
                            {
                                string[] fields = line.Split(',');
                                if (fields.Length != 7 || fields[1] != "sample") continue;
                                double time = double.Parse(fields[0], CultureInfo.InvariantCulture);
                                if (time < 5000 || time > (seconds + 4) * 1000) continue;
                                if (fields[6] != "arrow") throw new Exception("The controlled cursor changed; comparison invalidated.");
                                if (scenario == "stationary" && (fields[3] != "1" || double.Parse(fields[2], CultureInfo.InvariantCulture) < .99))
                                    throw new Exception("The stationary light surface changed; comparison invalidated.");
                                controlledSamples++;
                            }
                            if (controlledSamples < (seconds - 1) * 18) throw new Exception("Too many missing controlled adaptive samples; comparison invalidated.");
                        }
                        intervals.Sort();
                        double p95 = intervals.Count == 0 ? 0 : intervals[PercentileIndex(intervals.Count)];
                        double appGpu = gpu.AppSamples == 0 ? double.NaN : gpu.AppTotal / gpu.AppSamples;
                        double dwmGpu = gpu.DwmSamples == 0 ? double.NaN : gpu.DwmTotal / gpu.DwmSamples;
                        log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F6},{9:F6},{10},{11},{12},{13},{14},{15:F3},{16},{17},{18},{19},{20},{21},{22}",
                            index / 6 + 1, scenario, mode, elapsed, appCpu, hostCpu, dwmCpu, busy, appGpu, dwmGpu, gpu.AppCount, gpu.DwmCount, gpu.ReadErrors, gpu.Status,
                            replayUpdates, p95, memory, privateBytes, samples, switches, Trails(), Environment.ProcessorCount, dwmCpuReader.Status));
                        log.Flush(); gpuLog.Flush(); gpu.Dispose(); gpu = null;
                        Console.WriteLine("Measured {0}/{1}: application CPU {2:F3} ms/s; compositor CPU {3:F3} ms/s; real switches {4}", scenario, mode, appCpu, dwmCpu, switches);
                        index++;
                        if (index == rounds * 6) { timer.Stop(); form.Close(); } else start();
                    }
                    catch (Exception e) { failure = e; timer.Stop(); form.Close(); }
                };
                Application.Run(form);
                if (failure != null) throw failure;
                if (index != rounds * 6) throw new Exception("Runtime comparison cancelled; partial results retained.");
            }
            backups.RestoreAndVerify(true); Check(Trails() == trails, "Runtime comparison final trails");
            Console.WriteLine("Functional controls passed; performance equivalence is NOT certified by this harness.");
            Console.WriteLine("Report: " + report); Console.WriteLine("GPU report: " + gpuReport);
            return 0;
        }
        finally
        {
            if (child != null)
            {
                try { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                finally { child.Dispose(); }
            }
            if (gpu != null) gpu.Dispose();
            try { backups.RestoreAndVerify(true); }
            finally
            {
                SetCursorPos(originalPosition.X, originalPosition.Y);
                if (native != IntPtr.Zero) DestroyCursor(native);
                DestroyCursor(original);
                foreach (Process compositor in compositors) compositor.Dispose();
            }
        }
        }
    }
}
