using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class CheckShapes
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr id);
    [STAThread]
    static int Main()
    {
        SetProcessDPIAware();
        uint[] ids = { 32512, 32513, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649 };
        string[] names = { "arrow", "text", "cross", "up", "resize-nwse", "resize-nesw", "resize-ew", "resize-ns", "move", "unavailable", "link" };
        Cursor[] cursors = new Cursor[ids.Length];
        Process child = null;
        Point before = Cursor.Position, target = before;
        StringBuilder captured = new StringBuilder();
        string errors = "";
        Exception failure = null;
        List<long> paintTimes = new List<long>();
        try
        {
            for (int i = 0; i < ids.Length; i++)
            {
                IntPtr handle = LoadCursor(IntPtr.Zero, (IntPtr)ids[i]);
                if (handle == IntPtr.Zero) throw new Exception("Missing system cursor " + names[i]);
                cursors[i] = new Cursor(handle);
            }
            using (Form form = new Form())
            using (Timer timer = new Timer())
            {
                form.ClientSize = new Size(500, 260);
                form.StartPosition = FormStartPosition.CenterScreen;
                form.BackColor = Color.White; form.TopMost = true; form.Cursor = cursors[0];
                form.Text = "Standard cursor check - 22 seconds";
                Stopwatch clock = new Stopwatch();
                int stage = 0;
                form.Shown += delegate {
                    try
                    {
                        target = form.PointToScreen(new Point(250, 130));
                        Cursor.Position = target; form.Activate(); form.Refresh();
                        paintTimes.Add(Stopwatch.GetTimestamp());
                        child = new Process { StartInfo = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CursorTrial.exe"), "--adaptive-all 22") {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                        }};
                        child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                            if (e.Data == null) return;
                            lock (captured) captured.AppendLine(e.Data);
                            if (e.Data.StartsWith("Log: "))
                            {
                                try { form.BeginInvoke(new Action(delegate { clock.Restart(); })); }
                                catch (InvalidOperationException) { }
                            }
                        };
                        child.Start(); child.BeginOutputReadLine(); timer.Start();
                    }
                    catch (Exception e) { failure = e; form.Close(); }
                };
                timer.Interval = 50;
                timer.Tick += delegate {
                    int next = Math.Min(ids.Length * 2 - 1, (int)(clock.ElapsedMilliseconds / 1000));
                    if (next != stage)
                    {
                        stage = next;
                        form.Cursor = cursors[stage / 2];
                        form.BackColor = stage % 2 == 0 ? Color.White : Color.FromArgb(30, 30, 30);
                        form.Text = names[stage / 2] + (stage % 2 == 0 ? " / light" : " / dark");
                        form.Refresh();
                        paintTimes.Add(Stopwatch.GetTimestamp());
                    }
                    if (child != null && child.HasExited) { timer.Stop(); form.Close(); }
                };
                Application.Run(form);
            }
            if (child != null) { errors = child.StandardError.ReadToEnd(); child.WaitForExit(); }
            if (failure != null) throw failure;
            string output;
            lock (captured) output = captured.ToString();
            Console.Write(output);
            if (child == null || child.ExitCode != 0) throw new Exception("All-shape child failed: " + errors);
            string report = null;
            foreach (string line in output.Split('\n')) if (line.StartsWith("Log: ")) report = line.Substring(5).Trim();
            if (report == null) throw new Exception("Missing all-shape report.");
            int[] counts = new int[ids.Length * 2];
            List<double> latencies = new List<double>();
            foreach (string line in File.ReadAllLines(report))
            {
                if (line.StartsWith("# switch_qpc="))
                {
                    string[] metadata = line.Substring(2).Split(';');
                    long timestamp = long.Parse(metadata[0].Substring("switch_qpc=".Length), CultureInfo.InvariantCulture);
                    int phase = paintTimes.Count - 1;
                    while (phase >= 0 && paintTimes[phase] > timestamp) phase--;
                    if (phase > 0 && metadata[1] == "inverse=" + (phase % 2 == 0 ? "1" : "0") && metadata[2] == "shape=" + names[phase / 2])
                        latencies.Add((timestamp - paintTimes[phase]) * 1000.0 / Stopwatch.Frequency);
                    continue;
                }
                string[] fields = line.Split(',');
                if (fields.Length != 7 || fields[1] != "sample") continue;
                double elapsed = double.Parse(fields[0], CultureInfo.InvariantCulture);
                int stage = (int)(elapsed / 1000);
                double within = elapsed % 1000;
                if (stage >= counts.Length || within < 400 || within > 850) continue;
                if (fields[6] != names[stage / 2] || fields[3] != (stage % 2 == 0 ? "1" : "0"))
                    throw new Exception("Unexpected cursor/state in stage " + stage + ": " + line);
                double light = double.Parse(fields[2], CultureInfo.InvariantCulture);
                if (stage % 2 == 0 ? light < .99 : light > .01)
                    throw new Exception("Background capture is affected by cursor pixels: " + line);
                counts[stage]++;
            }
            for (int stage = 0; stage < counts.Length; stage++)
                if (counts[stage] < 3) throw new Exception("Insufficient samples for " + names[stage / 2] + " stage " + stage);
            if (!output.Contains("All standard cursor backups restored and verified. Trail setting unchanged."))
                throw new Exception("All-shape restoration was not verified.");
            if (latencies.Count > 0)
            {
                latencies.Sort();
                Console.WriteLine("Paint-to-SetSystemCursor acknowledgement (not physical display latency): p95 {0:F2} ms, max {1:F2} ms, samples {2}",
                    latencies[(int)Math.Ceiling(latencies.Count * .95) - 1], latencies[latencies.Count - 1], latencies.Count);
            }
            Console.WriteLine("PASS: 11 standard shapes on light/dark backgrounds, cursor-independent capture, all backups restored.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally
        {
            if (child != null) child.Dispose();
            if (Cursor.Position == target) Cursor.Position = before;
            foreach (Cursor cursor in cursors) if (cursor != null) cursor.Dispose();
        }
    }
}
