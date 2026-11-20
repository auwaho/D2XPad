using System.ComponentModel;
using System.Runtime.InteropServices;
using D2XPad.Capture;
using Microsoft.Win32.SafeHandles;

namespace D2XPad.Live;

internal static class RumbleSurvey
{
    public static int Run()
    {
        var pads = BluetoothPads();
        Console.WriteLine("85HF Bluetooth HID capability survey (descriptor metadata only).");
        if (pads.Length == 0)
        {
            Console.WriteLine("No 2DC8:2067 Bluetooth gamepad is visible to Raw Input. Turn it on and retry.");
            return 2;
        }
        var paths = pads.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containers = pads.Where(d => d.ContainerId != null).Select(d => d.ContainerId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int errors = 0;
        foreach (string path in HidInterfaces().Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (transport, container) = RawInput.Identify(path);
            if (!paths.Contains(path) && (container == null || !containers.Contains(container))) continue;
            found.Add(path);
            Console.WriteLine($"\n{transport} · container {container ?? "unknown"} · {path}");
            try { Describe(path); }
            catch (Exception ex) when (ex is IOException or Win32Exception) { errors++; Console.WriteLine("Unavailable: " + ex.Message); }
        }
        Console.WriteLine($"Inspected {found.Count} HID interface(s); {errors} unavailable. Output/Feature reports alone do not identify a motor command.");
        return errors == 0 ? 0 : 2;
    }

    internal static bool Supports(HidCollection d) => d.VendorId == 0x2dc8 && d.ProductId == 0x2067 &&
        d.Transport == "Bluetooth" && d.UsagePage == 1 && d.Usage == 5 &&
        d.InputReportLength == 32 && d.InputReportIds?.Contains((byte)1) == true;

    private static HidCollection[] BluetoothPads() => RawInput.Enumerate().Where(d =>
        d.VendorId == 0x2dc8 && d.ProductId == 0x2067 && d.Transport == "Bluetooth" && d.UsagePage == 1 && d.Usage == 5).ToArray();

    public static async Task<int> PulseAsync(CancellationToken token, string? selectedPath = null)
    {
        using var reservation = ConverterReservation.ReserveStopped("Stop conversion before running the physical vibration test.");
        var devices = BluetoothPads().Where(d => selectedPath == null ||
            string.Equals(d.Path, selectedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (devices.Length != 1)
            throw new IOException($"The pulse requires exactly one visible Bluetooth 85HF gamepad. Found {devices.Length}.");
        var pad = devices[0];
        using var motors = OpenMotors(pad);
        Console.WriteLine($"85HF Bluetooth: sending one 350 ms pulse at 48/255 to magnitude channels 1 and 2. Container: {pad.ContainerId ?? "unknown"}.");
        await motors.WriteAsync(0, 0, token);
        try
        {
            await motors.WriteAsync(48, 48, token);
            await Task.Delay(350, token);
        }
        finally
        {
            try { await motors.WriteAsync(0, 0, CancellationToken.None); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                throw new IOException("Could not confirm delivery of the stop report. Turn the controller off if it is still vibrating.", ex);
            }
        }
        Console.WriteLine("Pulse and stop writes completed. Physical vibration and stop require confirmation by touch.");
        return 0;
    }

    internal static IPhysicalMotorWriter OpenMotors(HidCollection pad)
    {
        if (!Supports(pad))
            throw new InvalidDataException("Vibration requires the verified 2DC8:2067 Bluetooth gamepad collection.");
        var handle = CreateFileW(pad.Path, 0x40000000, 3, 0, 3, 0x40000000, 0); // GENERIC_WRITE, overlapped
        nint data = 0;
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
            if (!HidD_GetAttributes(handle, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (attributes.Vendor != pad.VendorId || attributes.Product != pad.ProductId ||
                !BluetoothPads().Any(d => Supports(d) && d.Handle == pad.Handle && string.Equals(d.Path, pad.Path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.ContainerId, pad.ContainerId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The physical handle no longer identifies the selected Bluetooth 85HF.");
            if (!HidD_GetPreparsedData(handle, out data)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Check(HidP_GetCaps(data, out var caps));
            if (caps.Page != 1 || caps.Usage != 5 || caps.OutputLength != 5 || caps.OutputValues != 1 || caps.OutputButtons != 0)
                throw new InvalidDataException("The connected 85HF has a different output descriptor. No report was sent.");
            var fields = new Capability[1];
            ushort count = 1;
            Check(HidP_GetValueCaps(1, fields, ref count, data));
            var f = fields[0];
            if (count != 1 || f.Id != 5 || f.Page != 0x0f || f.First != 0x70 || f.Range != 0 ||
                f.Width != 8 || f.Count != 4 || f.Minimum != 0 || f.Maximum != 255)
                throw new InvalidDataException("Expected report 05 with four byte-sized PID Magnitude values. No report was sent.");
            return new MotorWriter(new FileStream(handle, FileAccess.Write, bufferSize: 1, isAsync: true), data, f);
        }
        catch
        {
            if (data != 0) HidD_FreePreparsedData(data);
            handle.Dispose();
            throw;
        }
    }

    internal static byte[] Magnitudes(byte left, byte right) => [left, right, 0, 0];

    private sealed class MotorWriter(FileStream stream, nint descriptor, Capability field) : IPhysicalMotorWriter
    {
        private nint data = descriptor;

        public async ValueTask WriteAsync(byte left, byte right, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(data == 0, this);
            byte[] values = Magnitudes(left, right);
            byte[] report = [field.Id, 0, 0, 0, 0];
            Check(HidP_SetUsageValueArray(1, field.Page, field.Link, field.First, values, (ushort)values.Length,
                data, report, (uint)report.Length));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            try { await stream.WriteAsync(report, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            { throw new IOException("The Bluetooth motor write timed out after two seconds.", ex); }
        }

        public void Dispose()
        {
            nint release = Interlocked.Exchange(ref data, 0);
            if (release == 0) return;
            try { stream.Dispose(); }
            finally { HidD_FreePreparsedData(release); }
        }
    }

    private static string[] HidInterfaces()
    {
        HidD_GetHidGuid(out var hid);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint result = CM_Get_Device_Interface_List_SizeW(out uint size, ref hid, null, 0);
            if (result != 0) throw new IOException($"Could not size the HID device-interface list (0x{result:X}).");
            var data = new char[Math.Max(size, 2)];
            result = CM_Get_Device_Interface_ListW(ref hid, null, data, (uint)data.Length, 0);
            if (result == 0) return new string(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != 0x1a) throw new IOException($"Could not read the HID device-interface list (0x{result:X}).");
        }
        throw new IOException("The HID device list kept changing. Retry the survey.");
    }

    private static void Describe(string path)
    {
        // Zero desired access permits metadata queries without opening a report read/write channel.
        using var handle = CreateFileW(path, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out nint preparsed)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Check(HidP_GetCaps(preparsed, out var caps));
            Console.WriteLine($"Collection {caps.Page:X4}/{caps.Usage:X4}; input {caps.InputLength} bytes");
            Report("Output", 1, caps.OutputLength, caps.OutputButtons, caps.OutputValues);
            Report("Feature", 2, caps.FeatureLength, caps.FeatureButtons, caps.FeatureValues);
            void Report(string name, int type, ushort length, ushort buttons, ushort values)
            {
                Console.WriteLine($"  {name}: {length} bytes (including report ID), {buttons} button caps, {values} value caps");
                for (int group = 0; group < 2; group++)
                {
                    ushort count = group == 0 ? buttons : values;
                    if (count == 0) continue;
                    var fields = new Capability[count];
                    Check(group == 0 ? HidP_GetButtonCaps(type, fields, ref count, preparsed)
                        : HidP_GetValueCaps(type, fields, ref count, preparsed));
                    foreach (var field in fields.Take(count))
                    {
                        string usages = field.Range == 0 ? $"{field.First:X4}" : $"{field.First:X4}–{field.Last:X4}";
                        string item = group == 0 ? "buttons" : $"{field.Width}-bit value × {field.Count}, logical {field.Minimum}..{field.Maximum}";
                        Console.WriteLine($"    ID 0x{field.Id:X2}, page 0x{field.Page:X4}, usage {usages}, {item}");
                    }
                }
            }
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private static void Check(int status)
    {
        if (status != 0x00110000) throw new IOException($"Windows could not parse HID capabilities (0x{status:X8}).");
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Caps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort Page;
        [FieldOffset(4)] public ushort InputLength;
        [FieldOffset(6)] public ushort OutputLength;
        [FieldOffset(8)] public ushort FeatureLength;
        [FieldOffset(52)] public ushort OutputButtons;
        [FieldOffset(54)] public ushort OutputValues;
        [FieldOffset(58)] public ushort FeatureButtons;
        [FieldOffset(60)] public ushort FeatureValues;
    }
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct Capability
    {
        [FieldOffset(0)] public ushort Page;
        [FieldOffset(2)] public byte Id;
        [FieldOffset(6)] public ushort Link;
        [FieldOffset(12)] public byte Range;
        [FieldOffset(18)] public ushort Width;
        [FieldOffset(20)] public ushort Count;
        [FieldOffset(40)] public int Minimum;
        [FieldOffset(44)] public int Maximum;
        [FieldOffset(56)] public ushort First;
        [FieldOffset(58)] public ushort Last;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        public uint Size;
        public ushort Vendor, Product, Version;
    }
    [DllImport("hid", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid")]
    private static extern int HidP_SetUsageValueArray(int type, ushort page, ushort link, ushort usage,
        byte[] values, ushort valueBytes, nint data, [In, Out] byte[] report, uint reportBytes);
    [DllImport("hid")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("cfgmgr32", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string? deviceId, uint flags);
    [DllImport("cfgmgr32", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] buffer, uint size, uint flags);
    [DllImport("kernel32", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("hid", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid")] private static extern int HidP_GetCaps(nint data, out Caps caps);
    [DllImport("hid")] private static extern int HidP_GetButtonCaps(int reportType, [Out] Capability[] fields, ref ushort count, nint data);
    [DllImport("hid")] private static extern int HidP_GetValueCaps(int reportType, [Out] Capability[] fields, ref ushort count, nint data);
}
