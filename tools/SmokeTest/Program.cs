using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WebcamSwitcher.SmokeTest;

internal static class Program
{
    const string PipeName = @"\\.\pipe\WebcamSwitcher.Frames.v1";
    const string SourceId = "{7B1C4D2E-8F3A-4B5C-9D6E-1F2A3B4C5D6E}";
    const int W = 1920, H = 1080;
    const uint MAGIC = 0x57564353u;

    [DllImport("WebcamSwitcher.Source.dll")]
    static extern int WebcamSwitcher_Start([MarshalAs(UnmanagedType.LPWStr)] string friendlyName, [MarshalAs(UnmanagedType.LPWStr)] string sourceId, out IntPtr handle);
    [DllImport("WebcamSwitcher.Source.dll")]
    static extern void WebcamSwitcher_Stop(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuf, uint inBuf, uint defaultTimeout, IntPtr secAttr);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr file, byte[] buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct MsgHeader { public uint magic; public ushort version; public ushort type; public uint payloadLen; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ConfigMsg { public uint width; public uint height; public uint stride; public uint format; public uint fpsNum; public uint fpsDen; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct FrameInfo { public ulong sequence; public ulong timestampQpc; public uint width; public uint height; public uint stride; public uint format; public uint payloadBytes; public uint flags; }

    const ushort T_CONFIG = 1, T_FRAME = 2;

    static byte[] StructToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(s, p, false);
        Marshal.Copy(p, buf, 0, size);
        Marshal.FreeHGlobal(p);
        return buf;
    }

    static bool WriteAll(IntPtr pipe, byte[] data)
    {
        uint total = 0;
        while (total < data.Length)
        {
            if (!WriteFile(pipe, data, (uint)(data.Length - total), out uint written, IntPtr.Zero))
                return false;
            total += written;
        }
        return true;
    }

    static void Pump(CancellationToken ct)
    {
        byte[] nv12 = new byte[W * H + W * H / 2];
        Array.Fill(nv12, (byte)128);

        ulong seq = 0;
        while (!ct.IsCancellationRequested)
        {
            IntPtr pipe = CreateNamedPipeW(PipeName, 3, 0, 16, 0, 0, 0, IntPtr.Zero);
            if (pipe == (IntPtr)(-1)) { Thread.Sleep(200); continue; }
            ConnectNamedPipe(pipe, IntPtr.Zero);
            Console.WriteLine("[pump] client connected");

            var cfg = new ConfigMsg { width = W, height = H, stride = W, format = 1, fpsNum = 30, fpsDen = 1 };
            var cfgHdr = new MsgHeader { magic = MAGIC, version = 1, type = T_CONFIG, payloadLen = (uint)Marshal.SizeOf<ConfigMsg>() };
            var cfgPayload = StructToBytes(cfg);
            byte[] cfgMsg = new byte[Marshal.SizeOf<MsgHeader>() + cfgPayload.Length];
            Buffer.BlockCopy(StructToBytes(cfgHdr), 0, cfgMsg, 0, Marshal.SizeOf<MsgHeader>());
            Buffer.BlockCopy(cfgPayload, 0, cfgMsg, Marshal.SizeOf<MsgHeader>(), cfgPayload.Length);
            WriteAll(pipe, cfgMsg);

            while (!ct.IsCancellationRequested)
            {
                var fi = new FrameInfo { sequence = seq++, timestampQpc = 0, width = W, height = H, stride = W, format = 1, payloadBytes = (uint)nv12.Length, flags = 0 };
                var hdr = new MsgHeader { magic = MAGIC, version = 1, type = T_FRAME, payloadLen = (uint)(Marshal.SizeOf<FrameInfo>() + nv12.Length) };
                byte[] msg = new byte[Marshal.SizeOf<MsgHeader>() + Marshal.SizeOf<FrameInfo>() + nv12.Length];
                Buffer.BlockCopy(StructToBytes(hdr), 0, msg, 0, Marshal.SizeOf<MsgHeader>());
                Buffer.BlockCopy(StructToBytes(fi), 0, msg, Marshal.SizeOf<MsgHeader>(), Marshal.SizeOf<FrameInfo>());
                Buffer.BlockCopy(nv12, 0, msg, Marshal.SizeOf<MsgHeader>() + Marshal.SizeOf<FrameInfo>(), nv12.Length);
                if (!WriteAll(pipe, msg)) { Console.WriteLine("[pump] client disconnected"); break; }
                Thread.Sleep(33);
            }
            CloseHandle(pipe);
        }
    }

    static async Task<int> Main()
    {
        Console.WriteLine("=== WebcamSwitcher SmokeTest ===");

        int hr = WebcamSwitcher_Start("SmokeTest Vcam", SourceId, out IntPtr handle);
        Console.WriteLine($"WebcamSwitcher_Start hr=0x{hr:X8} handle=0x{handle:X}");

        var cts = new CancellationTokenSource();
        var pump = Task.Run(() => Pump(cts.Token));

        await Task.Delay(2000);
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var vcamDev = devices.FirstOrDefault(d => d.Name.Contains("SmokeTest Vcam", StringComparison.OrdinalIgnoreCase));
            if (vcamDev == null)
            {
                Console.WriteLine("RESULT: VCAM NOT FOUND. Devices:");
                foreach (var d in devices) Console.WriteLine($"  - {d.Name}");
                return 1;
            }
            Console.WriteLine($"Found device: {vcamDev.Name}");

            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(g => g.Id == vcamDev.Id);
            if (group == null) { Console.WriteLine("RESULT: source group not found"); return 1; }

            capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });

            var colorSource = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (colorSource == null) { Console.WriteLine("RESULT: no color source"); return 1; }

            reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Nv12);
            var tcs = new TaskCompletionSource<(int, int, string?)>();
            reader.FrameArrived += (_, e) =>
            {
                using var rf = reader.TryAcquireLatestFrame();
                var sw = rf?.VideoMediaFrame?.SoftwareBitmap;
                if (sw != null && !tcs.Task.IsCompleted)
                    tcs.TrySetResult((sw.PixelWidth, sw.PixelHeight, sw.BitmapPixelFormat.ToString()));
            };
            await reader.StartAsync();

            var dims = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Console.WriteLine($"FrameReader frame: {dims.Item1}x{dims.Item2} format={dims.Item3}");
            Console.WriteLine(dims.Item1 == W && dims.Item2 == H ? "RESULT: OK (frame matches 1920x1080)" : $"RESULT: SIZE MISMATCH ({dims.Item1}x{dims.Item2})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT: ERROR {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            reader?.StopAsync().AsTask().Wait(2000);
            capture?.Dispose();
            cts.Cancel();
            try { await pump; } catch { }
            WebcamSwitcher_Stop(handle);
        }

        Console.WriteLine("=== DONE ===");
        return 0;
    }
}
