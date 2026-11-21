using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;
using D2XPad.Live;

namespace D2XPad.Desktop;

public partial class MainWindow
{
    private static string ServiceSetupPath => Path.Combine(AppContext.BaseDirectory, "D2XPad.ServiceSetup.exe");
    private static readonly string? ServiceSetupSha256 = typeof(MainWindow).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(entry => entry.Key == "D2XPad.ServiceSetup.SHA256")?.Value;
    private ConversionServiceStatus displayedServiceStatus = ConversionServiceStatus.Unavailable;
    private long nextServiceStatus;

    private void UpdateConversionServiceSettings()
    {
        long now = Environment.TickCount64;
        if (now >= nextServiceStatus)
        {
            displayedServiceStatus = ConversionService.Status();
            nextServiceStatus = now + 1000;
        }

        bool available = ServiceSetupSha256 != null && File.Exists(ServiceSetupPath);
        InstallConversionServiceButton.Content = displayedServiceStatus == ConversionServiceStatus.Missing
            ? "Install service" : "Remove service";
        InstallConversionServiceButton.IsEnabled = available && LibraryEditable &&
            displayedServiceStatus is not (ConversionServiceStatus.Unavailable or ConversionServiceStatus.Starting);
        ConversionServiceStatusText.Text = !available
            ? "Extract the complete portable ZIP to manage the conversion service."
            : displayedServiceStatus switch
            {
                ConversionServiceStatus.Missing => "Install once to start conversion without a Windows prompt.",
                ConversionServiceStatus.Running => "Installed. Start conversion without a Windows prompt.",
                ConversionServiceStatus.Starting => "The conversion service is starting.",
                ConversionServiceStatus.Stopped => "The conversion service is installed but stopped.",
                _ => "Could not read the conversion service status."
            };
    }

    private async void ChangeConversionService(object sender, RoutedEventArgs e)
    {
        if (!LibraryEditable || !InstallConversionServiceButton.IsEnabled) return;
        bool install = displayedServiceStatus == ConversionServiceStatus.Missing;
        if (!install && MessageBox.Show(this,
            "Remove the conversion service? Windows will ask for approval when you start conversion again.",
            "Remove conversion service", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await RunAction(install ? "Installing the conversion service…" : "Removing the conversion service…",
            install ? "Couldn't install the conversion service." : "Couldn't remove the conversion service.", async () =>
            {
                using var reservation = EngineClient.ReserveStoppedConverter();
                using var identity = WindowsIdentity.GetCurrent();
                var start = new ProcessStartInfo(ServiceSetupPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Environment.SystemDirectory,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add(install ? "install" : "remove");
                start.ArgumentList.Add(identity.User!.Value);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                starting = attempt;
                try
                {
                    using var process = await Task.Run(() => StartServiceSetup(start));
                    await process.WaitForExitAsync(attempt.Token);
                    if (process.ExitCode != 0)
                        throw new IOException($"Conversion service setup returned exit code {process.ExitCode}.");
                    ShowSuccess(install ? "Conversion service installed." : "Conversion service removed.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    ShowWarning("Windows approval was canceled.");
                }
                finally
                {
                    starting = null;
                    nextServiceStatus = 0;
                }
            });
    }

    private static Process StartServiceSetup(ProcessStartInfo start)
    {
        // Hold the verified executable open until Windows has launched it.
        using var file = File.OpenRead(start.FileName);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), ServiceSetupSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Service setup does not match this build. Re-extract the original D2XPad ZIP.");
        return Process.Start(start) ?? throw new IOException("Windows did not start service setup.");
    }
}
