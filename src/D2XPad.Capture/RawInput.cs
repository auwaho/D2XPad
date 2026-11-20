using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace D2XPad.Capture;

internal sealed record HidCollection(nint Handle, string Path, ushort VendorId, ushort ProductId,
    ushort UsagePage, ushort Usage, string Transport, string? ContainerId, int? InputReportLength = null,
    IReadOnlyList<byte>? InputReportIds = null, IReadOnlyList<HidValueField>? InputValues = null, string? ButtonSignature = null,
    IReadOnlyList<HidButtonBits>? InputButtons = null);

internal sealed record HidValueField(byte ReportId, ushort Page, ushort Usage, int Offset, int Width,
    int Minimum, int Maximum);

internal sealed record HidButtonBits(byte ReportId, int ByteOffset, byte Mask);

internal sealed record DeviceFollower(Func<IReadOnlyList<HidCollection>, HidCollection?> Select,
    Action<HidCollection?> Changed, Action<bool> PowerChanged);

/// <summary>Owns the process's Raw Input registrations on a dedicated message-pump thread.</summary>
internal sealed class RawInput : IDisposable
{
    private const uint HidType = 2, Input = 0x10000003, DeviceInfo = 0x2000000b,
        DeviceName = 0x20000007, PreparsedData = 0x20000005;
    private const uint WmInput = 0xff, DeviceChange = 0xfe, TimerMessage = 0x0113, StopMessage = 0x8001;
    private const uint RefreshAfterResume = 0x8002, Suspend = 4, ResumeAutomatic = 18, ResumeFromSuspend = 7;
    private const uint InputSink = 0x100, DeviceNotify = 0x2000, Remove = 1;
    private static readonly uint HeaderSize = (uint)(8 + 2 * IntPtr.Size);
    private readonly Dictionary<nint, HidCollection> devices;
    private readonly HashSet<(uint Vendor, uint Product, ushort Page, ushort Usage)> selectedUsages;
    private readonly Action<HidCollection, byte[]> onReport;
    private readonly Action<string> onError;
    private readonly DeviceFollower? follower;
    private readonly Thread worker;
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly nint window;
    private byte[] buffer = new byte[1024];
    private int stopping;

    internal RawInput(IReadOnlyList<HidCollection> selected, Action<HidCollection, byte[]> onReport,
        Action<string> onError, DeviceFollower? follower = null)
    {
        if (selected.Count == 0 || selected.Any(s => !AllowedUsage(s.UsagePage, s.Usage)))
            throw new ArgumentException("Select a game controller HID collection.", nameof(selected));
        selectedUsages = selected.Select(s => ((uint)s.VendorId, (uint)s.ProductId, s.UsagePage, s.Usage)).ToHashSet();
        devices = selected.ToDictionary(s => s.Handle);
        this.onReport = onReport;
        this.onError = onError;
        this.follower = follower;
        worker = new Thread(Pump) { IsBackground = true, Name = "D2XPad Raw Input" };
        worker.Start();
        window = ready.Task.GetAwaiter().GetResult();
    }

    private static bool AllowedUsage(ushort page, ushort usage) =>
        page != 0 && usage != 0 && !(page == 1 && usage is 1 or 2 or 6 or 7);

    private bool SelectedUsage(uint vid, uint pid, ushort page, ushort usage) =>
        selectedUsages.Contains((vid, pid, page, usage));

    internal static IReadOnlyList<HidCollection> Enumerate(Func<uint, uint, ushort, ushort, bool>? eligible = null)
    {
        // The standalone capture tool defaults to all safe collections of the 85HF.
        eligible ??= static (vid, pid, _, _) => vid == 0x2dc8 && pid == 0x2067;
        var result = new List<HidCollection>();
        foreach (var entry in DeviceEntries())
        {
            if (entry.Type != HidType) continue;
            var info = new HidDeviceInfo { Size = (uint)Marshal.SizeOf<HidDeviceInfo>() };
            uint size = info.Size;
            if (ReadDeviceInfo(entry.Handle, DeviceInfo, ref info, ref size) == uint.MaxValue ||
                !AllowedUsage(info.Page, info.Usage) || !eligible(info.VendorId, info.ProductId, info.Page, info.Usage)) continue;
            string? path = ReadPath(entry.Handle);
            if (path == null) continue;
            var (transport, container) = Identify(path);
            var (reportLength, reportIds, values, buttonSignature, buttons) = ReadCapabilities(entry.Handle);
            result.Add(new(entry.Handle, path, (ushort)info.VendorId, (ushort)info.ProductId,
                info.Page, info.Usage, transport, container, reportLength, reportIds, values, buttonSignature, buttons));
        }
        return result;
    }

