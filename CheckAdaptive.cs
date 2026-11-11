using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Deterministic interactive smoke test; it does not replace the performance benchmark.
internal static class CheckAdaptive
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread]
    static int Main()
    {
        SetProcessDPIAware();
        Point previous = Cursor.Position, target = previous;
        Process child = null;
        Exception failure = null;
        string output = "", errors = "";
        try
        {
            using (Form form = new Form())
            using (Timer timer = new Timer())
            using (Bitmap shape = Icon.FromHandle(Cursors.Arrow.Handle).ToBitmap())
            {
                form.Text = "Area inversion check - 8 seconds";
                form.ClientSize = new Size(500, 260);
                form.StartPosition = FormStartPosition.CenterScreen;
                form.BackColor = Color.White;
                form.TopMost = true;
                form.Cursor = Cursors.Arrow;
                int currentStage = 0;
                Point hotSpot = Cursors.Arrow.HotSpot;
                form.Paint += delegate(object sender, PaintEventArgs e) {
                    if (currentStage < 2) return;
                    // Opposite-color surroundings catch accidental sampling outside the arrow shape.
                    Brush ink = currentStage == 2 ? Brushes.White : Brushes.Black;
                    for (int y = 0; y < shape.Height; y++)
                    for (int x = 0; x < shape.Width; x++)
                        if (shape.GetPixel(x, y).A > 0)
                            e.Graphics.FillRectangle(ink, 250 - hotSpot.X + x, 130 - hotSpot.Y + y, 1, 1);
                };
                Stopwatch clock = new Stopwatch();
                form.Shown += delegate {
                    try
                    {
                        target = form.PointToScreen(new Point(250, 130));
                        Cursor.Position = target;
                        form.Activate(); form.Refresh();
                        child = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CursorTrial.exe"), "--adaptive 8") {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                        });
                        clock.Start(); timer.Start();
                    }
                    catch (Exception e) { failure = e; form.Close(); }
                };
                timer.Interval = 100;
                timer.Tick += delegate {
                    int stage = Math.Min(3, (int)(clock.ElapsedMilliseconds / 2000));
                    if (stage != currentStage)
                    {
                        currentStage = stage;
                        form.BackColor = stage == 1 || stage == 2 ? Color.FromArgb(30, 30, 30) : Color.White;
                        form.Invalidate(); form.Update();
                    }
                    if (child != null && child.HasExited)
                    {
                        timer.Stop();
                        output = child.StandardOutput.ReadToEnd();
                        errors = child.StandardError.ReadToEnd();
                        form.Close();
                    }
                };
                Application.Run(form);
            }
            // If the window was closed early, let the finite child finish and restore normally.
            if (child != null && !child.HasExited)
            {
                output = child.StandardOutput.ReadToEnd();
                errors = child.StandardError.ReadToEnd();
                child.WaitForExit();
            }
            if (failure != null) throw failure;
            if (child == null || child.ExitCode != 0) throw new Exception("Adaptive child failed: " + errors);
            Console.Write(output);
            string report = null;
            foreach (string line in output.Split('\n')) if (line.StartsWith("Log: ")) report = line.Substring(5).Trim();
            if (report == null) throw new Exception("Missing adaptive report.");
            int[] counts = new int[4];
            foreach (string line in File.ReadAllLines(report))
            {
                string[] fields = line.Split(',');
                if (fields.Length != 6 || fields[1] != "sample") continue;
                double elapsed = double.Parse(fields[0], CultureInfo.InvariantCulture);
                for (int stage = 0; stage < 4; stage++)
                {
                    if (elapsed < stage * 2000 + 1000 || elapsed > stage * 2000 + 1700) continue;
                    if (fields[3] != (stage % 2 == 0 ? "1" : "0"))
                        throw new Exception("Unexpected inversion in stage " + stage);
                    double fraction = double.Parse(fields[2], CultureInfo.InvariantCulture);
                    if ((stage % 2 == 0 && fraction < .99) || (stage % 2 != 0 && fraction > .01))
                        throw new Exception("Capture includes the cursor or pixels outside its shape, stage " + stage);
                    counts[stage]++;
                }
            }
            for (int stage = 0; stage < 4; stage++)
                if (counts[stage] < 3) throw new Exception("Insufficient samples in stage " + stage + "; keep mouse inside the test window.");
            if (!output.Contains("Original cursor restored and pixels/hotspot verified.")) throw new Exception("Restoration was not verified.");
            Console.WriteLine("PASS: light/dark surfaces, exact arrow footprint with opposite surroundings, capture independent of cursor image, exact restoration.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally
        {
            if (child != null) child.Dispose();
            if (Cursor.Position == target) Cursor.Position = previous;
        }
    }
}
