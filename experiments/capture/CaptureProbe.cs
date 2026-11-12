using System;
using System.Runtime.InteropServices;

// Capability probe only. Does not alter the mouse or save desktop pixels.
internal static partial class CaptureProbe
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct FrameInfo
    {
        public long LastPresent, LastMouseUpdate;
        public uint Frames, Coalesced, Protected;
        public Point Pointer;
        public uint PointerVisible, MetadataBytes, PointerBytes;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct OutputInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public int Left, Top, Right, Bottom;
        public uint Attached, Rotation;
        public IntPtr Monitor;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetAdapter(IntPtr self, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumOutput(IntPtr self, uint index, out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetOutputInfo(IntPtr self, out OutputInfo info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Duplicate(IntPtr self, IntPtr device, out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Acquire(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrame(IntPtr self);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, uint driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    static T Method<T>(IntPtr obj, int slot) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));
    }
    static IntPtr Query(IntPtr obj, string iid)
    {
        Guid guid = new Guid(iid); IntPtr result;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(obj, ref guid, out result));
        return result;
    }
    static int Main()
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, adapter = IntPtr.Zero;
        IntPtr output = IntPtr.Zero, output1 = IntPtr.Zero, duplication = IntPtr.Zero, resource = IntPtr.Zero;
        bool acquired = false;
        try
        {
            SetProcessDPIAware();
            if (Marshal.SizeOf(typeof(FrameInfo)) != 48 || Marshal.SizeOf(typeof(OutputInfo)) != 96)
                throw new Exception("Unexpected native structure layout.");
            uint level;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out device, out level, out context));
            dxgi = Query(device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Method<GetAdapter>(dxgi, 7)(dxgi, out adapter));
            Marshal.ThrowExceptionForHR(Method<EnumOutput>(adapter, 7)(adapter, 0, out output));
            OutputInfo screen;
            Marshal.ThrowExceptionForHR(Method<GetOutputInfo>(output, 7)(output, out screen));
            output1 = Query(output, "00cddea8-939b-4b83-a340-a685226666cc");
            Marshal.ThrowExceptionForHR(Method<Duplicate>(output1, 22)(output1, device, out duplication));
            FrameInfo frame;
            Marshal.ThrowExceptionForHR(Method<Acquire>(duplication, 8)(duplication, 1000, out frame, out resource));
            acquired = true;
            Point position;
            if (!GetCursorPos(out position)) throw new Exception("Cursor position unavailable.");
            Console.WriteLine("Output bounds {0},{1}-{2},{3}; cursor {4},{5}; on output {6}", screen.Left, screen.Top, screen.Right, screen.Bottom, position.X, position.Y,
                position.X >= screen.Left && position.X < screen.Right && position.Y >= screen.Top && position.Y < screen.Bottom);
            Console.WriteLine("DXGI available; output {0}x{1}; rotation {2}; separate pointer visible {3}; pointer shape bytes {4}; accumulated frames {5}",
                screen.Right - screen.Left, screen.Bottom - screen.Top, screen.Rotation, frame.PointerVisible != 0, frame.PointerBytes, frame.Frames);
            Console.WriteLine("An integrated/software pointer must be checked against the shape test before using this capture backend.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("DXGI probe: " + e.Message); return 1; }
        finally
        {
            if (resource != IntPtr.Zero) Marshal.Release(resource);
            if (acquired) Method<ReleaseFrame>(duplication, 14)(duplication);
            foreach (IntPtr obj in new[] { duplication, output1, output, adapter, dxgi, context, device })
                if (obj != IntPtr.Zero) Marshal.Release(obj);
        }
    }
}
