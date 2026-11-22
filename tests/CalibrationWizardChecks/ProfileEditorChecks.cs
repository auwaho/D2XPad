using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class ProfileEditorChecks
{
    public static void Run(string directory)
    {
        var catalog = new ProfileCatalog(Path.Combine(directory, "profile-editor-fixtures"));
        var preset = catalog.OpenEditor("default");
        const string name = "Profile editor fixture";
        var copiedDeadzones = new StickDeadzones(64, 13, 10, 14);

        Edit(catalog, preset, editor =>
        {
            Check(editor.Title == "Profile editor" && editor.ProfileTabs.SelectedItem == editor.MappingsTab &&
                Equals(editor.SaveProfile.Content, "Save copy and apply"), "Built-in profiles must open on Mappings and require a copy.");
            SelectA(editor, preset.Profile.Bindings);
            Click(editor.ClearSource);
            editor.TargetPicker.SelectedItem = editor.TargetPicker.Items.Cast<InputControl>().Single(target => target.Id == "B");
            Click(editor.AddAction);

            SelectTab(editor, editor.SticksTab);
            Check(editor.LeftDeadzone.Inner == 0 && editor.RightDeadzone.Inner == 0 &&
                editor.LeftDeadzone.Outer == 100 && editor.RightDeadzone.Outer == 100,
                "The built-in profile must open both stick ranges at 0%–100%.");
            editor.LeftDeadzone.InnerHandle.Value = 100;
            Check(editor.LeftDeadzone.Inner == 99, "The inner handle must reach 99% when the outer handle is at 100%.");
            editor.LeftDeadzone.OuterHandle.Value = 0;
            Check(editor.LeftDeadzone.Outer == editor.LeftDeadzone.Inner + 1, "The outer handle must stay above the inner handle.");
            editor.LeftDeadzone.SetRange(64, 85);
            Check(editor.LeftDeadzone.Inner == 64 && editor.LeftDeadzone.Outer == 85,
                "The slider must accept an inner handle above 30% with a closer outer threshold.");
            editor.LeftDeadzone.InnerHandle.Value = 100;
            Check(editor.LeftDeadzone.Inner == 84 && editor.LeftDeadzone.Outer == 85,
                "Moving the inner handle must leave a one percentage point gap below the current outer handle.");
            editor.LeftDeadzone.SetRange(copiedDeadzones.Left, 100 - copiedDeadzones.LeftOuter);
            Check(editor.RightDeadzone.Inner == 0 && editor.RightDeadzone.Outer == 100,
                "Changing the left range must leave the right one alone.");
            editor.RightDeadzone.SetRange(copiedDeadzones.Right, 100 - copiedDeadzones.RightOuter);
            CheckStickPreviews(editor, copiedDeadzones.Left);
            editor.ProfileName.Text = name;

            SelectTab(editor, editor.MappingsTab);
            Check(editor.SelectedName.Text == "A" && editor.ResetSource.IsEnabled &&
                editor.PendingChanges.Visibility == Visibility.Visible, "Switching tabs must preserve the selected input and mapping draft.");
            SelectTab(editor, editor.SticksTab);
            CheckRanges(editor, copiedDeadzones, "Switching tabs must preserve all four stick handles.");
        });

        var copied = catalog.OpenEditor(name);
        Check(copied.Profile.Bindings.Deadzones == copiedDeadzones && copied.Profile.Bindings.GetActions("A").SequenceEqual([new XboxAction("B")]),
            "Saving from Sticks must write the deadzones and the mapping to the copy together.");
        Check(catalog.Resolve(preset.Profile.Name).Bindings.Deadzones == default &&
            catalog.Resolve(preset.Profile.Name).Bindings.GetActions("A").SequenceEqual([new XboxAction("A")]),
            "Saving a copy must preserve the built-in profile.");

        var updatedDeadzones = copiedDeadzones with { Right = 18, RightOuter = 11 };
        Edit(catalog, copied, editor =>
        {
            Check(editor.ProfileName.Text == name && Equals(editor.SaveProfile.Content, "Save and apply"),
                "A managed profile must allow updating the existing name.");
            SelectTab(editor, editor.SticksTab);
            CheckRanges(editor, copiedDeadzones, "The managed profile must reopen with its saved stick ranges.");
            editor.RightDeadzone.SetRange(copiedDeadzones.Right, 100 - updatedDeadzones.RightOuter);
            Check(editor.PendingChanges.Visibility == Visibility.Visible, "An outer deadzone edit must mark the whole profile as modified.");
            editor.RightDeadzone.SetRange(copiedDeadzones.Right, 100 - copiedDeadzones.RightOuter);
            Check(editor.PendingChanges.Visibility != Visibility.Visible, "Restoring the saved range must clear the pending state.");
            editor.RightDeadzone.SetRange(updatedDeadzones.Right, 100 - updatedDeadzones.RightOuter);
            SelectTab(editor, editor.MappingsTab);
            SelectA(editor, copied.Profile.Bindings);
            Click(editor.DefaultSource);
        });

        var updated = catalog.Resolve(name);
        Check(updated.Bindings.Deadzones == updatedDeadzones && updated.Bindings.GetActions("A").SequenceEqual([new XboxAction("A")]),
            "Saving from Mappings must update the same profile with both edits.");
        Console.WriteLine("WPF profile editor passed independent range handles, live stick previews, cross-tab draft retention and combined saves.");
    }

    private static void CheckStickPreviews(ProfileEditor editor, int leftInner)
    {
        editor.UpdateStickPreview(new PadReading(0, 0, 0, 0, 0.91, 0, 0, 0.06));
        Check(editor.LeftStickPreview.PhysicalPosition == new Point(0.91, 0) &&
            editor.LeftStickPreview.FilteredPosition is { } saturated && Math.Abs(saturated.X - 1) < 1e-12 && saturated.Y == 0,
            "The left preview must retain physical input and show full output above the 90% threshold.");
        Check(editor.RightStickPreview.PhysicalPosition == new Point(0, 0.06) &&
            editor.RightStickPreview.FilteredPosition == new Point(0, 0),
            "The right preview must show its own input and center it inside the independent inner zone.");
        editor.LeftDeadzone.SetRange(leftInner, 100);
        Check(editor.LeftStickPreview.FilteredPosition is { } filtered && filtered.X is > 0 and < 1 && filtered.Y == 0,
            "Changing a draft handle must refresh the preview before another physical sample arrives.");
        editor.LeftDeadzone.SetRange(leftInner, 90);
        editor.UpdateStickPreview(null);
        Check(editor.LeftStickPreview.PhysicalPosition == null && editor.LeftStickPreview.FilteredPosition == null &&
            editor.RightStickPreview.PhysicalPosition == null && editor.RightStickPreview.FilteredPosition == null,
            "Losing the physical reading must clear both preview markers.");
    }

    private static void CheckRanges(ProfileEditor editor, StickDeadzones zones, string message) =>
        Check(editor.LeftDeadzone.Inner == zones.Left && editor.LeftDeadzone.Outer == 100 - zones.LeftOuter &&
            editor.RightDeadzone.Inner == zones.Right && editor.RightDeadzone.Outer == 100 - zones.RightOuter, message);

    private static void Edit(ProfileCatalog catalog, ProfileEditSnapshot original, Action<ProfileEditor> change)
    {
        int saves = 0;
        var editor = new ProfileEditor(original.Profile, original.CanReplace, (name, mappings, deadzones) =>
        {
            saves++;
            catalog.SaveProfile(original, name, mappings, deadzones);
            return Task.CompletedTask;
        })
        {
            ShowActivated = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };

        Exception? failure = null;
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                change(editor);
                Click(editor.SaveProfile);
                Check(saves == 1, "The save action must write the whole profile once.");
                if (editor.DialogResult != true)
                    throw new InvalidDataException(editor.EditorMessage.Text);
            }
            catch (Exception ex) { failure = ex; editor.Close(); }
        });

        bool? result = editor.ShowDialog();
        if (failure != null) throw new InvalidOperationException("Profile editor interaction failed.", failure);
        Check(result == true, "Saving the combined draft must close the editor.");
    }

    private static void SelectA(ProfileEditor editor, PadBindings bindings)
    {
        var sources = bindings.Sources.OrderByDescending(source => source.Id.StartsWith("extra:", StringComparison.Ordinal)).ToArray();
        editor.InputList.SelectedIndex = Array.FindIndex(sources, source => source.Id == "A");
        Check(editor.SelectedName.Text == "A", "The editor must select A in the physical input list.");
    }

    private static void SelectTab(ProfileEditor editor, TabItem tab)
    {
        editor.ProfileTabs.SelectedItem = tab;
        editor.UpdateLayout();
    }

    private static void Click(Button button)
    {
        Check(button.IsEnabled && button.IsVisible, $"Disabled or hidden button: {button.Name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
