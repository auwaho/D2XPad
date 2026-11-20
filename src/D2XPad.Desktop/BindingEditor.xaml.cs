using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using D2XPad.Live;

namespace D2XPad.Desktop;

public partial class BindingEditor : Window
{
    private sealed class ActionRow(InputControl target, int strength, Action changed) : INotifyPropertyChanged
    {
        public InputControl Target { get; } = target;
        public string Name => Target.Name;
        public bool IsAnalog => Target.IsAnalog;
        public string RemoveId => $"Remove_{Target.Id}";
        public string RemoveLabel => $"Remove {Name}";
        public string StrengthId => $"Strength_{Target.Id}";
        public string StrengthLabel => $"{Name} strength";
        public int Strength
        {
            get => strength;
            set
            {
                if (strength == value) return;
                strength = value;
                PropertyChanged?.Invoke(this, new(nameof(Strength)));
                changed();
            }
        }
        public XboxAction Export() => new(Target.Id, Strength);
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class PhysicalInput(InputControl source, bool isExtra, IReadOnlyList<XboxAction> savedActions) : INotifyPropertyChanged
    {
        public string Id => source.Id;
        public string Name => source.Name;
        public bool IsExtra => isExtra;
        public bool IsAnalog => source.IsAnalog;
        public string AutomationId => $"Input_{Id}";
        public string Summary => MappingText.Describe(Export());
        public ObservableCollection<ActionRow> Actions { get; } = [];
        public XboxAction[]? ClearedActions { get; set; }
        public IReadOnlyList<XboxAction> SavedActions { get; } = savedActions;
        public IReadOnlyList<XboxAction> DefaultActions { get; } = PadBindings.DefaultActionsFor(source.Id);
        public XboxAction[] Export() => Actions.Select(action => action.Export()).ToArray();
        public bool IsDefault => Matches(DefaultActions);
        public bool IsModified => !Matches(SavedActions);
        public bool HasDefaultAlternative => !IsExtra && !SavedActions.SequenceEqual(DefaultActions);

        private bool Matches(IReadOnlyList<XboxAction> expected) => Actions.Count == expected.Count &&
            Actions.All(row => expected.Any(action => action.Target == row.Target.Id && action.Strength == row.Strength));

        public void NotifyChanged()
        {
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
            PropertyChanged?.Invoke(this, new(nameof(IsModified)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ControllerProfile original;
    private readonly string profileCaption;
    private readonly string initialProfileName;
    private readonly bool canReplace;
    private readonly Func<string, IReadOnlyList<InputMapping>, bool, Task> saveAndApply;
    private readonly PhysicalInput[] inputs = [];
    private PhysicalInput? Selected => InputList.SelectedItem as PhysicalInput;
    private bool saving;

    internal BindingEditor(ControllerProfile profile, bool canReplace,
        Func<string, IReadOnlyList<InputMapping>, bool, Task> saveAndApply)
    {
        original = profile;
        this.canReplace = canReplace;
        this.saveAndApply = saveAndApply;
        var bindings = profile.Bindings;
        var layout = bindings.Layout;
        bool learnedDefault = layout.Id != DeviceLayout.Bluetooth85HF.Id && profile.Name == layout.DefaultProfileName;
        profileCaption = learnedDefault ? $"{layout.DisplayName} · default" : profile.Name;
        initialProfileName = canReplace ? profile.Name : SuggestCopyName(learnedDefault ? layout.DisplayName : profile.Name);
        InitializeComponent();
        WindowCaption.UseDark(this);
        KeyboardFocusCues.Track(this);
        var extras = bindings.Layout.ExtraSources.Select(s => PadBindings.ExtraId(s.Id)).ToHashSet();
        inputs = bindings.Sources.Select(source => new PhysicalInput(source, extras.Contains(source.Id), bindings.GetActions(source.Id)))
            .OrderByDescending(input => input.IsExtra).ToArray();
        foreach (var input in inputs)
            foreach (var action in input.SavedActions)
                input.Actions.Add(NewAction(input, action));
        ProfileName.Text = initialProfileName;
        InputList.ItemsSource = inputs;
        InputList.SelectedIndex = inputs.Length == 0 ? -1 : 0;
        DeviceName.Text = learnedDefault ? profileCaption : $"{layout.DisplayName}  ·  {profile.Name}";
        UpdateProfileHint();
        RefreshActions();
    }

    private static string SuggestCopyName(string label)
    {
        var delimiters = Path.GetInvalidFileNameChars().Append('.').ToArray();
        string stem = string.Join(" ", label.Split(delimiters, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (stem.Length == 0) stem = "Controller";
        return stem[..Math.Min(stem.Length, 243)].TrimEnd() + " custom";
    }

    private ActionRow NewAction(PhysicalInput input, XboxAction action) =>
        new(MappingText.Targets[action.Target], action.Strength, () =>
        {
            if (Selected == input) RefreshSourceButtons(input);
            UpdatePendingChanges();
            input.NotifyChanged();
        });

    private void InputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionsList == null) return;
        RefreshActions(resetTarget: true);
        MappingScroll?.ScrollToTop();
    }

    private void RefreshActions(bool resetTarget = false)
    {
        var input = Selected;
        ActionsList.ItemsSource = input?.Actions;
        SelectedName.Text = input?.Name ?? "No physical inputs";
        SourceType.Text = input == null ? "" : input.IsExtra ? "Extra button" : input.IsAnalog ? "Analog input" : "Standard button";
        var selectedTarget = resetTarget ? null : TargetPicker.SelectedItem as InputControl;
        var available = PadBindings.ActionTargets.Where(target =>
            input != null && input.Actions.All(action => action.Target.Id != target.Id)).ToArray();
        TargetPicker.ItemsSource = available;
        TargetPicker.SelectedItem = available.FirstOrDefault(target => target.Id == selectedTarget?.Id);
        TargetPicker.IsEnabled = available.Length > 0;
        UpdateAddAction();
        RefreshSourceButtons(input);
        EmptyActions.Visibility = input?.Actions.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        input?.NotifyChanged();
        UpdatePendingChanges();
    }

    private void RefreshSourceButtons(PhysicalInput? input)
    {
        bool canUndoClear = input?.ClearedActions != null;
        string clearLabel = canUndoClear ? "Undo clear" : "Clear actions";
        ClearSource.IsEnabled = canUndoClear || input?.Actions.Count > 0;
        ClearSource.Content = clearLabel;
        ResetSource.IsEnabled = input?.IsModified == true;
        DefaultSource.Visibility = input?.HasDefaultAlternative == true ? Visibility.Visible : Visibility.Collapsed;
        DefaultSource.IsEnabled = input != null && !input.IsDefault;
        ClearSource.ToolTip = input == null ? null : canUndoClear
            ? $"Restore the Xbox actions for {input.Name} from before the last clear."
            : $"Clear all Xbox actions for {input.Name}. Other inputs keep their actions.";
        ResetSource.ToolTip = input == null ? null : $"Restore the actions for {input.Name} from the opened profile.";
        DefaultSource.ToolTip = input == null ? null : $"Use the built-in Xbox action for {input.Name}.";
        AutomationProperties.SetName(ClearSource, input == null ? clearLabel : $"{clearLabel} for {input.Name}");
        AutomationProperties.SetName(ResetSource, input == null ? "Restore saved actions" : $"Restore saved actions for {input.Name}");
        AutomationProperties.SetName(DefaultSource, input == null ? "Use default action" : $"Use default action for {input.Name}");
    }

    private bool CanAddAction(PhysicalInput input, InputControl target) =>
        !saving && input.Actions.All(action => action.Target.Id != target.Id);

    private void TargetChanged(object sender, SelectionChangedEventArgs e) => UpdateAddAction();

    private void UpdateAddAction()
    {
        if (AddAction != null)
            AddAction.IsEnabled = Selected is { } input && TargetPicker.SelectedItem is InputControl target && CanAddAction(input, target);
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } input || TargetPicker.SelectedItem is not InputControl target || !CanAddAction(input, target)) return;
        input.ClearedActions = null;
        input.Actions.Add(NewAction(input, new(target.Id)));
        RefreshActions();
    }

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } input || sender is not Button { DataContext: ActionRow action }) return;
        input.Actions.Remove(action);
        RefreshActions();
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } input) ReplaceActions(input, input.SavedActions);
    }

    private void DefaultClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { HasDefaultAlternative: true } input) ReplaceActions(input, input.DefaultActions);
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } input) return;
        if (input.ClearedActions is { } actions) ReplaceActions(input, actions);
        else if (input.Actions.Count > 0) ReplaceActions(input, [], clearedActions: input.Export());
    }

    private void ReplaceActions(PhysicalInput input, IReadOnlyList<XboxAction> actions, XboxAction[]? clearedActions = null)
    {
        input.ClearedActions = clearedActions;
        input.Actions.Clear();
        foreach (var action in actions) input.Actions.Add(NewAction(input, action));
        RefreshActions();
    }

    private bool UpdatingOriginal => canReplace && string.Equals(ProfileName.Text, original.Name, StringComparison.OrdinalIgnoreCase);

    private void ProfileNameChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveMappings != null) UpdateProfileHint();
        UpdatePendingChanges();
    }

    private void UpdatePendingChanges()
    {
        if (PendingChanges == null || ProfileName == null) return;
        var comparison = canReplace ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool modified = !string.Equals(ProfileName.Text, initialProfileName, comparison) ||
            inputs.Any(input => input.IsModified);
        PendingChanges.Visibility = modified ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateProfileHint()
    {
        SaveMappings.Content = UpdatingOriginal ? "Save and apply" : "Save copy and apply";
        Message(UpdatingOriginal ? "Change the profile name to save a separate copy." : $"Save a new profile based on {profileCaption}.");
    }

    private void Message(string text, bool error = false)
    {
        EditorMessage.Text = text;
        EditorMessage.Foreground = (Brush)FindResource(error ? "Error" : "Secondary");
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        string name = ProfileName.Text;
        if (string.IsNullOrWhiteSpace(name) || (!canReplace && string.Equals(name, original.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Message("Enter a new profile name to save these mappings.", error: true);
            ProfileName.Focus();
            return;
        }
        saving = true;
        Fields.IsEnabled = ProfileName.IsEnabled = SaveMappings.IsEnabled = CancelEditor.IsEnabled = false;
        Message("Saving profile…");
        try
        {
            var mappings = inputs.Where(input => !input.IsDefault)
                .Select(input => new InputMapping(input.Id, input.Export())).ToArray();
            await saveAndApply(name, mappings, UpdatingOriginal);
            saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { Message(ex.Message, error: true); }
        finally
        {
            saving = false;
            Fields.IsEnabled = ProfileName.IsEnabled = SaveMappings.IsEnabled = CancelEditor.IsEnabled = true;
            UpdateAddAction();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (saving) e.Cancel = true;
        base.OnClosing(e);
    }
}
