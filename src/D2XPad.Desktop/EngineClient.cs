using System.Diagnostics;
using D2XPad.Capture;
using D2XPad.Live;
using D2XPad.SeriesProbe;

namespace D2XPad.Desktop;

internal sealed record DeviceChoice(string Path, string Label, string LayoutId, DeviceLayout? Layout = null)
{
    public override string ToString() => Label;
}

internal sealed record PadReading(ushort Buttons, ulong Extras, byte Lt, byte Rt,
    double Lx, double Ly, double Rx, double Ry)
{
    private static double HidAxis(byte value) => (value - 128) / (value < 128 ? 128d : 127d);
    private static double XboxAxis(short value) => value / (value < 0 ? 32768d : 32767d);

    public static PadReading FromPhysical(LiveState s, ExtraButtons extras) =>
        new((ushort)(s.Buttons | PadBindings.HatButtons(s.Hat)), (ulong)extras, s.Lt, s.Rt,
            HidAxis(s.Lx), -HidAxis(s.Ly), HidAxis(s.Rx), -HidAxis(s.Ry));

    public static PadReading FromXInput(XInputReading x) =>
        new(x.Buttons, 0, x.Lt, x.Rt, XboxAxis(x.Lx), XboxAxis(x.Ly), XboxAxis(x.Rx), XboxAxis(x.Ry));
}

internal sealed record EngineFrame(string SessionId, string Profile, int Slot, bool Connected,
    bool WaitingForNeutral, bool Suspended, long Received, PadReading? Physical, PadReading? Output,
    string? DevicePath = null, long ProfileRevision = 0, string? LayoutId = null,
    bool VibrationEnabled = false, string? VibrationProblem = null);

internal sealed class EngineClient : IDisposable
{
    private readonly ProfileCatalog catalog = new();
    private PhysicalSession? preview;
    private RawInput? previewInput;
    private string? previewPath;
    private volatile bool previewFailed;
    private DesktopLaunch? owned;
    private string? observedSession;

    public Task<IReadOnlyList<DeviceChoice>> DevicesAsync(CancellationToken token) => Task.Run<IReadOnlyList<DeviceChoice>>(() =>
    {
        var devices = PhysicalFeed.Devices();
        return devices.Select((d, i) =>
        {
            var layout = PhysicalSession.LayoutFor(d)!;
            return new DeviceChoice(d.Path, devices.Length == 1 ? layout.ShortName : $"{layout.ShortName} {i + 1} · {d.ContainerId ?? d.Path}",
                layout.Id, layout);
        }).ToArray();
    }, token);

    public void SetPreviewDevice(DeviceChoice? device)
    {
        if (!previewFailed && string.Equals(device?.Path, previewPath, StringComparison.OrdinalIgnoreCase)) return;
        previewInput?.Dispose();
        previewInput = null;
        preview = null;
        previewPath = null;
        previewFailed = false;
        if (device == null) return;
        var selected = PhysicalFeed.Select(null, device.Path);
        var session = new PhysicalSession(selected, PadBindings.Create(DeviceLayout.Resolve(device.LayoutId), []), _ => { }, () => { });
        previewInput = new RawInput([selected], session.Report, _ =>
        {
            session.Stop();
            previewFailed = true;
        }, new(session.SelectInstance, session.SetDevice, session.PowerChanged));
        preview = session;
        previewPath = device.Path;
    }

    public PadReading? Preview => preview is { Latest: { } frame } source
        && source.Layout.TryDecodePhysical(frame.Raw, out var physical, out var extras)
            ? PadReading.FromPhysical(physical, extras) : null;

    private static bool Active()
    {
        if (!EventWaitHandle.TryOpenExisting(D2XPad.Live.Program.StopEvent, out var signal)) return false;
        signal.Dispose();
        return true;
    }

    internal static EventWaitHandle ReserveStoppedConverter()
    {
        var reservation = new EventWaitHandle(false, EventResetMode.ManualReset, D2XPad.Live.Program.StopEvent, out bool created);
        if (created) return reservation;
        reservation.Dispose();
        throw new InvalidOperationException("Stop the converter before changing a saved controller.");
    }

    private static async Task<SessionStatus?> TryStatusAsync(int timeoutMs, CancellationToken token)
    {
        try { return await SessionControl.SendAsync("status", null, token, timeoutMs).ConfigureAwait(false); }
        catch (IOException ex) when (ex.InnerException is TimeoutException || !Active()) { return null; }
    }

