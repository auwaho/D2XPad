using System.Diagnostics;

namespace D2XPad.SeriesProbe;

internal static class BrowserWindow
{
    public static void Open(Uri url)
    {
        var chrome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe");
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        string browser = File.Exists(chrome) ? chrome : edge;
        if (!File.Exists(browser))
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return;
        }
        var start = new ProcessStartInfo(browser) { UseShellExecute = false };
        start.ArgumentList.Add("--app=" + url.AbsoluteUri);
        start.ArgumentList.Add("--new-window");
        Process.Start(start)?.Dispose();
    }
}
