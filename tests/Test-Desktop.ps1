param(
    [string]$AppPath = (Join-Path $PSScriptRoot '../artifacts/Desktop/D2XPad.exe'),
    [switch]$CheckRestart,
    [switch]$CheckEditor,
    [switch]$EditorOffline
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$app = (Resolve-Path $AppPath).Path
$live = Join-Path (Split-Path $app) 'D2XPad.Live.exe'
$descendants = [System.Windows.Automation.TreeScope]::Descendants

function Wait-For($probe, [string]$message, [int]$seconds = 15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt $seconds)
    throw $message
}

function Find-Control($element, [string]$id) {
    $filter = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $element.FindFirst($descendants, $filter)
}

function Read-Session {
    $json = & $live status --json 2>$null
    if ($LASTEXITCODE -eq 0) { return $json | ConvertFrom-Json }
}

function Select-Profile($element, [string]$name, [switch]$Offline) {
    $combo = Find-Control $element 'ProfilePicker'
    Wait-For { $combo.Current.IsEnabled } 'The profile picker is busy.' | Out-Null
    $dropdown = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($dropdown.Current.ExpandCollapseState -ne 'Expanded') { $dropdown.Expand() }
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $item = Wait-For { $combo.FindFirst($descendants, $condition) } "The profile picker does not offer $name."
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if (!$Offline) { Wait-For { (Read-Session).Profile -eq $name } "The engine did not apply $name." | Out-Null }
    Wait-For { $combo.Current.IsEnabled } 'The profile picker did not return to its ready state.' | Out-Null
    if ($dropdown.Current.ExpandCollapseState -eq 'Expanded') { $dropdown.Collapse() }
}

# Run on the interactive desktop with a Bluetooth 85HF. Leave the verified session available for play.
Start-Process -FilePath $app
$desktop = Wait-For {
    Get-Process -Name 'D2XPad' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app -and $_.MainWindowTitle -eq 'D2XPad' } | Select-Object -First 1
} 'The WPF window did not appear.'
$window = [System.Windows.Automation.AutomationElement]::FromHandle($desktop.MainWindowHandle)
if ($EditorOffline) {
    . (Join-Path $PSScriptRoot 'AssignmentEditorCheck.ps1')
    $picker = Wait-For {
        $element = Find-Control $window 'ProfilePicker'
        if ($element -and $element.Current.IsEnabled) { return $element }
    } 'The profile picker did not initialize.'
    if ((Find-Control $window 'PowerButton').Current.Name -ne 'Start conversion') { throw 'Offline check requires a stopped converter.' }
    $selected = $picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
    $checked = Test-AssignmentEditor $window ([pscustomobject]@{Profile=$selected}) -Offline
    [pscustomobject]@{Outcome='Passed'; Mode='Stopped'; Editor=$checked; Restored=$selected} | ConvertTo-Json
    return
}
$power = Wait-For {
    $button = Find-Control $window 'PowerButton'
    if ($button -and $button.Current.IsEnabled) { return $button }
} 'No converter action is available. Connect the Bluetooth 85HF.'
if ($power.Current.Name -eq 'Start conversion') {
    $power.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $power.Current.Name -eq 'Stop conversion' -and $power.Current.IsEnabled } 'The converter did not start from WPF.' 120 | Out-Null
}
$original = Read-Session
if (!$original.Connected) { throw 'The 85HF is not connected.' }
if ($CheckRestart) {
    $power.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $power.Current.Name -eq 'Start conversion' -and $power.Current.IsEnabled } 'The converter did not stop from WPF.' 60 | Out-Null
    $power.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $power.Current.Name -eq 'Stop conversion' -and $power.Current.IsEnabled } 'The converter did not restart from WPF.' 120 | Out-Null
    $restarted = Read-Session
    if (!$restarted.Connected -or $restarted.SessionId -eq $original.SessionId -or $restarted.Profile -ne $original.Profile) {
        throw 'The restart did not create a fresh session with the selected profile.'
    }
    $original = $restarted
}
$alternate = if ($original.Profile -eq 'southpaw-buttons') { 'default' } else { 'southpaw-buttons' }
$editorResult = $null
try {
    if ($CheckEditor) {
        . (Join-Path $PSScriptRoot 'AssignmentEditorCheck.ps1')
        $editorResult = Test-AssignmentEditor $window $original
    }
    Select-Profile $window $alternate
    $switched = Read-Session
    if ($switched.SessionId -ne $original.SessionId -or $switched.Slot -ne $original.Slot) { throw 'Switching profiles replaced the controller.' }
} finally {
    Select-Profile $window $original.Profile
}

$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
Wait-For { $desktop.Refresh(); $desktop.MainWindowHandle -eq 0 } 'The window did not hide.' | Out-Null
if ((Read-Session).SessionId -ne $original.SessionId) { throw 'Closing the window ended the session.' }
Start-Process -FilePath $app
Wait-For { $desktop.Refresh(); $desktop.MainWindowHandle -ne 0 } 'A second launch did not reopen the original window.' | Out-Null
$window = [System.Windows.Automation.AutomationElement]::FromHandle($desktop.MainWindowHandle)
foreach ($id in 'PhysicalSwitch', 'OutputSwitch') {
    (Find-Control $window $id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    foreach ($indicator in 'LeftTrigger','RightTrigger','LeftStickValues','RightStickValues','L4','R4','PL','PR') {
        if (!(Find-Control $window $indicator)) { throw "Missing tester indicator: $indicator" }
    }
}
$final = Read-Session
if ($final.SessionId -ne $original.SessionId -or $final.Slot -ne $original.Slot -or $final.Profile -ne $original.Profile) {
    throw 'The session, slot or original profile changed after reopening.'
}
[pscustomobject]@{ Outcome='Passed'; RestartChecked=[bool]$CheckRestart; Editor=$editorResult; DesktopProcess=$desktop.Id; Profile=$final.Profile; Slot=$final.Slot; Session=$final.SessionId; PhysicalReports=$final.Received } | ConvertTo-Json
