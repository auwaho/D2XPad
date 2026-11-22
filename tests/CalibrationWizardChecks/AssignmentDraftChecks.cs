using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class AssignmentDraftChecks
{
    public static void Run()
    {
        var bindings = PadBindings.CreateMappings(DeviceLayout.Bluetooth85HF,
            [new("A", [new("B")]), new("extra:PL", [new("A"), new("RT", 80)])]);
        var profile = new ControllerProfile("Platformer", bindings);
        var sources = bindings.Sources.OrderByDescending(source => source.Id.StartsWith("extra:", StringComparison.Ordinal)).ToArray();
        int attempts = 0;
        var editor = new ProfileEditor(profile, canReplace: true, (name, mappings, deadzones) =>
        {
            Check(name == "Platformer copy", "Editing the name must save a copy.");
            Check(deadzones == default, "Editing mappings must leave the saved stick settings intact.");
            Check(mappings.Single(mapping => mapping.Source == "A").Actions.SequenceEqual([new XboxAction("B")]) &&
                mappings.Single(mapping => mapping.Source == "extra:PL").Actions.SequenceEqual([new("A"), new("RT", 80), new("LeftStickUp"), new("X")]),
                "Saving changed an unrelated input or lost combined actions.");
            if (++attempts == 1) throw new IOException("The first save was rejected.");
            return Task.CompletedTask;
        })
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };
        Exception? failure = null;
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                void Select(string id)
                {
                    editor.InputList.SelectedIndex = Array.FindIndex(sources, source => source.Id == id);
                    Check(editor.SelectedName.Text == sources[editor.InputList.SelectedIndex].Name, $"The editor did not select {id}.");
                }

                void Choose(string id) =>
                    editor.TargetPicker.SelectedItem = editor.TargetPicker.Items.Cast<InputControl>().Single(target => target.Id == id);

                Check(!editor.AddAction.IsEnabled && editor.TargetPicker.SelectedIndex == -1 &&
                    editor.PendingChanges.Visibility != Visibility.Visible, "Opening the editor created a staged action or change.");
                editor.ProfileName.Text = "Temporary name";
                Check(editor.PendingChanges.Visibility == Visibility.Visible, "A profile name edit did not mark the draft.");
                editor.ProfileName.Text = profile.Name;
                Check(editor.PendingChanges.Visibility != Visibility.Visible, "Restoring the name left a pending change.");
                Select("extra:PL");
                editor.AddAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(editor.ActionsList.Items.Count == 2, "Add without a target modified the draft.");
                Choose("LeftStickUp");
                Check(editor.AddAction.IsEnabled, "A selected action did not enable Add.");
                Select("A");
                Check(!editor.AddAction.IsEnabled && editor.TargetPicker.SelectedIndex == -1,
                    "Changing physical input retained the previous action choice.");

                Check(editor.DefaultSource.Visibility == Visibility.Visible && editor.DefaultSource.IsEnabled &&
                    !editor.ResetSource.IsEnabled, "The edited standard input should offer its built-in default separately.");
                Click(editor.ClearSource);
                Check(editor.ActionsList.Items.Count == 0 && editor.ResetSource.IsEnabled &&
                    editor.PendingChanges.Visibility == Visibility.Visible && Equals(editor.ClearSource.Content, "Undo clear"),
                    "Clearing a mapping did not stage a pending change with an undo action.");
                Click(editor.DefaultSource);
                Check(editor.ActionsList.Items.Count == 1 && !editor.DefaultSource.IsEnabled && editor.ResetSource.IsEnabled &&
                    Equals(editor.ClearSource.Content, "Clear actions"), "Using default did not replace the clear history and stage the built-in action.");
                Click(editor.ResetSource);
                Check(editor.PendingChanges.Visibility != Visibility.Visible && editor.DefaultSource.IsEnabled && !editor.ResetSource.IsEnabled,
                    "Restoring the opened actions did not clear the pending change.");
                Click(editor.ClearSource);
                Choose("Y");
                Click(editor.AddAction);
                Check(Equals(editor.ClearSource.Content, "Clear actions"), "A newly added action left stale clear history.");
                Click(editor.ResetSource);

                Select("extra:PL");
                Check(editor.DefaultSource.Visibility == Visibility.Collapsed && editor.ActionsList.Items.Count == 2,
                    "Changing A affected an unrelated extra input.");
                editor.UpdateLayout();
                var strength = Descendants(editor.ActionsList).OfType<Slider>()
                    .Single(slider => AutomationProperties.GetAutomationId(slider) == "Strength_RT");
                strength.Value = 75;
                Check(editor.ResetSource.IsEnabled && editor.PendingChanges.Visibility == Visibility.Visible,
                    "Changing analog strength did not mark the input and profile modified.");
                strength.Value = 80;
                Check(!editor.ResetSource.IsEnabled && editor.PendingChanges.Visibility != Visibility.Visible,
                    "Returning a slider to the opened strength left a pending change.");

                Choose("LeftStickUp");
                Click(editor.AddAction);
                Check(!editor.AddAction.IsEnabled && editor.TargetPicker.SelectedIndex == -1 && editor.ActionsList.Items.Count == 3,
                    "Adding an action should preserve the combination and clear its choice.");
                Choose("X");
                Click(editor.AddAction);
                Check(editor.ActionsList.Items.Count == 4, "The editor did not keep multiple simultaneous actions.");
                strength.Value = 75;
                Click(editor.ClearSource);
                Select("A");
                Check(Equals(editor.ClearSource.Content, "Clear actions"), "The pending clear affected another input.");
                Select("extra:PL");
                Check(Equals(editor.ClearSource.Content, "Undo clear"), "Switching inputs lost the undo state.");
                Click(editor.ClearSource);
                editor.UpdateLayout();
                var restoredStrength = Descendants(editor.ActionsList).OfType<Slider>()
                    .Single(slider => AutomationProperties.GetAutomationId(slider) == "Strength_RT");
                Check(editor.ActionsList.Items.Count == 4 && restoredStrength.Value == 75 &&
                    editor.PendingChanges.Visibility == Visibility.Visible, "Undo clear lost previous unsaved actions or strength.");
                restoredStrength.Value = 80;
                editor.ProfileName.Text = "Platformer copy";
                Check(Equals(editor.SaveProfile.Content, "Save copy and apply"), "A renamed draft does not show the save-copy action.");
                Click(editor.SaveProfile);
                Check(attempts == 1 && editor.EditorMessage.Text == "The first save was rejected." && editor.SaveProfile.IsEnabled &&
                    editor.PendingChanges.Visibility == Visibility.Visible, "A failed save lost the draft or its retry action.");
                Click(editor.SaveProfile);
            }
            catch (Exception ex) { failure = ex; editor.Close(); }
        });
        bool? result = editor.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Profile mappings interaction failed.", failure);
        Check(result == true && attempts == 2, "Retry did not complete the save.");
        Console.WriteLine("WPF profile mappings passed explicit action choice, draft tracking, scoped reset, undo clear, analog strength and save retry.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Click(Button button)
    {
        Check(button.IsEnabled && button.Visibility == Visibility.Visible, $"Disabled or hidden button: {button.Name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
