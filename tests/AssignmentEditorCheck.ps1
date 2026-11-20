# Helpers for Test-Desktop.ps1. Operates only on a uniquely named test profile.
function Open-AssignmentEditor($window) {
    (Find-Control $window 'EditButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $filter = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BindingEditor'),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $window.Current.ProcessId))
    Wait-For {
        $editor = $window.FindFirst($descendants, $filter)
        if ($editor) { return $editor }
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $filter)
    } 'The assignment editor did not open.'
}

function Invoke-EditorButton($editor, [string]$id) {
    $button = Find-Control $editor $id
    if (!$button -or !$button.Current.IsEnabled) { throw "Unavailable editor action: $id." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-EditorInput($editor, [string]$source, [string]$label) {
    $item = Find-Control $editor ("Input_" + $source)
    if (!$item -or $item.Current.Name -cne $label) { throw "Missing physical input $label." }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { (Find-Control $editor 'SelectedName').Current.Name -ceq $label } "The editor did not select $label." | Out-Null
}

function Add-EditorAction($editor, [string]$name) {
    $combo = Find-Control $editor 'ActionTarget'
    if ((Find-Control $editor 'AddAction').Current.IsEnabled) { throw 'Add requires a fresh Xbox action selection.' }
    $dropdown = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $dropdown.Expand()
    $filter = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $item = Wait-For { $combo.FindFirst($descendants, $filter) } "Missing Xbox action $name."
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($dropdown.Current.ExpandCollapseState -eq 'Expanded') { $dropdown.Collapse() }
    Wait-For { (Find-Control $editor 'AddAction').Current.IsEnabled } 'The selected Xbox action did not enable Add.' | Out-Null
    Invoke-EditorButton $editor 'AddAction'
    Wait-For { !(Find-Control $editor 'AddAction').Current.IsEnabled } 'Adding an Xbox action did not clear its selection.' | Out-Null
}

function Test-AssignmentEditor($window, $original, [switch]$Offline) {
    $name = 'D2XPad-test-' + [guid]::NewGuid().ToString('N')
    $dir = if ($env:D2XPAD_PROFILE_DIR) { $env:D2XPAD_PROFILE_DIR } else { Join-Path (Split-Path $app) 'profiles' }
    $path = Join-Path $dir ($name + '.json')
    $backup = Join-Path $dir ($name + '.bak')
    if ((Test-Path -LiteralPath $path) -or (Test-Path -LiteralPath $backup)) { throw 'Test profile already exists.' }
    $editor = $null
    try {
        Select-Profile $window 'default' -Offline:$Offline
        if (!$Offline) { $previous = Read-Session }
        foreach ($pass in 1,2) {
            $editor = Open-AssignmentEditor $window
            (Find-Control $editor 'ProfileName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
            Select-EditorInput $editor 'A' 'A'
            if ($pass -eq 1) {
                Invoke-EditorButton $editor 'ClearSource'
                Wait-For { (Find-Control $editor 'ClearSource').Current.Name -like 'Undo clear*' } 'Clearing A did not offer undo.' | Out-Null
                Invoke-EditorButton $editor 'ClearSource'
                Wait-For { (Find-Control $editor 'ClearSource').Current.Name -like 'Clear actions*' } 'Undo did not restore the clear action.' | Out-Null
                Invoke-EditorButton $editor 'ClearSource'
                Wait-For { (Find-Control $editor 'ResetSource').Current.IsEnabled } 'Clearing A did not enable restoring its saved action.' | Out-Null
                Invoke-EditorButton $editor 'ResetSource'
                Wait-For { (Find-Control $editor 'ClearSource').Current.IsEnabled } 'The saved A action did not return.' | Out-Null
                Invoke-EditorButton $editor 'ClearSource'
            } else {
                if ((Find-Control $editor 'ClearSource').Current.IsEnabled) { throw 'Disabled A did not survive reopening.' }
                if ((Find-Control $editor 'ResetSource').Current.IsEnabled) { throw 'An unchanged input offered restoring its saved actions.' }
                Invoke-EditorButton $editor 'DefaultSource'
            }
            Select-EditorInput $editor 'extra:PL' 'PL'
            if ($pass -eq 1) {
                Invoke-EditorButton $editor 'ClearSource'
                Add-EditorAction $editor 'RT'
                Add-EditorAction $editor 'Left stick up'
                $slider = Wait-For { Find-Control $editor 'Strength_LeftStickUp' } 'Missing stick strength control.'
                $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(75)
                $summary = 'RT 100% + Left stick up 75%'
            } else {
                $slider = Find-Control $editor 'Strength_LeftStickUp'
                if (!$slider -or $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 75) {
                    throw 'The stick strength did not survive reopening.'
                }
                Invoke-EditorButton $editor 'Remove_LeftStickUp'
                Invoke-EditorButton $editor 'Remove_RT'
                Add-EditorAction $editor 'X'
                $summary = 'X'
            }
            Invoke-EditorButton $editor 'SaveMappings'
            if (!$Offline) {
                $confirmed = Wait-For {
                    $state = Read-Session
                    if ($state.Profile -eq $name -and $state.ProfileRevision -gt $previous.ProfileRevision) { return $state }
                } 'The live converter did not acknowledge the edit.'
                if ($confirmed.SessionId -ne $original.SessionId -or $confirmed.Slot -ne $original.Slot) { throw 'Applying the edit replaced the virtual controller.' }
                $previous = $confirmed
            }
            Wait-For { (Find-Control $window 'PL').Current.Name -ceq "PL → $summary" } 'Tester did not refresh the physical mapping.' | Out-Null
            $editor = $null
            $saved = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $paddle = $saved.mappings | Where-Object source -ceq 'extra:PL'
            $standard = $saved.mappings | Where-Object source -ceq 'A'
            if ($saved.schemaVersion -ne 3 -or !$paddle) { throw 'Missing version 3 mapping.' }
            if ($pass -eq 1) {
                if (($paddle.actions.target -join ',') -cne 'RT,LeftStickUp' -or $paddle.actions[1].strength -ne 75 -or !$standard -or $standard.actions.Count -ne 0) {
                    throw 'The saved combination or disabled standard source is incorrect.'
                }
            } elseif ($paddle.actions.Count -ne 1 -or $paddle.actions[0].target -cne 'X' -or $standard) { throw 'The saved reset or replacement action is incorrect.' }
            if (!$Offline -and !(Find-Control $window 'OutputSwitch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
                throw 'Save did not open the Xbox output tester.'
            }
        }
        if ($Offline) { return [pscustomobject]@{Source='PL'; SavedAs='RT + Left stick up 75%'; ReopenedAndUpdatedTo='X'; StandardReset=$true} }
        return [pscustomobject]@{ Source='PL'; SavedAs='RT + Left stick up 75%'; UpdatedTo='X'; StandardReset=$true; SessionPreserved=$true; Slot=$previous.Slot; LastRevision=$previous.ProfileRevision }
    } finally {
        if ($editor) {
            try { $editor.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch [System.Windows.Automation.ElementNotAvailableException] { }
        }
        Select-Profile $window $original.Profile -Offline:$Offline
        foreach ($fixture in $path,$backup) {
            if ((Test-Path -LiteralPath $fixture) -and (Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json).name -ceq $name) {
                Remove-Item -LiteralPath $fixture
            }
        }
    }
}
