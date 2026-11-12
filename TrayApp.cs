using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal static class PointerApp
{
    [STAThread]
    static int Main(string[] args) { return CursorTrial.RunTray(args); }
}

internal static partial class CursorTrial
{
    internal static int RunTray(string[] args)
    {
        bool selfTest = args.Length == 1 && args[0] == "--self-test";
        try
        {
            SelfTest(); AreaSelfTest();
            if (selfTest) return 0;
            if (args.Length != 0) throw new ArgumentException("Avvia senza argomenti.");
            using (Mutex mutex = new Mutex(false, "Local\\MousePointerAreaInverterTrial"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    MessageBox.Show("Il programma o una prova del cursore e gia in esecuzione. Cerca l'icona vicino all'orologio.", "Mouse Pointer Area Inverter");
                    return 1;
                }
                try
                {
                    SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (TrayContext context = new TrayContext()) Application.Run(context);
                    return 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception e)
        {
            if (!selfTest) MessageBox.Show(e.Message, "Mouse Pointer Area Inverter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon icon = new NotifyIcon();
        internal readonly ContextMenuStrip menu = new ContextMenuStrip();
        internal readonly ToolStripMenuItem toggle = new ToolStripMenuItem();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        CursorSet shapes;
        AdaptiveReader reader;
        bool disposed;
        public bool Active { get { return shapes != null; } }

        public TrayContext()
        {
            try
            {
                toggle.Click += delegate { TryToggle(); };
                menu.Items.Add(toggle);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Esci", null, delegate { ExitThread(); });
                icon.ContextMenuStrip = menu;
                icon.DoubleClick += delegate { TryToggle(); };
                timer.Interval = SamplePeriodMilliseconds;
                timer.Tick += delegate {
                    try { AdaptiveSample ignored; reader.Read(false, true, out ignored); }
                    catch (Exception e)
                    {
                        string error = e.Message;
                        try { Pause(); } catch (Exception restore) { error += "\n" + restore.Message; }
                        icon.ShowBalloonTip(10000, "Adattamento in pausa", error, ToolTipIcon.Error);
                    }
                };
                Resume();
                icon.Visible = true;
            }
            catch { Dispose(); throw; }
        }
        void UpdateStatus()
        {
            if (disposed) return;
            toggle.Text = Active ? "Pausa" : "Riprendi";
            icon.Text = "Mouse Pointer Area Inverter - " + (Active ? "Attivo" : "In pausa");
            icon.Icon = Active ? SystemIcons.Application : SystemIcons.Warning;
        }
        public void Resume()
        {
            if (Active) return;
            shapes = new CursorSet();
            reader = new AdaptiveReader(IntPtr.Zero, IntPtr.Zero, shapes);
            UpdateStatus();
            timer.Start();
        }
        public void Pause()
        {
            timer.Stop();
            CursorSet previous = shapes;
            shapes = null;
            try
            {
                if (previous != null)
                {
                    try { previous.RestoreAndVerify(); }
                    finally { previous.Dispose(); }
                }
            }
            finally
            {
                if (reader != null) reader.Dispose();
                reader = null;
                UpdateStatus();
            }
        }
        void TryToggle()
        {
            try { if (Active) Pause(); else Resume(); }
            catch (Exception e) { MessageBox.Show(e.Message, "Mouse Pointer Area Inverter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        protected override void ExitThreadCore()
        {
            try { Pause(); }
            catch (Exception e) { MessageBox.Show("Ripristino non riuscito: " + e.Message + "\nRiseleziona lo schema dei puntatori nelle impostazioni Mouse di Windows.", "Mouse Pointer Area Inverter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { base.ExitThreadCore(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposed) return;
            if (disposing)
            {
                disposed = true;
                try { Pause(); }
                finally
                {
                    timer.Dispose(); icon.Visible = false; icon.Dispose(); menu.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
