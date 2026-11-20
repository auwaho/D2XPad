using System.ComponentModel;
using System.Text.Json;

namespace D2XPad.Desktop;

public partial class MainWindow
{
    internal async Task<bool?> ReadTrayOutputAsync()
    {
        if (exiting) return null;
        if (IsVisible) return statusKnown && frame?.Output != null;

        // A hidden window pauses its tester. Sample only the assigned XInput slot,
        // yielding the control pipe to foreground operations such as switching profiles.
        if (!sessionLock.Wait(0)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            return (await engine.ReadAsync(timeout.Token))?.Output != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or Win32Exception or JsonException)
        {
            return false;
        }
        finally { sessionLock.Release(); }
    }
}
