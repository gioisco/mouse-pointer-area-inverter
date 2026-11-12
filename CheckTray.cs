using System;
using System.Drawing;
using System.Windows.Forms;

internal static class CheckTray
{
    [STAThread]
    static int Main() { return CursorTrial.CheckTrayLifecycle(); }
}

internal static partial class CursorTrial
{
    internal static int CheckTrayLifecycle()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        System.Drawing.Point originalPosition = Cursor.Position;
        System.Drawing.Point target = originalPosition;
        uint trails = Trails();
        IntPtr inverse = IntPtr.Zero;
        Exception failure = null;
        string[] logsBefore = System.IO.Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "adaptive-*.csv");
        try
        {
            using (CursorSet originals = new CursorSet())
            using (Form form = new Form { Text = "Tray lifecycle check - do not move mouse", BackColor = Color.White,
                StartPosition = FormStartPosition.CenterScreen, Size = new Size(520, 320), TopMost = true, Cursor = Cursors.Arrow })
            {
                inverse = Inverted(LoadCursor(IntPtr.Zero, (IntPtr)Arrow));
                byte[] expected = Pixels(inverse);
                form.Show(); form.Refresh();
                target = form.PointToScreen(new System.Drawing.Point(250, 130));
                Cursor.Position = target;
                using (TrayContext context = new TrayContext())
                using (System.Windows.Forms.Timer check = new System.Windows.Forms.Timer { Interval = 1000 })
                {
                    int phase = 0;
                    check.Tick += delegate {
                        try
                        {
                            if (Cursor.Position != target) throw new Exception("Unexpected mouse input; lifecycle check invalidated.");
                            if (phase == 0 || phase == 3)
                            {
                                if (!context.Active || !Equal(expected, Pixels(LoadCursor(IntPtr.Zero, (IntPtr)Arrow))))
                                    throw new Exception("Active tray application did not invert the arrow on white.");
                            }
                            if (phase == 0) context.toggle.PerformClick();
                            if (phase == 1 || phase == 2)
                            {
                                if (context.Active) throw new Exception("Pause did not stop the application.");
                                originals.RestoreAndVerify();
                            }
                            if (phase == 2) context.toggle.PerformClick();
                            if (phase == 3)
                            {
                                ((ToolStripMenuItem)context.menu.Items[2]).PerformClick();
                                originals.RestoreAndVerify();
                                if (context.Active) throw new Exception("Exit did not stop the application.");
                                check.Stop();
                            }
                            phase++;
                        }
                        catch (Exception e) { failure = e; check.Stop(); context.ExitThread(); }
                    };
                    check.Start();
                    try { Application.Run(context); }
                    finally { originals.RestoreAndVerify(true); }
                    if (failure != null) throw failure;
                    if (phase != 4) throw new Exception("Lifecycle check ended early.");
                }
            }
            if (Trails() != trails) throw new Exception("Trail setting changed.");
            if (System.IO.Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "adaptive-*.csv").Length != logsBefore.Length)
                throw new Exception("Normal application generated diagnostic CSVs.");
            Console.WriteLine("PASS: real tray timer inversion, menu pause/resume/exit, exact restoration, unchanged trails, no diagnostic CSVs.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            if (inverse != IntPtr.Zero) DestroyCursor(inverse);
            if (Cursor.Position == target) Cursor.Position = originalPosition;
        }
    }
}
