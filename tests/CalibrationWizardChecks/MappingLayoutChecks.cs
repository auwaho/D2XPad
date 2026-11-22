using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using D2XPad.Desktop;
using D2XPad.Live;

internal static class MappingLayoutChecks
{
    public static void Run()
    {
        CheckViewport(1.25, new Size(1000, 650));
        CheckViewport(1.50, new Size(960, 600));
        Console.WriteLine("Profile editor passed the 125% and 150% viewport checks for the compact header, square stick previews, independent mapping scrolling and pinned save controls.");
    }

    private static void CheckViewport(double scale, Size pixels)
    {
        var actions = PadBindings.ActionTargets.Where(target => target.IsAnalog)
            .Select(target => new XboxAction(target.Id, target.Id == "LT" ? 92 : 50)).ToArray();
        var profile = new ControllerProfile("Viewport fixture", PadBindings.CreateMappings(DeviceLayout.Bluetooth85HF,
            [new(PadBindings.ExtraId("PL"), actions)]));
        var editor = new ProfileEditor(profile, false, (_, _, _) => Task.CompletedTask);
        try
        {
            var pl = editor.InputList.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! ==
                PadBindings.ExtraId("PL"));
            editor.InputList.SelectedItem = pl;
            var root = (FrameworkElement)editor.Content;
            editor.Content = null;
            root.Resources.MergedDictionaries.Add(editor.Resources);
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            var viewport = new Size(pixels.Width / scale, pixels.Height / scale);
            root.Measure(viewport);
            root.Arrange(new Rect(viewport));
            root.UpdateLayout();
            Expect(Math.Abs(VisualTreeHelper.GetDpi(editor.SaveProfile).DpiScaleY - scale) < 0.01, "DPI did not reach the editor controls.");
            CheckHeader(editor, root, viewport.Width, $"{profile.Bindings.Layout.DisplayName}  ·  {profile.Name}");

            var left = Descendants(editor.InputList).OfType<ScrollViewer>().First();
            var right = editor.MappingScroll;
            double footer = Top(editor.EditorFooter, root);
            double save = Top(editor.SaveProfile, root);
            Expect(left.ScrollableHeight > 0 && right.ScrollableHeight > 0, "Both columns must be able to scroll.");
            Expect(save >= footer && save + editor.SaveProfile.ActualHeight <= viewport.Height &&
                Top(editor.ProfileName, root) + editor.ProfileName.ActualHeight <= viewport.Height &&
                Top(editor.CancelEditor, root) + editor.CancelEditor.ActualHeight <= viewport.Height,
                "Profile name, save and cancel must fit in the viewport.");
            Expect(Equals(editor.SaveProfile.Content, "Save copy and apply"), "The protected profile must show the copy action.");

            var placeholder = (TextBlock)editor.TargetPicker.Template.FindName("ValueText", editor.TargetPicker);
            Expect(editor.TargetPicker.SelectedIndex == -1 && placeholder.Text == "Select action..." && !editor.AddAction.IsEnabled,
                "An empty picker must present its placeholder and disable Add.");
            editor.TargetPicker.SelectedItem = editor.TargetPicker.Items.Cast<InputControl>().First(item => item.Id == "A");
            root.UpdateLayout();
            Expect(placeholder.Text == "A" && editor.AddAction.IsEnabled, "The picker must show an explicitly selected action by name.");

            var strength = Descendants(editor.ActionsList).OfType<Slider>()
                .Single(slider => AutomationProperties.GetAutomationId(slider) == "Strength_LT");
            var track = (Track)strength.Template.FindName("PART_Track", strength);
            Expect(strength.Value == 92 && track.DecreaseRepeatButton.ActualWidth > track.IncreaseRepeatButton.ActualWidth &&
                ReferenceEquals(track.DecreaseRepeatButton.Background, editor.FindResource("Accent")),
                "The mint fill must follow the actual Strength value.");
            Expect(Descendants(editor.ActionsList).OfType<TextBlock>().Any(text => text.Text == "92%"),
                "The Strength percentage must remain visible beside the slider.");

            left.ScrollToVerticalOffset(left.ScrollableHeight);
            root.UpdateLayout();
            Expect(left.VerticalOffset > 0 && right.VerticalOffset == 0, "Scrolling the input list must not move the editor pane.");
            double leftOffset = left.VerticalOffset;
            right.ScrollToVerticalOffset(right.ScrollableHeight);
            root.UpdateLayout();
            Expect(right.VerticalOffset > 0 && left.VerticalOffset == leftOffset && Top(editor.EditorFooter, root) == footer &&
                Top(editor.SaveProfile, root) == save, "Scrolling either pane must keep the footer and save controls fixed.");

            editor.ProfileTabs.SelectedItem = editor.SticksTab;
            root.UpdateLayout();
            CheckHeader(editor, root, viewport.Width, $"{profile.Bindings.Layout.DisplayName}  ·  {profile.Name}");
            Expect(Top(editor.LeftDeadzone, root) >= Top(editor.SticksTab, root) + editor.SticksTab.ActualHeight &&
                Top(editor.RightDeadzone, root) + editor.RightDeadzone.ActualHeight <= footer &&
                editor.LeftDeadzone.ActualWidth > 0 && editor.RightDeadzone.ActualWidth > 0 &&
                Top(editor.EditorFooter, root) == footer && Top(editor.SaveProfile, root) == save,
                "Both deadzone sliders and the shared footer must fit after switching tabs.");
            double sliderRight = editor.LeftDeadzone.TransformToAncestor(root).Transform(new Point(editor.LeftDeadzone.ActualWidth, 0)).X;
            foreach (var preview in new[] { editor.LeftStickPreview, editor.RightStickPreview })
            {
                Point position = preview.TransformToAncestor(root).Transform(new Point());
                Expect(preview.ActualWidth > 0 && Math.Abs(preview.ActualWidth - preview.ActualHeight) <= 1 &&
                    position.X >= sliderRight - 1 && position.X + preview.ActualWidth <= viewport.Width &&
                    position.Y + preview.ActualHeight <= footer,
                    "Each stick preview must stay square, to the right of the ranges and above the footer.");
            }
        }
        finally { editor.Close(); }
    }

    private static void CheckHeader(ProfileEditor editor, FrameworkElement root, double viewportWidth, string expectedCaption)
    {
        var caption = editor.ProfileTabs.Template.FindName("DeviceProfileCaption", editor.ProfileTabs) as TextBlock
            ?? throw new InvalidDataException("The device and profile caption is missing from the tab header.");
        Point captionOrigin = caption.TransformToAncestor(root).Transform(new Point());
        Point lastTabEdge = editor.SticksTab.TransformToAncestor(root).Transform(new Point(editor.SticksTab.ActualWidth, 0));
        Expect(caption.Text == expectedCaption && caption.ActualWidth > 0 && captionOrigin.X >= lastTabEdge.X - 1 &&
            captionOrigin.X + caption.ActualWidth <= viewportWidth && captionOrigin.Y >= lastTabEdge.Y - 1 &&
            captionOrigin.Y + caption.ActualHeight <= lastTabEdge.Y + editor.SticksTab.ActualHeight + 1,
            "The device and profile caption must fit to the right of the tabs in the same header row.");
        Expect(editor.Title == "Profile editor" && !Descendants(root).OfType<TextBlock>().Any(text => text.Text == editor.Title),
            "The editor title must appear only in the native window title bar.");
    }

    private static double Top(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).Transform(new Point()).Y;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
