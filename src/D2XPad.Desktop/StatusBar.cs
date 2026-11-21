using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace D2XPad.Desktop;

public partial class MainWindow
{
    private enum StatusSeverity { Information, Success, Warning, Error }
    private sealed record StatusNotice(string Message, StatusSeverity Severity, string? Details = null);

    private const string Circle = "M8,2 A6,6 0 0 1 8,14 A6,6 0 0 1 8,2 Z ";
    private static readonly Geometry InformationIcon = Geometry.Parse(Circle + "M8,7.5 L8,11 M8,5 L8,5.8");
    private static readonly Geometry SuccessIcon = Geometry.Parse(Circle + "M5.2,8 L7.1,9.9 L10.9,6.1");
    private static readonly Geometry WarningIcon = Geometry.Parse("M8,2.5 L14,13 L2,13 Z M8,6 L8,9 M8,10.8 L8,11.3");
    private static readonly Geometry ErrorIcon = Geometry.Parse(Circle + "M6,6 L10,10 M10,6 L6,10");

    private StatusNotice? statusIssue, statusContext, statusSuccess, shownStatus;
    private long statusSuccessUntil;
    private int copyFeedbackRevision;

    private void ShowError(string? message, string? details = null) =>
        SetStatusIssue(message == null ? null : new(message, StatusSeverity.Error, details));

    private void ShowWarning(string message) => SetStatusIssue(new(message, StatusSeverity.Warning));

    private void SetStatusIssue(StatusNotice? issue)
    {
        statusIssue = issue;
        UpdateStatusBar();
    }

    private void SetStatusContext(string? message, bool warning = false)
    {
        statusContext = message == null ? null : new(message, warning ? StatusSeverity.Warning : StatusSeverity.Information);
        UpdateStatusBar();
    }

    private void ShowSuccess(string message, int durationMilliseconds = 5000)
    {
        statusSuccess = new(message, StatusSeverity.Success);
        statusSuccessUntil = Environment.TickCount64 + durationMilliseconds;
        UpdateStatusBar();
    }

    private void ShowSettingsSaveError(Exception exception)
    {
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        string summary = exception.HResult is sharingViolation or lockViolation
            ? "Couldn't save settings. File is in use."
            : "Couldn't save settings.";
        ShowError(summary, exception.ToString());
    }

    private void UpdateStatusBar()
    {
        if (statusSuccess != null && Environment.TickCount64 >= statusSuccessUntil) statusSuccess = null;
        var next = (statusIssue is { Severity: StatusSeverity.Error } ? statusIssue : null) ?? profileProblem ?? pollProblem
            ?? (automaticProblem is { Severity: StatusSeverity.Error } ? automaticProblem : null)
            ?? statusIssue ?? automaticProblem ?? (statusContext is { Severity: StatusSeverity.Warning } || busy ? statusContext : null)
            ?? statusSuccess ?? statusContext;
        if (next == shownStatus) return;
        shownStatus = next;

        copyFeedbackRevision++;
        CopyStatusDetailsLabel.Text = "Copy details";
        bool canCopy = next is { Severity: StatusSeverity.Error, Details.Length: > 0 };
        CopyStatusDetailsButton.Visibility = canCopy ? Visibility.Visible : Visibility.Collapsed;
        StatusMessage.Visibility = StatusIcon.Visibility = next == null ? Visibility.Hidden : Visibility.Visible;
        StatusMessage.Text = next?.Message.ReplaceLineEndings(" ") ?? "";
        StatusTooltipText.Text = canCopy ? FullStatusDetails(next!) : StatusMessage.Text;
        if (next == null) return;

        var (color, icon) = next.Severity switch
        {
            StatusSeverity.Error => ("Error", ErrorIcon),
            StatusSeverity.Warning => ("Warning", WarningIcon),
            StatusSeverity.Success => ("Accent", SuccessIcon),
            _ => ("SecondaryText", InformationIcon)
        };
        StatusMessage.Foreground = StatusIcon.Stroke = (Brush)FindResource(color);
        AutomationProperties.SetLiveSetting(StatusMessage, next.Severity == StatusSeverity.Error
            ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        StatusIcon.Data = icon;
        UIElementAutomationPeer.FromElement(StatusMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string FullStatusDetails(StatusNotice notice) => $"{notice.Message}{Environment.NewLine}{Environment.NewLine}{notice.Details}";

    private async void CopyStatusDetails(object sender, RoutedEventArgs e)
    {
        if (shownStatus is not { Severity: StatusSeverity.Error, Details.Length: > 0 } notice) return;
        int revision = ++copyFeedbackRevision;
        try
        {
            Clipboard.SetText(FullStatusDetails(notice));
            CopyStatusDetailsLabel.Text = "Copied";
        }
        catch (ExternalException) { CopyStatusDetailsLabel.Text = "Copy failed"; }

        await Task.Delay(TimeSpan.FromSeconds(2));
        if (revision == copyFeedbackRevision) CopyStatusDetailsLabel.Text = "Copy details";
    }
}