    public async Task<EngineFrame?> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Active())
        {
            observedSession = null;
            return null;
        }
        var status = await TryStatusAsync(1000, token).ConfigureAwait(false);
        if (status == null)
        {
            if (!Active()) return null;
            throw new InvalidOperationException("D2XPad is busy starting, stopping or running a diagnostic. Waiting for the playable session.");
        }
        observedSession = status.SessionId;
        var output = XInputReader.Read(status.Slot);
        return new(status.SessionId, status.Profile, status.Slot, status.Connected, status.WaitingForNeutral,
            status.Suspended, status.Received,
            status.Physical is { } native ? PadReading.FromPhysical(native, status.Extras) : null,
            output == null ? null : PadReading.FromXInput(output), status.DevicePath, status.ProfileRevision, status.LayoutId,
            status.VibrationEnabled, status.VibrationProblem);
    }

    public async Task StartAsync(string profile, DeviceChoice? device, CancellationToken token, bool vibrationEnabled = false)
    {
        token.ThrowIfCancellationRequested();
        if (await ReadAsync(token).ConfigureAwait(false) != null) return;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "D2XPad.Live.exe"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("play");
        start.ArgumentList.Add("--desktop");
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profile);
        if (vibrationEnabled) start.ArgumentList.Add("--vibration");
        if (device != null)
        {
            start.ArgumentList.Add("--device-path");
            start.ArgumentList.Add(device.Path);
        }
        var child = new DesktopLaunch(Process.Start(start) ?? throw new IOException("The converter process could not start."));
        owned = child;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            while (true)
            {
                if (Active() && await TryStatusAsync(250, deadline.Token).ConfigureAwait(false) is { } status)
                {
                    if (status.ProcessId != child.Id)
                    {
                        // A concurrent CLI launch won the singleton and owns the playable session.
                        child.Stop();
                        owned = null;
                    }
                    else child.Ready = true;
                    return;
                }
                if (child.Exit.IsCompleted)
                {
                    var exit = await child.Exit.ConfigureAwait(false);
                    if (exit.Code == DriverApprovalCanceledException.DesktopExitCode) throw new DriverApprovalCanceledException();
                    throw new InvalidOperationException(exit.Error.Length == 0 ? "The converter closed before it was ready." : exit.Error);
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            child.Stop();
            throw new TimeoutException("The converter did not start in time. Check the Windows permission prompt and try again.");
        }
        catch { child.Stop(); throw; }
    }

    public Task SetVibrationAsync(string expectedSession, bool enabled, CancellationToken token) =>
        SessionControl.SetVibrationAsync(expectedSession, enabled, token);

    public string? TakeRuntimeFailure()
    {
        if (owned is not { Exit.IsCompletedSuccessfully: true } launch) return null;
        owned = null;
        var exit = launch.Exit.Result;
        if (!launch.Ready || exit.Code == 0) return null;
        return exit.Error.Length > 0 ? exit.Error : $"The converter stopped unexpectedly (exit code {exit.Code}).";
    }

    private sealed record LaunchExit(int Code, string Error);

    // The process owns its virtual device. Only its launching GUI has the write end of
    // this pipe, so cancelling a pending UAC request cannot signal a concurrent CLI run.
    private sealed class DesktopLaunch
    {
        private StreamWriter? input;
        public int Id { get; }
        public bool Ready { get; set; }
        public Task<LaunchExit> Exit { get; }

        public DesktopLaunch(Process child)
        {
            Id = child.Id;
            input = child.StandardInput;
            Exit = WaitForExitAsync(child);
        }

        public void Stop()
        {
            try { Interlocked.Exchange(ref input, null)?.Dispose(); }
            catch (IOException) { } // The child already closed its pipe.
        }

        private async Task<LaunchExit> WaitForExitAsync(Process child)
        {
            using (child)
            {
                var error = child.StandardError.ReadToEndAsync();
                try
                {
                    await child.WaitForExitAsync().ConfigureAwait(false);
                    return new(child.ExitCode, (await error.ConfigureAwait(false)).Trim());
                }
                finally { Stop(); }
            }
        }
    }

    public async Task SwitchAsync(string profile, CancellationToken token) =>
        await SessionControl.SendAsync("switch", ProfileSelection.Normalize(profile), token).ConfigureAwait(false);

    public async Task<string?> DeleteProfileAsync(ProfileEditSnapshot selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reserve the same singleton as 'play' so a new session cannot start with the file during offline deletion.
        using var reservation = new EventWaitHandle(false, EventResetMode.ManualReset, D2XPad.Live.Program.StopEvent, out bool offline);
        if (!offline) return (await SessionControl.DeleteAsync(selected, token).ConfigureAwait(false)).Profile;
        catalog.Resolve(selected.Profile.Bindings.Layout.DefaultProfileName);
        catalog.Delete(selected);
        return null;
    }

    public async Task StopAsync(CancellationToken token)
    {
        if (owned is { } launch && !launch.Exit.IsCompleted)
        {
            launch.Stop();
            try { await launch.Exit.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new TimeoutException("The converter is still stopping. Dismiss any pending Windows permission prompt.");
            }
            return;
        }
        if (!EventWaitHandle.TryOpenExisting(D2XPad.Live.Program.StopEvent, out var signal)) return;
        string sessionId;
        using (signal)
        {
            // Keeping this reference open prevents the singleton event from being recycled
            // for a different process between the playable-session check and the stop request.
            var status = await TryStatusAsync(2000, token).ConfigureAwait(false);
            if (status == null)
            {
                if (observedSession != null)
                    throw new IOException("The previously connected converter did not answer. Retry once it responds.");
                return; // A diagnostic has no playable session to stop.
            }
            sessionId = status.SessionId;
            signal.Set();
        }
        var watch = Stopwatch.StartNew();
        while (Active())
        {
            if (watch.Elapsed >= TimeSpan.FromSeconds(45))
                throw new TimeoutException("The converter is still stopping. Check for a pending Windows permission prompt.");
            await Task.Delay(100, token).ConfigureAwait(false);
            // A fresh launch can acquire the name before our next poll.
            try
            {
                if (Active() && await TryStatusAsync(150, token).ConfigureAwait(false) is { } current
                    && sessionId != current.SessionId) return;
            }
            catch (IOException) { } // The original server can close during a final status request.
        }
    }

    public void Dispose() => SetPreviewDevice(null);
}
