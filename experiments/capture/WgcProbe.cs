using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Forms;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

internal static class WgcProbe
{
    [STAThread] static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "wgc";
        int seconds = 6;
        if (args.Length > 2 || (mode != "wgc" && mode != "gdi" && mode != "gdi-monitor" && mode != "gdi-transient" && mode != "baseline") ||
            (args.Length > 1 && (!int.TryParse(args[1], out seconds) || seconds < 4 || seconds > 120)))
        { Console.Error.WriteLine("Usage: WgcProbe.exe [baseline|gdi|gdi-monitor|gdi-transient|wgc] [measured-seconds:4-120]"); return 1; }
        return CaptureProbe.CheckWgc(mode, seconds);
    }
}
internal static partial class CaptureProbe
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface CaptureItemInterop
    {
        [PreserveSig] int ForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int ForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }
    [StructLayout(LayoutKind.Sequential)] struct TextureInfo
    {
        public uint Width, Height, Mips, Array, Format, Samples, Quality, Usage, Bind, CpuAccess, Misc;
    }
    [StructLayout(LayoutKind.Sequential)] struct Region { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NewTexture(IntPtr self, ref TextureInfo info, IntPtr data, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegion(IntPtr self, IntPtr dst, uint sub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Region box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapTexture(IntPtr self, IntPtr texture, uint sub, uint type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapTexture(IntPtr self, IntPtr texture, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SurfaceTexture(IntPtr self, ref Guid iid, out IntPtr texture);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [StructLayout(LayoutKind.Sequential)] struct DibHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref DibHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, IntPtr output, IntPtr init);

    internal static int CheckWgc(string mode, int seconds)
    {
        SetProcessDPIAware();
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, adapter = IntPtr.Zero;
        IntPtr output = IntPtr.Zero, interopDevice = IntPtr.Zero, itemPointer = IntPtr.Zero, staging = IntPtr.Zero;
        IDirect3DDevice graphicsDevice = null;
        Direct3D11CaptureFramePool pool = null;
        GraphicsCaptureSession session = null;
        Direct3D11CaptureFrame currentFrame = null;
        IntPtr frameTexture = IntPtr.Zero;
        IntPtr gdiScreen = IntPtr.Zero, gdiMemory = IntPtr.Zero, gdiBitmap = IntPtr.Zero, gdiPrevious = IntPtr.Zero, gdiBits = IntPtr.Zero;
        Point original; GetCursorPos(out original);
        System.Drawing.Point target = new System.Drawing.Point(original.X, original.Y);
        Exception failure = null;
        int samples = 0;
        using (Process process = Process.GetCurrentProcess())
        using (PerformanceCounter dwmCpuCounter = new PerformanceCounter("Process", "% Processor Time", "dwm", true))
        using (PerformanceCounter dwmIdCounter = new PerformanceCounter("Process", "ID Process", "dwm", true))
        {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new Exception("Graphics Capture is unsupported.");
            uint level;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out level, out context));
            dxgi = Query(device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Method<GetAdapter>(dxgi, 7)(dxgi, out adapter));
            Marshal.ThrowExceptionForHR(Method<EnumOutput>(adapter, 7)(adapter, 0, out output));
            OutputInfo screen;
            Marshal.ThrowExceptionForHR(Method<GetOutputInfo>(output, 7)(output, out screen));
            if (mode == "wgc")
            {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out interopDevice));
            graphicsDevice = (IDirect3DDevice)Marshal.GetObjectForIUnknown(interopDevice);
            Guid itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            CaptureItemInterop factory = (CaptureItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            Marshal.ThrowExceptionForHR(factory.ForMonitor(screen.Monitor, ref itemId, out itemPointer));
            GraphicsCaptureItem item = (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPointer);
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphicsDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.MinUpdateInterval = TimeSpan.FromMilliseconds(50);
            TextureInfo texture = new TextureInfo { Width = 32, Height = 32, Mips = 1, Array = 1, Format = 87, Samples = 1, Usage = 3, CpuAccess = 0x20000 };
            Marshal.ThrowExceptionForHR(Method<NewTexture>(device, 5)(device, ref texture, IntPtr.Zero, out staging));
            }
            if (mode.StartsWith("gdi", StringComparison.Ordinal))
            {
                gdiScreen = mode == "gdi-monitor" ? CreateDC("DISPLAY", screen.Name, IntPtr.Zero, IntPtr.Zero) : GetDC(IntPtr.Zero);
                gdiMemory = CreateCompatibleDC(gdiScreen);
                DibHeader header = new DibHeader { Size = 40, Width = 32, Height = -32, Planes = 1, Bits = 32 };
                gdiBitmap = CreateDIBSection(gdiScreen, ref header, 0, out gdiBits, IntPtr.Zero, 0);
                if (gdiScreen == IntPtr.Zero || gdiMemory == IntPtr.Zero || gdiBitmap == IntPtr.Zero || gdiBits == IntPtr.Zero)
                    throw new Exception("GDI capture allocation failed.");
                gdiPrevious = SelectObject(gdiMemory, gdiBitmap);
                if (gdiPrevious == IntPtr.Zero || gdiPrevious == new IntPtr(-1)) throw new Exception("GDI selection failed.");
                if (mode == "gdi-transient") { ReleaseDC(IntPtr.Zero, gdiScreen); gdiScreen = IntPtr.Zero; }
            }
            CopyRegion copy = Method<CopyRegion>(context, 46);
            MapTexture map = Method<MapTexture>(context, 14);
            UnmapTexture unmap = Method<UnmapTexture>(context, 15);
            int[] counts = new int[2];
            byte[] pixels = new byte[32 * 32 * 4];
            List<double> durations = new List<double>();
            double cpuStart = 0, elapsed = 0, cpu = 0, busy = 0, dwmStart = 0, dwmCpu = 0;
            int dwmPid = (int)dwmIdCounter.RawValue;
            if (dwmCpuCounter.CounterType != PerformanceCounterType.Timer100Ns) throw new Exception("Unexpected compositor CPU counter type.");
            ulong idle0 = 0, kernel0 = 0, user0 = 0;
            Stopwatch measured = new Stopwatch();
            bool measuring = false;
            using (Form form = new Form())
            using (Timer timer = new Timer())
            {
                form.Text = "Capture only (no cursor inversion): " + mode + " - close to stop";
                form.ClientSize = new Size(500, 260); form.TopMost = true; form.Cursor = Cursors.Arrow;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(screen.Left + 100, screen.Top + 100);
                form.BackColor = Color.White;
                Stopwatch clock = new Stopwatch();
                form.Shown += delegate {
                    target = form.PointToScreen(new System.Drawing.Point(250, 130));
                    Cursor.Position = target; form.Activate(); form.Refresh();
                    if (session != null) session.StartCapture(); clock.Start(); timer.Start();
                };
                timer.Interval = 50;
                timer.Tick += delegate {
                    try
                    {
                        double travel = clock.Elapsed.TotalSeconds % 4;
                        double progress = travel <= 2 ? travel / 2 : (4 - travel) / 2;
                        target = form.PointToScreen(new System.Drawing.Point(100 + (int)(300 * progress), 130));
                        Cursor.Position = target;
                        int phase = (int)(clock.ElapsedMilliseconds / 2000) % 2;
                        form.BackColor = phase == 1 ? Color.FromArgb(30, 30, 30) : Color.White;
                        if (!measuring && clock.Elapsed.TotalSeconds >= 2)
                        {
                            if (!GetSystemTimes(out idle0, out kernel0, out user0)) throw new Exception("CPU counters unavailable.");
                            cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                            dwmStart = dwmCpuCounter.RawValue / 10000.0;
                            measured.Start(); measuring = true;
                        }
                        if (measuring && measured.Elapsed.TotalSeconds >= seconds)
                        {
                            elapsed = measured.Elapsed.TotalSeconds;
                            cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                            if ((int)dwmIdCounter.RawValue != dwmPid) throw new Exception("Compositor process changed during probe.");
                            dwmCpu = (dwmCpuCounter.RawValue / 10000.0 - dwmStart) / elapsed;
                            ulong idle1, kernel1, user1;
                            if (!GetSystemTimes(out idle1, out kernel1, out user1)) throw new Exception("CPU counters unavailable.");
                            double total = (kernel1 - kernel0) + (user1 - user0);
                            busy = total > 0 ? 100 * (1 - (idle1 - idle0) / total) : 0;
                            timer.Stop(); form.Close(); return;
                        }
                        if (mode == "baseline") { if (measuring) samples++; return; }
                        long sampleStart = Stopwatch.GetTimestamp();
                        if (mode.StartsWith("gdi", StringComparison.Ordinal))
                        {
                            int sourceX = target.X - (mode == "gdi-monitor" ? screen.Left : 0);
                            int sourceY = target.Y - (mode == "gdi-monitor" ? screen.Top : 0);
                            IntPtr sourceDC = mode == "gdi-transient" ? GetDC(IntPtr.Zero) : gdiScreen;
                            try
                            {
                                if (sourceDC == IntPtr.Zero || !BitBlt(gdiMemory, 0, 0, 32, 32, sourceDC, sourceX, sourceY, 0x00CC0020) || !GdiFlush())
                                    throw new Exception("GDI capture failed.");
                                Marshal.Copy(gdiBits, pixels, 0, pixels.Length);
                            }
                            finally { if (mode == "gdi-transient" && sourceDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sourceDC); }
                        }
                        else
                        {
                            Direct3D11CaptureFrame next;
                            while ((next = pool.TryGetNextFrame()) != null)
                            {
                                if (frameTexture != IntPtr.Zero) { Marshal.Release(frameTexture); frameTexture = IntPtr.Zero; }
                                if (currentFrame != null) currentFrame.Dispose();
                                currentFrame = next;
                            }
                            if (currentFrame == null) return;
                            if (frameTexture == IntPtr.Zero)
                            {
                                IntPtr surface = Marshal.GetIUnknownForObject(currentFrame.Surface), access = IntPtr.Zero;
                                try
                                {
                                    access = Query(surface, "a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                                    Guid textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                                    Marshal.ThrowExceptionForHR(Method<SurfaceTexture>(access, 3)(access, ref textureId, out frameTexture));
                                }
                                finally { if (access != IntPtr.Zero) Marshal.Release(access); Marshal.Release(surface); }
                            }
                            int localX = target.X - screen.Left, localY = target.Y - screen.Top;
                            var description = currentFrame.Surface.Description;
                            if (localX < 0 || localY < 0 || localX + 32 > description.Width || localY + 32 > description.Height)
                                throw new Exception("Sample outside the captured texture; stop and restart the probe on the original monitor.");
                            uint x = (uint)localX, y = (uint)localY;
                            Region region = new Region { Left = x, Top = y, Right = x + 32, Bottom = y + 32, Back = 1 };
                            copy(context, staging, 0, 0, 0, 0, frameTexture, 0, ref region);
                            Mapped mapped;
                            Marshal.ThrowExceptionForHR(map(context, staging, 0, 1, 0, out mapped));
                            try
                            {
                                for (int row = 0; row < 32; row++)
                                    Marshal.Copy(IntPtr.Add(mapped.Data, row * (int)mapped.RowPitch), pixels, row * 128, 128);
                            }
                            finally { unmap(context, staging, 0); }
                        }
                        if (measuring)
                        {
                            samples++;
                            durations.Add((Stopwatch.GetTimestamp() - sampleStart) * 1000.0 / Stopwatch.Frequency);
                        }
                        if (clock.ElapsedMilliseconds % 2000 >= 500)
                        {
                            int expected = phase == 1 ? 30 : 255;
                            for (int p = 0; p < pixels.Length; p += 4)
                            for (int c = 0; c < 3; c++)
                                if (Math.Abs(pixels[p + c] - expected) > 2)
                                    throw new Exception("Capture contains cursor pixels or unexpected colors: phase " + phase + ", actual " + pixels[p + c] + ", expected " + expected);
                            counts[phase]++;
                        }
                    }
                    catch (Exception e) { failure = e; timer.Stop(); form.Close(); }
                };
                Application.Run(form);
            }
            if (failure != null) throw failure;
            if (elapsed == 0) throw new Exception("Probe cancelled before measurement completed.");
            if (mode != "baseline") for (int i = 0; i < counts.Length; i++) if (counts[i] == 0) throw new Exception("No usable capture frame in phase " + i);
            durations.Sort();
            double p95 = durations.Count == 0 ? 0 : durations[(int)Math.Ceiling(durations.Count * .95) - 1];
            process.Refresh();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "METRIC,{0},{1:F3},{2:F3},{3:F3},{4},{5:F4},{6},{7:F3}", mode, elapsed, cpu / elapsed, busy, samples, p95, process.WorkingSet64, dwmCpu));
            Console.WriteLine("PASS: " + mode + ", moving pointer and matching light/dark background; no images saved.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("Graphics Capture probe: " + e.Message); return 1; }
        finally
        {
            if (frameTexture != IntPtr.Zero) Marshal.Release(frameTexture);
            if (currentFrame != null) currentFrame.Dispose();
            if (session != null) session.Dispose();
            if (pool != null) pool.Dispose();
            if (graphicsDevice != null) graphicsDevice.Dispose();
            foreach (IntPtr obj in new[] { staging, itemPointer, interopDevice, output, adapter, dxgi, context, device })
                if (obj != IntPtr.Zero) Marshal.Release(obj);
            if (gdiPrevious != IntPtr.Zero && gdiPrevious != new IntPtr(-1)) SelectObject(gdiMemory, gdiPrevious);
            if (gdiBitmap != IntPtr.Zero) DeleteObject(gdiBitmap);
            if (gdiMemory != IntPtr.Zero) DeleteDC(gdiMemory);
            if (gdiScreen != IntPtr.Zero)
            {
                if (mode == "gdi-monitor") DeleteDC(gdiScreen); else ReleaseDC(IntPtr.Zero, gdiScreen);
            }
            if (Cursor.Position == target) Cursor.Position = new System.Drawing.Point(original.X, original.Y);
        }
        }
    }
}
