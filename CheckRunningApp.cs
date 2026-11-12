using System;
using System.Drawing;
using System.Windows.Forms;

internal static class CheckRunningApp
{
    [STAThread]
    static int Main(string[] args) { return args.Length == 1 && args[0] == "--observe" ? CursorTrial.ObserveRunningCursor() : CursorTrial.CheckRunningApplication(); }
}

internal static partial class CursorTrial
{
    internal static int ObserveRunningCursor()
    {
        SetProcessDPIAware();
        uint[] ids = { Arrow, 32513, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649 };
        string[] names = { "arrow", "text", "cross", "up", "resize-nwse", "resize-nesw", "resize-ew", "resize-ns", "move", "unavailable", "link" };
        for (int tick = 0; tick < 12; tick++)
        {
            CursorInfo info = new CursorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(CursorInfo)) };
            if (!GetCursorInfo(ref info)) { Console.WriteLine("Input desktop unavailable"); continue; }
            string shape = "application-owned/unsupported";
            for (int i = 0; i < ids.Length; i++) if (info.Cursor == LoadCursor(IntPtr.Zero, (IntPtr)ids[i])) { shape = names[i]; break; }
            if (info.Cursor != IntPtr.Zero)
            {
                using (AreaSampler sampler = new AreaSampler(info.Cursor))
                {
                    int bright, count; sampler.Sample(info.Position, out bright, out count);
                    Console.WriteLine("{0}: shape={1}; handle={2}; flags={3}; light={4:F1}%", tick, shape, info.Cursor, info.Flags, bright * 100.0 / count);
                }
            }
            System.Threading.Thread.Sleep(500);
        }
        return 0;
    }
    internal static int CheckRunningApplication()
    {
        SetProcessDPIAware();
        System.Drawing.Point before = Cursor.Position, target = before;
        byte[] light = null, dark = null;
        Exception failure = null;
        try
        {
            using (Form form = new Form { Text = "Checking the RUNNING packaged app - leave mouse still", BackColor = Color.White,
                StartPosition = FormStartPosition.CenterScreen, Size = new Size(500, 300), TopMost = true, Cursor = Cursors.Arrow })
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 })
            {
                int phase = 0;
                form.Shown += delegate { target = form.PointToScreen(new System.Drawing.Point(250, 150)); Cursor.Position = target; timer.Start(); };
                timer.Tick += delegate {
                    try
                    {
                        if (Cursor.Position != target) throw new Exception("Unexpected mouse input.");
                        CursorInfo info = new CursorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(CursorInfo)) };
                        Check(GetCursorInfo(ref info), "GetCursorInfo");
                        IntPtr arrow = LoadCursor(IntPtr.Zero, (IntPtr)Arrow);
                        Console.WriteLine("Background {0}; visible handle {1}; system arrow handle {2}", phase == 0 ? "white" : "dark", info.Cursor, arrow);
                        if (info.Flags != 1 || info.Cursor != arrow) throw new Exception("The controlled surface is not displaying the standard arrow.");
                        if (phase++ == 0) { light = Pixels(arrow); form.BackColor = Color.FromArgb(30, 30, 30); form.Refresh(); }
                        else { dark = Pixels(arrow); timer.Stop(); form.Close(); }
                    }
                    catch (Exception e) { failure = e; timer.Stop(); form.Close(); }
                };
                Application.Run(form);
            }
            if (failure != null) throw failure;
            if (light == null || dark == null || Equal(light, dark)) throw new Exception("FAIL: the running packaged app did not change the system arrow between white and dark.");
            Console.WriteLine("PASS: the running packaged app changes the system arrow on white/dark.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (Cursor.Position == target) Cursor.Position = before; }
    }
}