    private static string? ReadPath(nint handle)
    {
        uint chars = 0;
        if (ReadDeviceBytes(handle, DeviceName, null, ref chars) == uint.MaxValue || chars == 0) return null;
        var name = new StringBuilder(checked((int)chars + 1));
        return ReadDeviceName(handle, DeviceName, name, ref chars) == uint.MaxValue ? null : name.ToString();
    }

    private static DeviceEntry[] DeviceEntries()
    {
        uint entrySize = (uint)Marshal.SizeOf<DeviceEntry>();
        // Device count can change between the size query and the actual enumeration.
        while (true)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, entrySize) == uint.MaxValue)
                throw NativeError("Could not enumerate Raw Input devices");
            if (count == 0) return [];
            var entries = new DeviceEntry[count];
            uint copied = GetRawInputDeviceList(entries, ref count, entrySize);
            if (copied == uint.MaxValue)
            {
                if (Marshal.GetLastWin32Error() == 122) continue;
                throw NativeError("Could not read the Raw Input device list");
            }
            if (copied < entries.Length) Array.Resize(ref entries, checked((int)copied));
            return entries;
        }
    }

    private static (int? Length, IReadOnlyList<byte>? Ids, IReadOnlyList<HidValueField>? Values, string? ButtonSignature,
        IReadOnlyList<HidButtonBits>? Buttons) ReadCapabilities(nint device)
    {
        uint length = 0;
        if (ReadDeviceBytes(device, PreparsedData, null, ref length) == uint.MaxValue || length == 0)
            return (null, null, null, null, null);
        var bytes = new byte[length];
        if (ReadDeviceBytes(device, PreparsedData, bytes, ref length) == uint.MaxValue ||
            HidP_GetCaps(bytes, out var caps) != 0x00110000) return (null, null, null, null, null);

        var buttons = new Capability[caps.ButtonCount];
        var values = new Capability[caps.ValueCount];
        ushort buttonCount = caps.ButtonCount, valueCount = caps.ValueCount;
        if ((buttonCount != 0 && HidP_GetButtonCaps(0, buttons, ref buttonCount, bytes) != 0x00110000) ||
            (valueCount != 0 && HidP_GetValueCaps(0, values, ref valueCount, bytes) != 0x00110000))
            return (caps.InputReportByteLength, null, null, null, null);
        var ids = buttons.Take(buttonCount).Concat(values.Take(valueCount))
            .Select(c => c.ReportId).Distinct().Order().ToArray();
        var (signature, buttonBits) = ReadButtons(buttons.Take(buttonCount), bytes, caps.InputReportByteLength);
        return (caps.InputReportByteLength, ids, ValueFields(values.Take(valueCount), bytes, caps.InputReportByteLength),
            signature, buttonBits);
    }

    private static (string Signature, IReadOnlyList<HidButtonBits> Bits) ReadButtons(
        IEnumerable<Capability> capabilities, byte[] descriptor, int length)
    {
        if (length is < 2 or > 4096) return ("", []);
        using var signature = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var report = new byte[length];
        var masks = new Dictionary<byte, byte[]>();
        Span<byte> marker = stackalloc byte[1];
        foreach (var c in capabilities.OrderBy(c => c.ReportId).ThenBy(c => c.Link).ThenBy(c => c.FirstIndex))
        {
            int last = c.IsRange == 0 ? c.FirstUsage : c.LastUsage;
            int lastIndex = c.IsRange == 0 ? c.FirstIndex : c.LastIndex;
            var metadata = FormattableString.Invariant($"{c.ReportId},{c.Page},{c.Link},{c.BitField},{c.FirstUsage},{last},{c.FirstIndex},{lastIndex}:");
            signature.AppendData(Encoding.ASCII.GetBytes(metadata));
            if (last - c.FirstUsage <= 255)
                for (int u = c.FirstUsage; u <= last; u++)
                {
                    Array.Clear(report);
                    report[0] = c.ReportId;
                    ushort usage = (ushort)u;
                    uint count = 1;
                    bool assigned = HidP_SetUsages(0, c.Page, c.Link, ref usage, ref count, descriptor, report, (uint)length) == 0x00110000;
                    marker[0] = assigned ? (byte)1 : (byte)0;
                    signature.AppendData(marker);
                    if (!assigned) continue;
                    signature.AppendData(report);
                    if (!masks.TryGetValue(c.ReportId, out var mask))
                        masks.Add(c.ReportId, mask = new byte[length]);
                    for (int i = 1; i < length; i++) mask[i] |= report[i];
                }
        }
        var bits = new List<HidButtonBits>();
        foreach (var (id, mask) in masks)
            for (int i = 1; i < length; i++)
                if (mask[i] != 0) bits.Add(new(id, i, mask[i]));
        return (Convert.ToHexString(signature.GetHashAndReset()), bits);
    }

    // Ask the HID parser to encode a value into a local scratch report. This also locates
    // non-byte-aligned fields without depending on undocumented preparsed-data offsets.
    private static HidValueField[] ValueFields(IEnumerable<Capability> capabilities, byte[] descriptor, int length)
    {
        var fields = new List<HidValueField>();
        if (length is < 2 or > 4096) return [];
        var report = new byte[length];
        foreach (var cap in capabilities)
        {
            if (cap.Width is < 1 or > 24 || cap.Absolute == 0 || cap.Alias != 0 ||
                cap.ReportCount != 1 && cap.IsRange == 0 || cap.Maximum <= cap.Minimum) continue;
            int last = cap.IsRange != 0 ? cap.LastUsage : cap.FirstUsage;
            if (last - cap.FirstUsage > 255) continue;
            for (int usage = cap.FirstUsage; usage <= last; usage++)
            {
                Array.Clear(report);
                report[0] = cap.ReportId;
                if (HidP_SetUsageValue(0, cap.Page, cap.Link, (ushort)usage, (1u << cap.Width) - 1,
                    descriptor, report, (uint)length) != 0x00110000) continue;
                int offset = -1, bits = 0;
                for (int i = 8; i < length * 8; i++)
                {
                    if ((report[i / 8] & (1 << (i % 8))) == 0) continue;
                    if (offset < 0) offset = i;
                    if (i != offset + bits) break;
                    bits++;
                }
                if (bits == cap.Width)
                    fields.Add(new(cap.ReportId, cap.Page, (ushort)usage, offset, bits, cap.Minimum, cap.Maximum));
            }
        }
        return fields.Distinct().OrderBy(f => f.ReportId).ThenBy(f => f.Offset).ThenBy(f => f.Page).ThenBy(f => f.Usage).ToArray();
    }

    internal static (string Transport, string? Container) Identify(string path)
    {
        nint set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) return ("Unknown", null);
        try
        {
            var iface = new DeviceInterface { Size = (uint)Marshal.SizeOf<DeviceInterface>() };
            if (!SetupDiOpenDeviceInterfaceW(set, path, 0, ref iface)) return ("Unknown", null);
            var item = new DeviceInstance { Size = (uint)Marshal.SizeOf<DeviceInstance>() };
            // The size query also fills SP_DEVINFO_DATA; ERROR_INSUFFICIENT_BUFFER is expected.
            SetupDiGetDeviceInterfaceDetailW(set, ref iface, 0, 0, out uint needed, ref item);
            if (needed == 0 || item.DevInst == 0) return ("Unknown", null);

            var key = new PropertyKey { Format = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
            uint size = 16;
            string? container = CM_Get_DevNode_PropertyW(item.DevInst, ref key, out uint type, out var guid,
                ref size, 0) == 0 && type == 13 && guid != Guid.Empty ? guid.ToString("D") : null;
            return (ClassifyTransport(AncestorIds(item.DevInst)), container);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static IEnumerable<string> AncestorIds(uint node)
    {
        for (int depth = 0; depth < 64; depth++)
        {
            var id = new StringBuilder(512);
            if (CM_Get_Device_IDW(node, id, id.Capacity, 0) == 0) yield return id.ToString();
            if (CM_Get_Parent(out uint parent, node, 0) != 0 || parent == node) yield break;
            node = parent;
        }
    }

    internal static string ClassifyTransport(IEnumerable<string> ancestors)
    {
        string? transport = null;
        bool usbRootHub = false;
        foreach (string instance in ancestors)
        {
            if (instance.StartsWith(@"SWD\HID", StringComparison.OrdinalIgnoreCase) ||
                instance.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) ||
                instance.Contains("VIGEM", StringComparison.OrdinalIgnoreCase)) return "Virtual";
            // A Bluetooth radio can itself be on USB. Keep the first identifying transport.
            if (instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) transport ??= "Bluetooth";
            if (instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) transport ??= "USB";
            if (instance.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) usbRootHub = true;
        }
        // Virtual buses can expose USB VID/PID children under a generic ROOT\SYSTEM parent.
        // Require the USB root hub in the ancestry before offering an apparent USB device.
        return transport == "USB" && !usbRootHub ? "Unknown" : transport ?? "Unknown";
    }

    private void Pump()
    {
        nint hwnd = 0, powerRegistration = 0;
        var powerGate = new object();
        bool suspended = false, refreshPending = false;
        ulong? resumeTick = null;
        Exception? powerError = null;
        PowerCallback powerCallback = ReceivePower;
        var subscription = new PowerSubscription { Callback = Marshal.GetFunctionPointerForDelegate(powerCallback) };
        Registration[]? registrations = null;
        try
        {
            hwnd = CreateWindowExW(0, "STATIC", "D2XPad Capture", 0, 0, 0, 0, 0, -3, 0, 0, 0);
            if (hwnd == 0) throw NativeError("Could not create the Raw Input window");
            var targets = devices.Values.Select(d => (d.UsagePage, d.Usage)).Distinct().Select(d =>
                new Registration { Page = d.UsagePage, Usage = d.Usage, Flags = InputSink | DeviceNotify, Window = hwnd })
                .ToArray();
            if (!RegisterRawInputDevices(targets, (uint)targets.Length, (uint)Marshal.SizeOf<Registration>()))
                throw NativeError("Could not register for controller input reports");
            registrations = targets;
            if (follower != null)
            {
                // Use the power manager's callback directly. A noninteractive, message-only HID
                // window need not receive the user-session WM_POWERBROADCAST notification.
                uint error = PowerRegisterSuspendResumeNotification(2, ref subscription, out powerRegistration);
                if (error != 0) throw new Win32Exception((int)error, "Could not subscribe to Windows suspend and resume.");
                Reconcile();
                // Some Bluetooth stacks drop a collection without a removal notification.
                // Inspect OS presence independently of how frequently an idle pad sends input.
                if (SetTimer(hwnd, 1, 750, 0) == 0) throw NativeError("Could not start the device presence timer");
            }
            else
            {
                var current = Enumerate(SelectedUsage).ToDictionary(d => d.Handle);
                if (devices.Values.Any(d => !current.TryGetValue(d.Handle, out var found) ||
                    !string.Equals(found.Path, d.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The controller disconnected while capture was starting.");
            }

            ready.SetResult(hwnd);
            while (Volatile.Read(ref stopping) == 0)
            {
                int status = GetMessageW(out var message, 0, 0, 0);
                if (Volatile.Read(ref powerError) is { } problem) throw new IOException("Windows power transition failed.", problem);
                if (status < 0) throw NativeError("Could not receive a Raw Input message");
                if (status == 0 || message.Id == StopMessage) break;
                try
                {
                    lock (powerGate)
                    {
                        if (refreshPending && !suspended)
                        {
                            Reconcile();
                            refreshPending = false;
                        }
                        if (message.Id == DeviceChange && message.WParam == 2 && devices.ContainsKey(message.LParam))
                        {
                            if (follower == null)
                                throw new IOException("The selected collection disconnected. Reconnect the gamepad and start a new capture.");
                            devices.Clear();
                            follower.Changed(null);
                        }
                        else if (follower != null && (message.Id == DeviceChange || message.Id == TimerMessage) && message.WParam == 1)
                            Reconcile(presenceCheck: message.Id == TimerMessage);
                        else if (message.Id == WmInput && !suspended &&
                            (resumeTick == null || PostedAfterResume(message.Time, resumeTick.Value, GetTickCount64())))
                        {
                            try { HandleReport(message.LParam); }
                            catch (Win32Exception ex) when (follower != null && ex.NativeErrorCode is 6 or 87 or 1167)
                            {
                                // A queued WM_INPUT may refer to a handle removed just before the read.
                                Reconcile();
                            }
                        }
                    }
                }
                finally { DispatchMessageW(ref message); }
            }
        }
        catch (Exception ex)
        {
            if (!ready.TrySetException(ex) && Volatile.Read(ref stopping) == 0) onError(ex.Message);
        }
        finally
        {
            if (powerRegistration != 0) PowerUnregisterSuspendResumeNotification(powerRegistration);
            if (registrations != null)
            {
                for (int i = 0; i < registrations.Length; i++)
                {
                    registrations[i].Flags = Remove;
                    registrations[i].Window = 0;
                }
                RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<Registration>());
            }
            if (hwnd != 0) DestroyWindow(hwnd);
            GC.KeepAlive(powerCallback);
        }

        uint ReceivePower(nint context, uint kind, nint setting)
        {
            if (kind is not (Suspend or ResumeAutomatic or ResumeFromSuspend)) return 0;
            try
            {
                lock (powerGate)
                {
                    bool sleeping = kind == Suspend;
                    if (suspended == sleeping) return 0; // Automatic wake can be followed by a user wake.
                    suspended = sleeping;
                    if (!sleeping)
                    {
                        resumeTick = GetTickCount64();
                        refreshPending = true;
                    }
                    follower!.PowerChanged(sleeping);
                    if (!sleeping) PostMessageW(hwnd, RefreshAfterResume, 0, 0);
                }
            }
            catch (Exception ex)
            {
                // A managed exception must never unwind through a native callback.
                Interlocked.CompareExchange(ref powerError, ex, null);
                PostMessageW(hwnd, StopMessage, 0, 0);
                return 1;
            }
            return 0;
        }
    }

    // MSG.time contains the low 32 bits of the system tick count. Compare message age
    // with time since wake to handle wraparound and reject the boundary tick itself.
    internal static bool PostedAfterResume(uint posted, ulong resumeTick, ulong now) =>
        unchecked((uint)now - posted) < now - resumeTick;

    // All changes to the handle dictionary and all report delivery run on the same pump.
    private void Reconcile(bool presenceCheck = false)
    {
        var previous = devices.Values.SingleOrDefault();
        if (presenceCheck && previous != null && DeviceEntries().Any(e => e.Type == HidType && e.Handle == previous.Handle) &&
            string.Equals(ReadPath(previous.Handle), previous.Path, StringComparison.OrdinalIgnoreCase)) return;
        var match = follower!.Select(Enumerate(SelectedUsage));
        if (previous?.Handle == match?.Handle &&
            string.Equals(previous?.Path, match?.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (previous != null)
        {
            devices.Clear();
            follower.Changed(null);
        }
        if (match != null)
        {
            devices.Add(match.Handle, match);
            follower.Changed(match);
        }
    }

    private void HandleReport(nint raw)
    {
        uint required = 0;
        if (GetRawInputData(raw, Input, null, ref required, HeaderSize) == uint.MaxValue)
            throw NativeError("Could not determine the Raw Input packet length");
        if (required > buffer.Length) buffer = new byte[required];
        uint bytes = GetRawInputData(raw, Input, buffer, ref required, HeaderSize);
        if (bytes == uint.MaxValue) throw NativeError("Could not read the Raw Input packet");
        if (bytes < HeaderSize || BitConverter.ToUInt32(buffer, 0) != HidType) return;
        nint handle = IntPtr.Size == 8 ? (nint)BitConverter.ToInt64(buffer, 8) : BitConverter.ToInt32(buffer, 8);
        if (!devices.TryGetValue(handle, out var device)) return;
        int offset = checked((int)HeaderSize + 8);
        if (bytes < offset) throw new InvalidDataException("Invalid RAWHID header.");
        uint length = BitConverter.ToUInt32(buffer, (int)HeaderSize);
        uint count = BitConverter.ToUInt32(buffer, (int)HeaderSize + 4);
        if (length == 0 || (ulong)length * count > bytes - (uint)offset)
            throw new InvalidDataException("Invalid RAWHID packet length.");
        for (uint i = 0; i < count; i++)
        {
            onReport(device, buffer.AsSpan(offset, (int)length).ToArray());
            offset += (int)length;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0 && worker.IsAlive)
            PostMessageW(window, StopMessage, 0, 0);
        if (Thread.CurrentThread != worker) worker.Join();
    }

    private static Win32Exception NativeError(string context)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{context}: {new Win32Exception(code).Message} ({code}).");
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceEntry { public nint Handle; public uint Type; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct HidDeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public uint VendorId;
        [FieldOffset(12)] public uint ProductId;
        [FieldOffset(20)] public ushort Page;
        [FieldOffset(22)] public ushort Usage;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(46)] public ushort ButtonCount;
        [FieldOffset(48)] public ushort ValueCount;
    }
    // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS both have this stride and ReportID offset.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct Capability
    {
        [FieldOffset(0)] public ushort Page;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(3)] public byte Alias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort Link;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(15)] public byte Absolute;
        [FieldOffset(18)] public ushort Width;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int Minimum;
        [FieldOffset(44)] public int Maximum;
        [FieldOffset(56)] public ushort FirstUsage;
        [FieldOffset(58)] public ushort LastUsage;
        [FieldOffset(68)] public ushort FirstIndex;
        [FieldOffset(70)] public ushort LastIndex;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Registration { public ushort Page, Usage; public uint Flags; public nint Window; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface { public uint Size; public Guid Class; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInstance { public uint Size; public Guid Class; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [DllImport("user32", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] DeviceEntry[]? list, ref uint count, uint size);
    [DllImport("user32", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint ReadDeviceInfo(nint device, uint cmd, ref HidDeviceInfo info, ref uint size);
    [DllImport("user32", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint ReadDeviceBytes(nint device, uint cmd, [Out] byte[]? data, ref uint size);
    [DllImport("user32", EntryPoint = "GetRawInputDeviceInfoW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ReadDeviceName(nint device, uint cmd, StringBuilder data, ref uint size);
    [DllImport("hid")] private static extern int HidP_GetCaps(byte[] data, out HidCaps caps);
    [DllImport("hid")] private static extern int HidP_GetButtonCaps(int reportType,
        [Out] Capability[] buttons, ref ushort count, byte[] preparsedData);
    [DllImport("hid")] private static extern int HidP_GetValueCaps(int reportType,
        [Out] Capability[] values, ref ushort count, byte[] preparsedData);
    [DllImport("hid")] private static extern int HidP_SetUsageValue(int reportType, ushort usagePage,
        ushort link, ushort usage, uint value, byte[] preparsedData, [In, Out] byte[] report, uint length);
    [DllImport("hid")] private static extern int HidP_SetUsages(int reportType, ushort usagePage,
        ushort link, ref ushort usage, ref uint usageCount, byte[] preparsedData, [In, Out] byte[] report, uint length);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string type, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(Registration[] targets, uint count, uint size);
    [DllImport("user32", SetLastError = true)]
    private static extern int GetMessageW(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] private static extern nint DispatchMessageW(ref Message msg);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerCallback(nint context, uint kind, nint setting);
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerSubscription { public nint Callback, Context; }
    [DllImport("powrprof", ExactSpelling = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref PowerSubscription recipient, out nint registration);
    [DllImport("powrprof", ExactSpelling = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint registration);
    [DllImport("kernel32", ExactSpelling = true)] private static extern ulong GetTickCount64();
    [DllImport("user32", SetLastError = true)]
    private static extern uint GetRawInputData(nint raw, uint command, [Out] byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);

    [DllImport("setupapi")] private static extern nint SetupDiCreateDeviceInfoList(nint type, nint parent);
    [DllImport("setupapi", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetupDiOpenDeviceInterfaceW(nint set, string path, uint flags, ref DeviceInterface iface);
    [DllImport("setupapi")]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref DeviceInterface iface, nint detail,
        uint bytes, out uint needed, ref DeviceInstance instance);
    [DllImport("setupapi")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32")]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type,
        out Guid data, ref uint size, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
