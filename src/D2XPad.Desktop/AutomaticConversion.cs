using System.Windows;
using D2XPad.Live;

namespace D2XPad.Desktop;

internal sealed record AutomaticConversionTarget(string Path, string LayoutId, string? ContainerId)
{
    public DeviceChoice? Select(IReadOnlyList<DeviceChoice> devices)
    {
        var compatible = devices.Where(device => device.LayoutId == LayoutId);
        var exact = compatible.Where(device => string.Equals(Path, device.Path, StringComparison.OrdinalIgnoreCase) &&
            (ContainerId == null || device.ContainerId == null || SameContainer(device))).Take(2).ToArray();
        if (exact.Length == 1) return exact[0];
        if (ContainerId == null) return null;
        var sameContainer = compatible.Where(SameContainer).Take(2).ToArray();
        return sameContainer.Length == 1 ? sameContainer[0] : null;
    }

    private bool SameContainer(DeviceChoice device) =>
        string.Equals(ContainerId, device.ContainerId, StringComparison.OrdinalIgnoreCase);
}

public partial class MainWindow
{
    private bool automaticWaiting;
    private long? automaticServiceDeadline;
    private StatusNotice? automaticProblem;

    private AutomaticConversionTarget? RememberedAutomaticTarget =>
        preferences.DevicePath is { Length: > 0 } path && preferences.LayoutId is { Length: > 0 } layoutId
            ? new(path, layoutId, preferences.ContainerId) : null;

    internal void BeginAutomaticConversion()
    {
        if (preferences.StartAutomatically) ArmAutomaticConversion();
    }

    private void ArmAutomaticConversion()
    {
        automaticWaiting = frame == null;
        automaticServiceDeadline = null;
        automaticProblem = null;
        nextDevices = 0;
    }

    private void CancelAutomaticConversion()
    {
        automaticWaiting = false;
        automaticServiceDeadline = null;
        automaticProblem = null;
        nextDevices = 0;
    }

    private void StartAutomaticallyChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || exiting) return;
        bool enabled = StartAutomatically.IsChecked == true;
        if (enabled == preferences.StartAutomatically) return;
        try { preferences.SaveStartAutomatically(enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StartAutomatically.IsChecked = preferences.StartAutomatically;
            ShowSettingsSaveError(ex);
            return;
        }

        if (enabled) ArmAutomaticConversion();
        else CancelAutomaticConversion();
        Draw();
    }

    // Driven by the tray timer, so waiting works before the window is ever shown.
    internal async Task PollAutomaticConversionAsync()
    {
        if (!automaticWaiting || exiting || busy || calibrating || !sessionLock.Wait(0)) return;
        DeviceChoice? device;
        AutomaticConversionTarget? target;
        try
        {
            await ReadSession();
            if (exiting || !automaticWaiting) return;
            if (frame != null)
            {
                CancelAutomaticConversion();
                return;
            }
            target = RememberedAutomaticTarget;
            if (target == null || !AutomaticServiceReady()) return;
            var found = await engine.DevicesAsync(lifetime.Token);
            if (exiting || !automaticWaiting || RememberedAutomaticTarget != target) return;
            device = target.Select(found);
            if (device == null) return;
        }
        catch (OperationCanceledException) when (exiting) { return; }
        catch (Exception ex)
        {
            FailAutomaticConversion("Couldn't check for the selected controller.", ex);
            return;
        }
        finally
        {
            sessionLock.Release();
            if (IsVisible && !exiting) Draw();
        }

        if (busy || exiting || calibrating || !automaticWaiting || RememberedAutomaticTarget != target) return;
        await RunAction("Starting conversion…", "Couldn't start conversion automatically.", async () =>
        {
            try
            {
                var deviceLayout = device.Layout ?? DeviceLayout.Resolve(device.LayoutId);
                var remembered = preferences.ReadProfile(device.LayoutId);
                string profile = remembered != null && catalog.IsCompatible(remembered, deviceLayout)
                    ? remembered : deviceLayout.DefaultProfileName;
                await StartConverterAsync(profile, device, LiveOutputMode.Service);
                CancelAutomaticConversion();
            }
            catch (OperationCanceledException) when (exiting) { throw; }
            catch (Exception ex)
            {
                // The Bluetooth collection can disappear between enumeration and opening the input.
                if (target.Select(await engine.DevicesAsync(lifetime.Token)) != null)
                    FailAutomaticConversion("Couldn't start conversion automatically.", ex);
            }
        });
    }

    private bool AutomaticServiceReady()
    {
        switch (ConversionService.Status())
        {
            case ConversionServiceStatus.Running:
                automaticServiceDeadline = null;
                return true;
            case ConversionServiceStatus.Missing:
                FailAutomaticConversion("Install the conversion service in Settings to start automatically.");
                break;
            default:
                long now = Environment.TickCount64;
                automaticServiceDeadline ??= now + 15000;
                if (now >= automaticServiceDeadline)
                    FailAutomaticConversion("The conversion service is unavailable. Check its status in Settings.");
                break;
        }
        return false;
    }

    private void FailAutomaticConversion(string message, Exception? details = null)
    {
        CancelAutomaticConversion();
        automaticProblem = new(message, details == null ? StatusSeverity.Warning : StatusSeverity.Error, details?.ToString());
        UpdateStatusBar();
    }
}
