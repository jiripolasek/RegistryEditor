param(
    [string] $Executable,
    [string] $TestDataDirectory,
    [guid] $SandboxRun = [guid]::Empty,
    [string] $TestFilter,
    [string] $ExpectedAppSid,
    [string] $ExpectedPreviewPrinter,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/investigations-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
if ($SandboxRun -eq [guid]::Empty) {
    Assert-UiTestBuild $Executable
} else { Assert-ShippingUiBuild $Executable }
Assert-UiTestPath $ArtifactDirectory
$dataFolder = New-UiTestProfile $TestDataDirectory $SandboxRun
$Executable = [IO.Path]::GetFullPath($Executable)
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$fixtureName = '!Regedit.InvestigationsUi.' + [guid]::NewGuid().ToString('N')
$subKey = "Software\$fixtureName"
$address = "HKEY_CURRENT_USER\$subKey"
$settingsFile = Join-Path $dataFolder 'settings.json'
$appProcess = $null
$mainWindow = 0
. (Join-Path $PSScriptRoot 'UiTestWindow.ps1')

Add-Type -AssemblyName UIAutomationTypes, UIAutomationClient
$uiaReferences = @(
    [System.Windows.Automation.AutomationElement].Assembly.Location,
    [System.Windows.Automation.AutomationEventArgs].Assembly.Location,
    'System.Runtime.dll', 'System.Collections.dll', 'System.Threading.dll'
)
Add-Type -ReferencedAssemblies $uiaReferences -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

public sealed record UxNotification(int Kind, int Processing, string Text, string Activity);

public static class UxTestAccessibility {
    public static bool SetByteAndObserveValue(AutomationElement editor, string value) {
        using var changed = new System.Threading.ManualResetEventSlim();
        AutomationPropertyChangedEventHandler handler = (_, args) => {
            if (Equals(args.NewValue, value)) changed.Set();
        };
        Automation.AddAutomationPropertyChangedEventHandler(editor, TreeScope.Element, handler, ValuePattern.ValueProperty);
        try {
            ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
            return changed.Wait(TimeSpan.FromSeconds(5));
        } finally {
            Automation.RemoveAutomationPropertyChangedEventHandler(editor, handler);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr target, uint message, UIntPtr wParam,
        string value, uint flags, uint timeout, out UIntPtr result);
    public static void BroadcastTextScaleChange() {
        UIntPtr ignored;
        SendMessageTimeoutW(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "Accessibility", 2, 1000, out ignored);
    }
}

public sealed class UxNotificationObserver : IDisposable {
    private readonly AutomationElement _root;
    private readonly AutomationEventHandler _handler;
    private readonly List<UxNotification> _events = new();
    public UxNotification[] Events { get { lock (_events) return _events.ToArray(); } }
    public bool CancelNextExport { get; set; }
    public volatile bool CancelWasInvoked;
    public string CancelError { get; private set; } = string.Empty;

    public UxNotificationObserver(IntPtr window) {
        _root = AutomationElement.FromHandle(window);
        _handler = OnNotification;
        Automation.AddAutomationEventHandler(AutomationElement.NotificationEvent, _root, TreeScope.Subtree, _handler);
    }

    private void OnNotification(object sender, AutomationEventArgs args) {
        if (args is NotificationEventArgs notice) {
            lock (_events) _events.Add(new((int)notice.NotificationKind, (int)notice.NotificationProcessing,
                notice.DisplayString, notice.ActivityId));
            if (CancelNextExport && notice.DisplayString.StartsWith("Exporting", StringComparison.Ordinal)) {
                CancelNextExport = false;
                try {
                    var card = sender as AutomationElement ?? _root;
                    var buttons = card.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Cancel"));
                    foreach (AutomationElement button in buttons) {
                        if (!button.Current.AutomationId.StartsWith("NotificationAction_", StringComparison.Ordinal)) continue;
                        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        CancelWasInvoked = true;
                        return;
                    }
                    CancelError = "The progress toast has no Cancel action.";
                } catch (Exception error) { CancelError = error.Message; }
            }
        }
    }

    public void Dispose() {
        Automation.RemoveAutomationEventHandler(AutomationElement.NotificationEvent, _root, _handler);
    }
}
'@

function Invoke-WinAppChecked([string[]] $Arguments) {
    $output = @(& winapp @Arguments --on local 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' '): $($output -join [Environment]::NewLine)" }
    $output
}
function Ui([string[]] $Arguments, [long] $Window = $script:mainWindow) {
    $targetIndex = [Array]::IndexOf($Arguments, '--target')
    if ($Arguments[0] -eq 'send-keys' -and $targetIndex -ge 0) {
        if ($Window -eq $mainWindow) { Set-TestWindowForeground }
        Invoke-WinAppChecked @('ui', 'focus', $Arguments[$targetIndex + 1], '-w', "$Window") | Out-Null
    }
    Invoke-WinAppChecked (@('ui') + $Arguments + @('-w', "$Window"))
}
function Elements($Nodes) { foreach ($node in $Nodes) { $node; if ($node.children) { Elements $node.children } } }
function Read-Ui([long] $Window = $script:mainWindow, [switch] $All) {
    $arguments = @('inspect', '-d', '14', '--json')
    if (-not $All) { $arguments += '--hide-offscreen' }
    for ($attempt = 0; ; $attempt++) {
        try { $json = Ui $arguments -Window $Window; break }
        catch {
            if ($_ -notmatch 'stale_element' -or $attempt -eq 2) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    $tree = $json | ConvertFrom-Json
    @(Elements $tree.windows[0].elements)
}
function Assert-AppSid([int] $ProcessId) {
    if ($ExpectedAppSid) {
        $owner = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $ProcessId) | Invoke-CimMethod -MethodName GetOwnerSid
        if ($owner.Sid -ne $ExpectedAppSid) { throw 'The test editor process does not belong to the verified disposable SID.' }
    }
}
function Wait-Ui([scriptblock] $Check, [switch] $All, [long] $Window = $script:mainWindow) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Check @(Read-Ui -Window $Window -All:$All)) { return }; Start-Sleep -Milliseconds 100 } while ($timer.Elapsed.TotalSeconds -lt 20)
    throw 'Expected UI state did not appear.'
}
function Assert-Clipboard([string] $Expected, [string] $EvidenceName, [string] $Sentinel = '') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0; $actual = ''; $actualType = 'null'; $readError = $null; $exact = $false
    do {
        $attempts++
        try {
            $observed = Get-Clipboard -Raw
            $actualType = if ($null -eq $observed) { 'null' } else { $observed.GetType().FullName }
            $actual = [string]$observed
            $readError = $null
            $exact = $observed -is [string] -and [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)
        } catch { $readError = $_.Exception.Message; $exact = $false }
        if ($exact) { break }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 10)
    $evidence = Join-Path $ArtifactDirectory ($EvidenceName + '-clipboard.json')
    if ($EvidenceName -eq 'large-value' -or -not $exact) {
        @{
            expectedLength = $Expected.Length; actualLength = $actual.Length; actualType = $actualType; matches = $exact
            isSentinel = $Sentinel.Length -gt 0 -and $actual -ceq $Sentinel
            attempts = $attempts; elapsedMilliseconds = $watch.ElapsedMilliseconds; readError = $readError
        } | ConvertTo-Json | Set-Content -LiteralPath $evidence -Encoding utf8
    }
    if (-not $exact) { throw "Clipboard mismatch for $EvidenceName after $attempts reads: expected $($Expected.Length) characters, observed $actualType with $($actual.Length) characters. See $evidence." }
}
function Assert-KeyboardReadout([string] $AutomationId, [string] $Expected, [string] $EvidenceName) {
    for ($step = 0; $step -lt 40; $step++) {
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if ($focused.hasFocus -and $focused.element.automationId -eq $AutomationId) { break }
        Ui @('send-keys', 'tab', '--via', 'send-input') | Out-Null
    }
    if (-not $focused.hasFocus -or $focused.element.automationId -ne $AutomationId) { throw "Tab did not reach $AutomationId." }
    Set-Clipboard -Value 'keyboard target copy sentinel'
    Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
    Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
    Assert-Clipboard $Expected $EvidenceName 'keyboard target copy sentinel'
}
function Pick([string] $Type, [string] $Name, [long] $Window = $script:mainWindow, [switch] $All) {
    $items = @(Read-Ui -Window $Window -All:$All | Where-Object { $_.type -eq $Type -and $_.name -eq $Name })
    if ($items.Count -ne 1) { throw "Expected one $Type named '$Name', found $($items.Count)." }
    $items[0]
}
function Assert-FocusedPane([string] $Name) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $chain = @("Initial: $($focused.Current.ProcessId):$($focused.Current.Name)")
        $matched = $false
        while ($focused -and $focused.Current.ProcessId -in 0, $appProcess.Id) {
            $chain += $focused.Current.Name
            if ($focused.Current.Name -eq $Name) { $matched = $true }
            if ($focused.Current.NativeWindowHandle -eq $mainWindow) {
                if ($matched) { return }
                break
            }
            $focused = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($focused)
        }
        Start-Sleep -Milliseconds 50
    } while ($watch.Elapsed.TotalSeconds -lt 5)
    throw "Keyboard focus did not reach the '$Name' pane. Foreground: $([UxTestWindow]::GetForegroundWindow()), expected $mainWindow. Actual: $($chain -join ' > ')"
}
function Press([string] $Name, [string] $CommandBarId = '', [string] $CommandBarChildId = '') {
    $items = @(Read-Ui)
    if ($CommandBarId) {
        $bars = @($items | Where-Object { $_.type -eq 'AppBar' -and $_.automationId -eq $CommandBarId -and
            (-not $CommandBarChildId -or $_.children.automationId -contains $CommandBarChildId) })
        if ($bars.Count -ne 1) { throw "Expected one command bar '$CommandBarId', found $($bars.Count)." }
        $items = @(Elements $bars[0].children)
    }
    $buttons = @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq $Name })
    if ($buttons.Count -eq 1) { Ui @('invoke', $buttons[0].selector, '--action', 'invoke') | Out-Null; return }
    $more = @($items | Where-Object automationId -eq 'MoreButton')
    if ($more.Count -ne 1) { throw "Expected one overflow for command '$Name', found $($more.Count). Specify its command bar." }
    Ui @('invoke', $more[0].selector, '--action', 'invoke') | Out-Null
    $menu = Read-ContextMenu -Expected $Name -Types @('Button', 'MenuItem')
    $command = @($menu.Items | Where-Object name -eq $Name)
    if ($command.Count -ne 1) { throw "Expected one command '$Name', found $($command.Count)." }
    Ui @('invoke', $command[0].selector, '--action', 'invoke') -Window $menu.Window | Out-Null
}
function Tool-Option([string] $Name) {
    $browserToolbar = (Read-Ui).automationId -contains 'QuickSearch'
    Press 'Tools'
    if ($Name -eq 'Right sidebar' -and -not $browserToolbar) { return }
    $menu = if ($browserToolbar) { Read-ToolbarTools } else { Read-ContextMenu $Name }
    $item = @($menu.Items | Where-Object name -eq $Name)
    if ($item.Count -ne 1) { throw "Expected one Tools option named '$Name'." }
    Ui @('invoke', $item[0].selector) -Window $menu.Window | Out-Null
}
function Start-WatchPreview([string] $ExpectedAddress, [string] $ExpectedValue = $null) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $candidates = @($script:mainWindow) + @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | ForEach-Object hwnd)
        foreach ($window in $candidates) {
            $items = @(Read-Ui -Window $window)
            $start = @($items | Where-Object automationId -eq 'WatchProposalStart')
            if ($start.Count -ne 1) { continue }
            $target = @($items | Where-Object automationId -eq 'WatchProposalTarget')
            $text = ($target | ForEach-Object { $_.name + ' ' + $_.value }) -join ' '
            $kind = if ($ExpectedValue) { ' · Value: ' + $ExpectedValue } else { ' · Branch' }
            if (-not $text.Contains($ExpectedAddress) -or -not $text.Contains($env:COMPUTERNAME) -or $text -notmatch ' · (32|64)-bit · ' -or -not $text.Contains($kind)) {
                throw "Watch proposal did not show the captured local key, view and expected target '$kind': $text"
            }
            Ui @('invoke', $start[0].selector, '--action', 'invoke') -Window $window | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
            Press 'Open panel tab in main tab'
            return
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 10)
    throw 'The captured watch proposal did not appear before Start.'
}
function Confirm-WorkspacePreview([switch] $Start, [switch] $Replace) {
    Wait-Ui { param($items) $items.name -contains 'Open workspace' -and $items.automationId -contains 'WorkspaceReplaceTabs' -and $items.automationId -contains 'WorkspaceStartInvestigations' }
    foreach ($id in 'WorkspaceReplaceTabs', 'WorkspaceStartInvestigations') {
        $state = Ui @('get-property', $id, '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?Off$|^0$') { throw "Workspace preview did not default to Add with no automatic Start: $id" }
    }
    if ($Replace) { Ui @('invoke', 'WorkspaceReplaceTabs', '--action', 'toggle-on') | Out-Null }
    if ($Start) { Ui @('invoke', 'WorkspaceStartInvestigations', '--action', 'toggle-on') | Out-Null }
    Primary
    Wait-Ui { param($items) $items.automationId -notcontains 'WorkspaceReplaceTabs' }
}
function Fill([string] $Name, [string] $Text) {
    if ($Name -eq 'Search tool items') {
        $items = @(Read-Ui)
        if ($items.automationId -contains 'FavoriteTree' -and -not @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq $Name }).Count) {
            Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'Search' -or $items.automationId -contains 'ToolSearch' }
        }
    }
    $field = Pick 'Edit' $Name
    Ui @('set-value', $field.selector, $Text) | Out-Null
    if ([string](Pick 'Edit' $Name).value -cne $Text) { throw "The text field '$Name' did not accept the test value." }
}
function Primary { Ui @('invoke', 'PrimaryButton', '--action', 'invoke') | Out-Null }
function Assert-ToolToolbar {
    $items = @(Read-Ui)
    $bar = @($items | Where-Object { $_.type -eq 'AppBar' -and $_.automationId -in 'Commands','ItemCommands' })[0]
    if (-not $bar -or $bar.height -gt 34) { throw 'The tool command row is taller than the compact toolbar height.' }
    $search = @($items | Where-Object { $_.type -eq 'Edit' -and $_.automationId -in 'ToolSearch','SearchResultFilter' })[0]
    $view = @($items | Where-Object automationId -eq 'ToolListViewOptions')[0]
    $preview = @($items | Where-Object automationId -eq 'InspectDetails')[0]
    if (-not $search -or -not $view -or -not $preview -or $preview.type -ne 'Button') { throw 'The shared toolbar is missing its normal preview button or view dropdown.' }
    if ($search.x + $search.width -gt $view.x -or $view.x + $view.width -gt $preview.x) { throw 'Toolbar utilities are not ordered search, view, preview.' }
    foreach ($control in @($view,$preview)) {
        if ([Math]::Abs(($control.y + $control.height / 2) - ($search.y + $search.height / 2)) -gt 1) { throw 'Toolbar controls are not vertically centered.' }
    }
    $more = @($items | Where-Object { $_.automationId -eq 'MoreButton' -and [Math]::Abs(($_.y + $_.height / 2) - ($search.y + $search.height / 2)) -le 1 })
    if ($more.Count) {
        if ($more[0].x + $more[0].width -gt $search.x) { throw 'See more is not before the right-side toolbar utilities.' }
        $actions = @($items | Where-Object { $_.type -in 'Button','CheckBox' -and $_.x -lt $more[0].x -and
            [Math]::Abs(($_.y + $_.height / 2) - ($search.y + $search.height / 2)) -le 1 })
        $lastAction = ($actions | ForEach-Object { $_.x + $_.width } | Measure-Object -Maximum).Maximum
        if ($actions.Count -and $more[0].x - $lastAction -gt 12) { throw 'See more is separated from the left-side actions.' }
    }
}
function Assert-SplitterDrag([string] $Id, [string] $Axis, [string] $EvidenceName) {
    $measurements = [Collections.Generic.List[object]]::new()
    try {
        foreach ($delta in -48, 48, -96, 96) {
            Set-TestWindowForeground
            $before = @((Read-Ui) | Where-Object automationId -eq $Id)[0]
            if (-not $before) { throw "Missing splitter $Id." }
            $x = [int]($before.x + $before.width / 2)
            $y = [int]($before.y + $before.height / 2)
            $destination = if ($Axis -eq 'x') { '{0},{1}' -f ($x + $delta), $y } else { '{0},{1}' -f $x, ($y + $delta) }
            Activate-Editor
            Ui @('drag', $Id, $destination, '--hold-ms', '100', '--dwell-ms', '200') | Out-Null
            $after = @((Read-Ui) | Where-Object automationId -eq $Id)[0]
            $actual = $after.$Axis - $before.$Axis
            $measurements.Add(@{ delta = $delta; actual = $actual; before = $before.$Axis; after = $after.$Axis })
            if ([Math]::Abs($actual - $delta) -gt 3) { throw "$Id moved $actual pixels for a $delta pixel pointer move." }
        }
    } finally {
        $measurements.ToArray() | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "$EvidenceName.json") -Encoding utf8
    }
}
function Assert-PreviewResize {
    Wait-Ui { param($items) $items.automationId -contains 'ToolDetailsResize' }
    $before = @((Read-Ui) | Where-Object automationId -eq 'ToolDetailsResize')[0]
    Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'ToolDetailsResize') | Out-Null
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.x -lt $before.x - 10 }).Count -eq 1 }
    $resized = @((Read-Ui) | Where-Object automationId -eq 'ToolDetailsResize')[0]
    $destination = '{0},{1}' -f [int]($resized.x + $resized.width / 2 - 24), [int]($resized.y + $resized.height / 2)
    Ui @('drag', 'ToolDetailsResize', $destination) | Out-Null
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.x -lt $resized.x - 10 }).Count -eq 1 }
    $resized = @((Read-Ui) | Where-Object automationId -eq 'ToolDetailsResize')[0]
    Ui @('invoke', 'InspectDetails', '--action', 'invoke') | Out-Null
    Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
    Ui @('invoke', 'InspectDetails', '--action', 'invoke') | Out-Null
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and [Math]::Abs($_.x - $resized.x) -le 1 }).Count -eq 1 }
}
function Set-Preview([bool] $Show) {
    $button = @((Read-Ui) | Where-Object automationId -eq 'InspectDetails')[0]
    if (-not $button) { throw 'Missing preview toggle.' }
    if (($button.name -eq 'Hide details') -ne $Show) { Ui @('invoke', $button.selector) | Out-Null }
}
function Read-ToolbarIconMask([string] $Path) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($Path)
    try {
        $scale = $bitmap.Height / 32
        $mask = [Text.StringBuilder]::new()
        for ($y = [int](5 * $scale); $y -lt [int](27 * $scale); $y++) {
            for ($x = [int](6 * $scale); $x -lt [int](26 * $scale); $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                [void]$mask.Append([int]($pixel.R + $pixel.G + $pixel.B -gt 450))
            }
        }
        $mask.ToString()
    } finally { $bitmap.Dispose() }
}
function Read-SortStatus([string] $Column) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, ($Column + 'SortButton'))
    $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $button) { throw "Missing $Column sort button in UI Automation." }
    $button.Current.ItemStatus
}
function View-Option([string] $Name) {
    $view = Pick 'Button' 'View'
    Ui @('invoke', $view.selector, '--action', 'invoke') | Out-Null
    $menu = Read-ContextMenu $Name
    $item = @($menu.Items | Where-Object name -eq $Name)
    if ($item.Count -ne 1) { throw "Expected one View option named '$Name'." }
    $selector = if ($item[0].automationId) { $item[0].automationId } else { $Name }
    Ui @('invoke', $selector) -Window $menu.Window | Out-Null
}
function Header-Command([string] $Header, [string] $Name, [switch] $Keyboard) {
    $target = $Header + 'SortButton'
    if ($Keyboard) { Ui @('send-keys', 'shift+f10', '--target', $target, '--via', 'send-input') | Out-Null }
    else { Ui @('click', $target, '--right') | Out-Null }
    $menu = Read-ContextMenu $Name
    $item = @($menu.Items | Where-Object name -ceq $Name)
    if ($item.Count -ne 1 -or -not $item[0].isEnabled) { throw "Missing enabled header command '$Name' for $Header." }
    Ui @('invoke', $item[0].selector) -Window $menu.Window | Out-Null
    Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
}
function Browser {
    $tab = @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $fixtureName })[0]
    if (-not $tab) { throw 'Missing fixture browser tab.' }
    Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
    Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
}
function Start-SearchInMainTab {
    Press 'Start search'
    Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
    Press 'Open panel tab in main tab'
}
function Open-Menu([string] $Name, [string] $Group, [switch] $MainTab) {
    Set-TestWindowForeground
    Ui @('send-keys', 'alt+f', '--via', 'send-input') | Out-Null
    if ($Group) {
        $popup = Read-ContextMenu $Group
        $groupItem = Pick 'MenuItem' $Group -Window $popup.Window
        Ui @('invoke', $groupItem.selector, '--action', 'expand') -Window $popup.Window | Out-Null
    }
    $popup = Read-ContextMenu $Name
    $item = @($popup.Items | Where-Object name -eq $Name)[0]
    Ui @('invoke', $item.selector) -Window $popup.Window | Out-Null
    if ($MainTab) {
        if ($Name -in 'Favorites', 'Notes') { Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null }
        else { Press 'Open panel tab in main tab' }
    }
}
function Select-Row([string] $Text) {
    $pattern = '*' + [Management.Automation.WildcardPattern]::Escape($Text) + '*'
    $rows = @(Read-Ui | Where-Object { $_.type -in @('ListItem','TreeItem') -and ($_.name -like $pattern -or $_.type -eq 'ListItem' -and @((Elements $_.children) | Where-Object { $_.name -like $pattern }).Count) })
    if ($rows.Count -ne 1) { throw "Expected one row containing '$Text', found $($rows.Count)." }
    Ui @('invoke', $rows[0].selector, '--action', 'select') | Out-Null
    $rows[0]
}
function Copy-DetailBytes([ValidateSet('Before','After')][string] $Side) {
    Ui @('invoke', ($Side + 'ValueActions'), '--action', 'invoke') | Out-Null
    $menu = Read-ContextMenu 'Copy exact bytes'
    $item = @($menu.Items | Where-Object automationId -eq ('Copy' + $Side + 'ValueBytes'))[0]
    if (-not $item) { throw "Missing exact-byte action for $Side." }
    Ui @('invoke', $item.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
}
function Assert-CaptureContext {
    $capture = Pick 'ComboBox' 'Choose capture'
    $items = @(Read-Ui)
    $label = @($items | Where-Object automationId -eq 'WatchCaptureLabel')[0]
    $status = @($items | Where-Object automationId -eq 'Context')[0]
    if (-not $label -or $capture.x -lt $label.x + $label.width -or -not $status -or
        [Math]::Abs(($status.y + $status.height / 2) - ($capture.y + $capture.height / 2)) -gt 3) {
        throw 'The capture selector overlaps its label or is detached from its status row.'
    }
}
function Assert-DetailStateAfterResize {
    Ui @('invoke', 'CompareRawBytes', '--action', 'expand') | Out-Null
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CompareRawBytes')
    $comparison = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $identity = $comparison.GetRuntimeId() -join ','
    [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1180, 900, 0x16) | Out-Null
    Wait-Ui { param($items) $items.automationId -contains 'ByteDifference' }
    $comparison = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $pattern = [System.Windows.Automation.ExpandCollapsePattern]$comparison.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if (($comparison.GetRuntimeId() -join ',') -ne $identity -or $pattern.Current.ExpandCollapseState -ne 'Expanded') {
        throw 'Resizing the window recreated the selected comparison or collapsed its byte details.'
    }
    Ui @('invoke', 'CompareRawBytes', '--action', 'collapse') | Out-Null
    [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
}
function Read-Details {
    (@(Read-Ui -All | Where-Object automationId -in @('BeforeText','AfterText') | ForEach-Object name) -join "`n")
}
function Get-ResultViewport([string] $ListName, [string] $Selection) {
    $list = Pick 'List' $ListName
    Ui @('scroll', $list.selector, '--to', 'bottom') | Out-Null
    Ui @('scroll', $list.selector, '--direction', 'up') | Out-Null
    $list = Pick 'List' $ListName
    $visible = @(Elements $list.children | Where-Object type -eq 'ListItem')
    if ($visible.name -like "*$Selection*") { throw "The selected row '$Selection' should be offscreen." }
    $row = @($visible | Where-Object {
        $_.y -gt $list.y -and $_.y + $_.height -le $list.y + $list.height
    } | Sort-Object y)[0]
    if (-not $row -or $row.name -notlike '*Bulk*') { throw "Missing scrolled fixture row in $ListName." }
    @{ List = $ListName; Name = $row.name; Top = $row.y - $list.y }
}
function Assert-ResultViewport($Viewport) {
    $list = Pick 'List' $Viewport.List
    $row = @(Elements $list.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Viewport.Name })[0]
    if (-not $row -or [Math]::Abs(($row.y - $list.y) - $Viewport.Top) -gt 2) {
        throw "Scroll moved in $($Viewport.List) at '$($Viewport.Name)': expected $($Viewport.Top), found $($row.y - $list.y)."
    }
}
function Show-RootFavorites {
    $link = @((Read-Ui) | Where-Object automationId -eq 'FavoritesRoot')
    if ($link.Count) { Ui @('invoke', 'FavoritesRoot', '--action', 'invoke') | Out-Null }
    Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoritesFolderTitle' -and $_.name -eq 'Favorites' }).Count -eq 1 }
}
function Select-Folder([string] $Name) {
    $item = Pick 'TreeItem' $Name
    Ui @('invoke', $item.selector, '--action', 'select') | Out-Null
}
function Assert-TreeSelection([string] $Path, [switch] $All) {
    Wait-Ui {
        param($items)
        $node = @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $Path })
        if ($node.Count -ne 1) { return $false }
        $property = Ui @('get-property', $node[0].selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        $property.properties.IsSelected -eq 'True'
    } -All:$All
}
function Test-Ui([string] $Name, [scriptblock] $Action) {
    if ($TestFilter -and $Name -notmatch $TestFilter) { return }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $result = @{ name = $Name; status = 'PASS' }
    try {
        & $Action | Out-Null
        Write-Output "PASS: $Name"
    } catch {
        $result.status = 'FAIL'
        $result.detail = "$_"
        $result.stack = $_.ScriptStackTrace
        throw
    } finally {
        $result.seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        $results.Add($result)
    }
}
function Context-Menu([string] $Target, [switch] $Keyboard) {
    if ($Keyboard) { Ui @('send-keys', 'shift+f10', '--target', $Target, '--via', 'send-input') | Out-Null }
    else { Ui @('click', $Target, '--right') | Out-Null }
    Read-ContextMenu
}
function Read-ContextMenu([string] $Expected = '', [string[]] $Types = @('MenuItem')) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $candidates = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | ForEach-Object hwnd) + @($script:mainWindow)
        foreach ($window in $candidates) {
            try { $items = @(Read-Ui -Window $window | Where-Object { $_.type -in $Types }) }
            catch {
                if ($_ -match 'AppNotFoundException|stale_element|different process' -or
                    ($window -ne $script:mainWindow -and $_ -match 'GetDpiForWindow failed for HWND')) { continue }
                throw
            }
            if ($items.Count -and (-not $Expected -or $items.name -contains $Expected)) { return @{ Window = $window; Items = $items } }
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw "Expected context command '$Expected' did not appear."
}
function Read-ToolbarTools {
    $menu = Read-ContextMenu 'Customize toolbar…' -Types @('MenuItem', 'Button')
    $menu.Items = @($menu.Items | Where-Object { $_.type -eq 'MenuItem' -or $_.automationId -like 'ToolsMenuPin*' })
    $menu
}
function File-Picker([string] $Action, [string] $Path) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $picker = @($windows | Where-Object { $_.ownerHwnd -eq $mainWindow -and $_.processName -eq 'PickerHost' })[0]
        if ($picker) {
            if ($Action -eq 'Cancel') {
                $cancel = Pick 'Button' 'Cancel' -Window $picker.hwnd
                Ui @('invoke', $cancel.selector, '--action', 'invoke') -Window $picker.hwnd | Out-Null
                return
            }
            $fields = @(Read-Ui -Window $picker.hwnd | Where-Object { $_.type -eq 'Edit' -and
                ($_.automationId -eq '1001' -or $_.name -eq 'File name:') })
            if ($fields.Count -ne 1) { Start-Sleep -Milliseconds 100; continue }
            if ($Action -eq 'Save') {
                Set-Clipboard -Value $Path
                Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $fields[0].selector) -Window $picker.hwnd | Out-Null
                Ui @('send-keys', 'ctrl+v', '--via', 'send-input') -Window $picker.hwnd | Out-Null
            } else {
                Ui @('set-value', $fields[0].selector, $Path) -Window $picker.hwnd | Out-Null
            }
            $typedTimer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $items = @(Read-Ui -Window $picker.hwnd)
                $typed = @($items | Where-Object { $_.type -eq 'Edit' -and $_.value -ceq $Path })
                if ($typed.Count -eq 1) { break }
                Start-Sleep -Milliseconds 100
            } while ($typedTimer.Elapsed.TotalSeconds -lt 10)
            if ($typed.Count -ne 1) { throw "The $Action picker did not accept the exact path '$Path'." }
            $confirm = @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq $Action -and $_.automationId -eq '1' })
            if ($confirm.Count -ne 1) { throw "Missing the file picker's $Action confirmation button." }
            Ui @('invoke', $confirm[0].selector, '--action', 'invoke') -Window $picker.hwnd | Out-Null
            return
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw "The $Action file picker did not appear."
}
function Wait-PrintPreviewClosed {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $previews = @($windows | Where-Object { $_.hwnd -ne $mainWindow -and $_.title -ceq "$address - Print" })
        if (-not $previews.Count) {
            Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
            return
        }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw 'Print preview did not close after Cancel.'
}
function Open-VerifiedPrintPreview([switch] $MultiplePages) {
    if (-not $ExpectedPreviewPrinter) { throw 'Pass the exact disposable XPS queue name for print preview checks.' }
    $expectedTitle = "$address - Print"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $preview = @($windows | Where-Object { $_.hwnd -ne $mainWindow -and $_.title -ceq $expectedTitle })[0]
        if ($preview) {
            try { $controls = @(Read-Ui -Window ([long]$preview.hwnd)) }
            catch { if ($_ -match 'AppNotFoundException|stale_element|different process') { Start-Sleep -Milliseconds 100; continue }; throw }
            $ui = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Raw | ConvertFrom-Json
            $root = @($ui.windows)[0]
            if ([long]$root.hwnd -ne [long]$preview.hwnd -or $root.title -cne $expectedTitle) { throw 'UIA inspection did not match the fixture print window.' }
            $images = @($controls | Where-Object { $_.automationId -eq 'PreviewImage' -and $_.type -eq 'Image' -and -not $_.isOffscreen -and $_.width -gt 0 -and $_.height -gt 0 })
            $print = @($controls | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Print' -and $_.isEnabled })
            $cancel = @($controls | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' -and $_.isEnabled })
            $printerSelector = @($controls | Where-Object { $_.type -eq 'ComboBox' -and $_.automationId -eq 'printerSelector' })
            $selectedPrinter = @()
            if ($printerSelector.Count -eq 1) { $selectedPrinter = @(Elements $printerSelector[0].children | Where-Object { $_.type -eq 'ListItem' }) }
            $page = @($controls | Where-Object automationId -eq 'TotalPageNumber')[0]
            $pageCount = 0
            $validPageCount = $page -and [int]::TryParse([string]$page.name, [ref]$pageCount)
            if ($selectedPrinter.Count -ne 1 -or $selectedPrinter[0].name -cne $ExpectedPreviewPrinter) {
                throw "The preview is not using the disposable XPS queue '$ExpectedPreviewPrinter'."
            }
            if ($controls.name -contains 'Loading preview' -or $images.Count -ne 1 -or $print.Count -ne 1 -or
                $cancel.Count -ne 1 -or -not $validPageCount -or
                ($MultiplePages -and $pageCount -le 1) -or (-not $MultiplePages -and $pageCount -ne 1)) {
                Start-Sleep -Milliseconds 150
                continue
            }
            @{
                FixtureAddress = $address; WindowTitle = $root.title; WindowHwnd = [long]$root.hwnd
                PageImageVisible = $true; PageImageWidth = $images[0].width; PageImageHeight = $images[0].height
                PageCount = $pageCount; PrintEnabled = $true; SelectedPrinter = $selectedPrinter[0].name; CancelEnabled = $true
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'verified-preview.json') -Encoding utf8
            Ui @('invoke', $cancel[0].selector, '--action', 'invoke') -Window $preview.hwnd | Out-Null
            Wait-PrintPreviewClosed
            return
        }
        $errors = @(Read-Ui | Where-Object { $_.name -in 'Windows could not complete the print job.', 'Windows could not open print preview.', "Couldn't print the registry report" })
        if ($errors.Count) { throw "Print failed before verified preview: $($errors[0].name)" }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt 50)
    $windows | Select-Object processName,title,className,label,hwnd,ownerHwnd | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'print-windows.json') -Encoding utf8
    throw 'The exact preview did not expose a visible page image, selected XPS queue, enabled Print button, and valid page count.'
}
function Snapshot-Action([string] $Name) {
    Press 'Snapshots'
    $menu = Read-ContextMenu $Name
    Ui @('invoke', ($menu.Items | Where-Object name -eq $Name).selector, '--action', 'invoke') -Window $menu.Window | Out-Null
}
function Close-ContextMenu($Menu) { Ui @('send-keys', 'esc', '--via', 'send-input') -Window $Menu.Window | Out-Null }
function Wait-ByteToolWindow([string]$hexView) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
        $window = $windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.title -eq 'Registry Editor' } |
            Select-Object -First 1
        if ($window) { break }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    if (-not $window) { throw 'The byte tool did not open as a separate window.' }
    $hwnd = [long]$window.hwnd
    if (-not [UxTestWindow]::IsOwnedBy($hwnd, $mainWindow) -or [UxTestWindow]::IsWindowEnabled($mainWindow)) {
        throw 'The byte tool window is not modal to the editor.'
    }
    Wait-Ui { param($items) $items.automationId -contains $hexView } -Window $hwnd
    $hwnd
}
function Expect-HistoryLocation([string] $Target) {
    Wait-Ui {
        param($items)
        $crumbs = @($items | Where-Object automationId -match '^Breadcrumb\d+$')
        $crumbs.Count -gt 0 -and $crumbs[-1].name -eq ($Target -split '\\')[-1] -and $items.name -notcontains 'Address'
    }
    Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
    Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Address' }).Count -eq 1 }
    $input = Pick 'Edit' 'Address'
    if ($input.value -ne $Target) { throw "Navigation reached '$($input.value)' instead of '$Target'." }
    Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
}
function Go-ToKey([string] $Path) {
    Set-TestWindowForeground
    Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
    Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
    Ui @('set-value', 'OmniboxInput', $Path) | Out-Null
    Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
    Wait-Ui { param($items) @($items | Where-Object automationId -match '^Breadcrumb\d+$')[-1].name -eq ($Path -split '\\')[-1] }
}

function Activate-Editor {
    if ([UxTestWindow]::GetForegroundWindow() -eq $mainWindow) { return }
    if (-not $ExpectedAppSid) {
        try { [UxTestWindow]::RequireForeground() }
        catch { Set-TestWindowForeground }
        return
    }
    $point = Get-TestActivationPoint
    $point.Pid = $appProcess.Id
    $point.Hwnd = [long]$mainWindow
    $ack = Join-Path $ArtifactDirectory "activation-$($appProcess.Id)-$mainWindow.exit"
    [IO.File]::WriteAllText("$ArtifactDirectory\activate.tmp", ($point | ConvertTo-Json -Compress))
    [IO.File]::Move("$ArtifactDirectory\activate.tmp", "$ArtifactDirectory\activate.json")
    if (-not [Threading.SpinWait]::SpinUntil([Func[bool]] { [IO.File]::Exists($ack) }, 15000) -or
        [IO.File]::ReadAllText($ack) -ne '0') { throw 'The Sandbox desktop could not activate the verified editor window.' }
    [UxTestWindow]::RequireForeground()
}
function Restart-Editor {
    if (-not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The editor did not save its layout and close.' }
    }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    if ($SandboxRun -eq [guid]::Empty) { $start.Environment['REGEDIT_UI_TEST_DATA'] = $dataFolder }
    $script:appProcess = [Diagnostics.Process]::Start($start)
    $appProcess.Id | Set-Content (Join-Path $ArtifactDirectory 'process.txt')
    $appProcess.WaitForInputIdle(15000) | Out-Null
    Invoke-WinAppChecked @('ui', 'wait-for', 'ToolsButton', '-a', "$($appProcess.Id)") | Out-Null
    $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
    $window = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.className -eq 'WinUIDesktopWin32WindowClass' })[0]
    if (-not $window) { throw 'The editor did not reopen its saved session.' }
    $script:mainWindow = [long]$window.hwnd
    [UxTestWindow]::TargetWindow = [IntPtr]$mainWindow
    Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
    Assert-UiTestProcess $appProcess.Id -SandboxRun $SandboxRun
    Assert-AppSid $appProcess.Id
    Activate-Editor
}
function Middle-Open($Target, [string] $TabName) {
    Set-TestWindowForeground
    $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
    $Target = Pick $Target.type $Target.name
    [UxTestWindow]::MiddleClickAt([int]($Target.x + $Target.width / 2), [int]($Target.y + $Target.height / 2))
    Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $count + 1 } -All
    $tab = @(Read-Ui -All | Where-Object type -eq 'TabItem')[-1]
    if ($tab.name -ne $TabName) { throw "Middle-click opened '$($tab.name)' instead of '$TabName'." }
    $tab
}
function Close-MiddleTab($Tab) {
    Set-TestWindowForeground
    if (@(Read-Ui | Where-Object { $_.automationId -eq 'PinTools' -and $_.name -eq 'Pin tool panel' }).Count) { Press 'Close tool panel' }
    $tabs = @(Read-Ui -All | Where-Object type -eq 'TabItem')
    $target = @($tabs | Where-Object selector -eq $Tab.selector)
    if ($target.Count -ne 1) { throw 'The tab to middle-close is no longer present.' }
    if ($target[0].isOffscreen -or $target[0].width -lt 100) {
        Ui @('invoke', $target[0].selector, '--action', 'select') | Out-Null
        $target = @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.selector -eq $Tab.selector })
    }
    if ($target.Count -ne 1 -or $target[0].isOffscreen -or $target[0].width -le 0) { throw 'The tab is not visible for the close gesture.' }
    [UxTestWindow]::MiddleClickAt([int]($target[0].x + $target[0].width / 2), [int]($target[0].y + $target[0].height / 2))
    Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $tabs.Count - 1 } -All
    if (@(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.selector -eq $Tab.selector }).Count) { throw 'Middle-click closed the wrong tab.' }
}

try {
    Test-Ui 'Clean no-argument startup works with settings absent' {
        if ([IO.File]::Exists($settingsFile)) { throw 'Fresh startup test profile already contains settings.' }
        $start = [Diagnostics.ProcessStartInfo]::new($Executable)
        $start.UseShellExecute = $false
        if ($SandboxRun -eq [guid]::Empty) { $start.Environment['REGEDIT_UI_TEST_DATA'] = $dataFolder }
        $cleanProcess = [Diagnostics.Process]::Start($start)
        try {
            $cleanProcess.WaitForInputIdle(15000) | Out-Null
            Invoke-WinAppChecked @('ui', 'wait-for', 'ToolsButton', '-a', "$($cleanProcess.Id)") | Out-Null
            $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
            $window = @($windows | Where-Object { $_.processId -eq $cleanProcess.Id -and $_.className -eq 'WinUIDesktopWin32WindowClass' })[0]
            if (-not $window) { throw 'The no-argument process did not create its own main window.' }
            Assert-UiTestProcess $cleanProcess.Id -SandboxRun $SandboxRun
            Assert-AppSid $cleanProcess.Id
            $elements = @(Read-Ui -Window ([long]$window.hwnd))
            if (@($elements | Where-Object { $_.type -eq 'List' -and $_.name -eq 'Registry keys and values' }).Count -ne 1 -or
                @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'HKEY_CURRENT_USER' }).Count -ne 1) {
                throw 'A clean no-argument start did not open the expected default registry view.'
            }
            if (-not $cleanProcess.CloseMainWindow() -or -not $cleanProcess.WaitForExit(15000)) {
                throw 'The clean-start editor did not close gracefully.'
            }
            if (-not [IO.File]::Exists($settingsFile)) { throw 'The clean-start editor did not persist its initial settings on close.' }
        } finally {
            if (-not $cleanProcess.HasExited) { $cleanProcess.Kill(); $cleanProcess.WaitForExit() }
            $cleanProcess.Dispose()
        }
    }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey)
    try {
        $child = $key.CreateSubKey('Child'); $child.Dispose()
        $key.SetValue('Counter', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('NeedleOne', 'needle one')
        $key.SetValue('NeedleTwo', 'needle two')
        $key.SetValue('Bytes', [byte[]](0..15), [Microsoft.Win32.RegistryValueKind]::Binary)
    } finally { $key.Dispose() }
    $workspaceRefresh = 'Bulk workspace refresh preserves selection and scroll'
    $settings = @{}
    if (-not $TestFilter -or $workspaceRefresh -match $TestFilter) {
        $settings.Workspaces = @(1..80 | ForEach-Object {
            $name = 'Bulk workspace {0:D3}' -f $_
            @{ Name = $name; Tabs = @(@{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = "$subKey\$name" }); Searches = @(); Watches = @() }
        })
    }
    [IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 6))
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.ArgumentList.Add($address)
    if ($SandboxRun -eq [guid]::Empty) { $start.Environment['REGEDIT_UI_TEST_DATA'] = $dataFolder }
    $appProcess = [Diagnostics.Process]::Start($start)
    $appProcess.Id | Set-Content (Join-Path $ArtifactDirectory 'process.txt')
    $appProcess.WaitForInputIdle(15000) | Out-Null
    Invoke-WinAppChecked @('ui', 'wait-for', 'ToolsButton', '-a', "$($appProcess.Id)") | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
        $window = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.className -eq 'WinUIDesktopWin32WindowClass' })[0]
        if ($window) { break }; Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    if (-not $window) { throw 'Test editor did not open.' }
    Assert-UiTestProcess $appProcess.Id -SandboxRun $SandboxRun
    Assert-AppSid $appProcess.Id
    $script:mainWindow = $window.hwnd
    [UxTestWindow]::TargetWindow = [IntPtr]$mainWindow
    Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
    if ($ExpectedAppSid) { Activate-Editor }

    Test-Ui 'Binary icons render in registry rows and the inspector after session restore' {
        Restart-Editor
        Browser
        if ((Read-Ui).automationId -contains 'ToolDetailsResize') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        $row = Select-Row 'Bytes'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueNameHeading' -and $_.value -eq 'Bytes' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'binary-icon-inspector.png')) | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        if ($appProcess.HasExited) { throw 'The editor exited while rendering binary icons after session restore.' }
    }

    Test-Ui 'Browser splitters preserve drag keyboard limits and saved navigation width' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PaneSplitter' -and $items.automationId -contains 'SearchSplitter' }
        Assert-SplitterDrag 'PaneSplitter' 'x' 'browser-tree-drag'
        Assert-SplitterDrag 'SearchSplitter' 'x' 'browser-search-drag'
        foreach ($id in 'PaneSplitter', 'SearchSplitter') {
            $before = @(Read-Ui | Where-Object automationId -eq $id)[0]
            Ui @('send-keys', 'right', '--via', 'send-input', '--target', $id) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq $id -and $_.x -gt $before.x + 10 }).Count -eq 1 }
            Ui @('send-keys', 'left', '--via', 'send-input', '--target', $id) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq $id -and [Math]::Abs($_.x - $before.x) -le 2 }).Count -eq 1 }
        }
        $scale = [UxTestWindow]::GetDpiForWindow([IntPtr]$mainWindow) / 96.0
        if (-not $scale) { throw 'Could not read the browser display scale.' }
        foreach ($destination in 50, 1000) {
            $splitter = @(Read-Ui | Where-Object automationId -eq 'PaneSplitter')[0]
            Ui @('drag', 'PaneSplitter', ('{0},{1}' -f $destination, [int]($splitter.y + $splitter.height / 2))) | Out-Null
            $tree = @(Read-Ui | Where-Object automationId -eq 'RegistryTree')[0]
            $paneWidth = $tree.width + 6 * $scale
            if ($paneWidth -lt 160 * $scale - 2 -or $paneWidth -gt 600 * $scale + 2) { throw 'Navigation resizing exceeded its width bounds.' }
        }
        Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'PaneSplitter') | Out-Null
        $savedWidth = @(Read-Ui | Where-Object automationId -eq 'RegistryTree')[0].width
        Restart-Editor
        $restoredWidth = @(Read-Ui | Where-Object automationId -eq 'RegistryTree')[0].width
        if ([Math]::Abs($restoredWidth - $savedWidth) -gt 2) { throw 'The resized navigation width did not survive restart.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'browser-splitters-restored.png')) | Out-Null
    }

    Test-Ui 'Command icons survive browser command overflow and tab reloads' {
        Browser
        foreach ($width in 760, 1380, 800, 1380) {
            [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, $width, 900, 0x16) | Out-Null
            $new = Pick 'Button' 'New'
            $more = @(Read-Ui | Where-Object { $_.automationId -eq 'MoreButton' -and [Math]::Abs($_.y - $new.y) -lt 12 })
            if ($more.Count -ne 1) { throw 'The browser command bar does not expose one overflow button.' }
            Ui @('invoke', $more[0].selector) | Out-Null
            $overflow = Read-ContextMenu 'Search' -Types @('Button', 'MenuItem')
            Close-ContextMenu $overflow
            Open-Menu 'New tab'
            Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -gt 1 }
            Open-Menu 'Close tab'
            Browser
            if ($appProcess.HasExited) { throw 'The editor exited during command bar template transitions.' }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'browser-command-icons.png')) | Out-Null
    }

    Test-Ui 'Value inspector routes dialogs and toggles the docked detail panel' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        if ((Read-Ui).automationId -contains 'ToolDetailsResize') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and $_.name -eq 'Close' }).Count -eq 1 }
        if (@(Read-Ui | Where-Object { $_.automationId -eq 'ToggleValueInspector' -and $_.name -eq 'Close detail panel' }).Count) { throw 'Inspect opened the closed detail panel.' }
        Set-Clipboard -Value 'Inspector copy sentinel'
        Press 'Copy decimal (unsigned)'
        Assert-Clipboard '1' 'inspector-decimal-copy' 'Inspector copy sentinel'
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }

        $toggle = @(Read-Ui | Where-Object automationId -eq 'ToggleValueInspector')[0]
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ToggleValueInspector'))
        $togglePattern = $null
        if ($button.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$togglePattern)) {
            throw 'The detail panel button still exposes toggle state.'
        }
        $more = @(Read-Ui | Where-Object { $_.automationId -eq 'MoreButton' -and [Math]::Abs($_.y - $toggle.y) -lt 12 })
        if ($more.Count -ne 1 -or $toggle.x + $toggle.width -gt $more[0].x + 1 -or
            $more[0].x - ($toggle.x + $toggle.width) -gt 12) { throw 'The detail toggle is not immediately before toolbar overflow.' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolDetailsResize' }
        if (@(Read-Ui | Where-Object automationId -eq 'CloseValueInspector').Count -or
            @(Read-Ui | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Value inspector' }).Count) { throw 'The side inspector still reserves space for a header.' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        if (@(Read-Ui | Where-Object automationId -eq 'PrimaryButton').Count) { throw 'Inspect opened a dialog while the panel was visible.' }
        if (@(Read-Ui | Where-Object automationId -eq 'ValueHexView').Count) { throw 'The Inspector displays a raw hex view inline.' }
        Ui @('invoke', 'OpenRawBytes', '--action', 'invoke') | Out-Null
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
            $raw = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.title -eq 'Registry Editor' })
            if ($raw.Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 15)
        if ($raw.Count -ne 1) { throw 'Raw bytes did not open with the app caption.' }
        $rawWindow = [long]$raw[0].hwnd
        if (-not [UxTestWindow]::IsOwnedBy($rawWindow, $mainWindow)) { throw 'The app window does not own the raw-byte window.' }
        if ([UxTestWindow]::IsWindowEnabled($mainWindow)) { throw 'The raw-byte window left its owner enabled.' }
        Wait-Ui { param($items) $items.automationId -contains 'ExpandedValueHexView' } -Window $rawWindow
        $rawItems = Read-Ui -Window $rawWindow
        $hex = @($rawItems | Where-Object automationId -eq 'ExpandedValueHexView')[0]
        $scale = [UxTestWindow]::GetDpiForWindow([IntPtr]$mainWindow) / 96.0
        if ($hex.width -lt 744 * $scale - 2 -or $hex.height -lt 350 * $scale) { throw 'The larger raw-byte view is not wide or tall enough.' }
        $caption = @($rawItems | Where-Object automationId -eq 'DialogTitleBar')[0]
        $heading = @($rawItems | Where-Object automationId -eq 'DialogHeading')[0]
        $body = @($rawItems | Where-Object automationId -eq 'DialogBody')[0]
        if (-not $caption -or -not $heading -or [Math]::Abs($heading.y - $caption.y - $caption.height - 12 * $scale) -gt 2) {
            throw 'The raw-byte window is missing its custom caption or compact header spacing.'
        }
        if (-not $body -or [Math]::Abs($hex.x - $body.x) -gt 2 -or [Math]::Abs($hex.y - $body.y) -gt 2 -or
            [Math]::Abs($hex.width - $body.width) -gt 2 -or [Math]::Abs($hex.height - $body.height) -gt 2) {
            throw 'The raw-byte view does not fill the dialog body.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-inspector-raw-bytes.png')) -Window $rawWindow | Out-Null
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'CloseButton') -Window $rawWindow | Out-Null
        Wait-Ui { [UxTestWindow]::IsWindowEnabled($mainWindow) -and [UxTestWindow]::GetForegroundWindow() -eq $mainWindow }
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-inspector-docked-wide.png')) | Out-Null
        Select-Row 'NeedleOne' | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FullValueData' -and $_.value -eq 'needle one' }).Count -eq 1 }
        Set-Clipboard -Value 'Inspector data sentinel'
        Press 'Copy value data (full contents)'
        Assert-Clipboard 'needle one' 'inspector-data-copy' 'Inspector data sentinel'
        Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', 'FullValueData') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolDetailsResize' }
        $splitter = @(Read-Ui | Where-Object automationId -eq 'ToolDetailsResize')[0]
        Ui @('drag', 'ToolDetailsResize', ('{0},{1}' -f [int]($splitter.x - 500), [int]($splitter.y + $splitter.height / 2))) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OpenRawBytes' -and $items.automationId -contains 'OpenByteInterpreter' -and
            $items.automationId -notcontains 'ValueHexView' }
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 900, 900, 0x16) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.width -gt $_.height }).Count -eq 1 }
        Wait-Ui { param($items) $items.automationId -contains 'CloseValueInspector' -and @($items | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Value inspector' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-inspector-docked-narrow.png')) | Out-Null
        Ui @('invoke', 'CloseValueInspector') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'CloseValueInspector' }
    }

    Test-Ui 'F6 visits the open inspector in side and bottom layouts and skips it when closed' {
        Browser
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $windowPattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Activate-Editor
        $row = Select-Row 'Counter'
        if ((Read-Ui).automationId -notcontains 'ToolDetailsResize') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        foreach ($width in 1380, 900) {
            [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, $width, 900, 0x16) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueNameHeading' -and $_.value -eq 'Counter' }).Count -eq 1 }
            Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
            Assert-FocusedPane 'Value inspector'
            $focus = Ui @('get-focused', '--json') | ConvertFrom-Json
            if (-not $focus.hasFocus -or $focus.element.automationId -ne 'ValueNameHeading') { throw 'F6 did not focus the first inspector readout.' }
            Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
            Assert-FocusedPane 'Registry keys'
            Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
            Assert-FocusedPane 'Registry keys and values'
        }
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Select one key or value to inspect.' -and $items.automationId -notcontains 'ValueNameHeading' }
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Value inspector'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys'
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Registry keys'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys and values'
        View-Option 'Navigation pane'
        Wait-Ui { param($items) $items.automationId -notcontains 'RegistryTree' }
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Registry keys and values'
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueNameHeading' }
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Value inspector'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys and values'
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        View-Option 'Navigation pane'
    }

    Test-Ui 'F6 includes the sidebar and bottom panel in the window pane cycle' {
        function Assert-BottomPaneFocus {
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $divider = @(Read-Ui | Where-Object automationId -eq 'BottomPanelResize')[0]
            if (-not $focused -or $focused.Current.ProcessId -ne $appProcess.Id -or -not $divider -or
                $focused.Current.BoundingRectangle.Top -lt $divider.y) { throw 'Keyboard focus did not reach the bottom panel.' }
        }
        Browser
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $windowPattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Activate-Editor
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1700, 900, 0x16) | Out-Null
        $row = Select-Row 'Counter'
        Open-Menu 'Change journal' -Group 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        Press 'Notes for this key'
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' }
        $pinChanged = (Read-Ui).name -contains 'Pin tool panel'
        if ($pinChanged) { Press 'Pin tool panel' }
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueNameHeading' }
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Value inspector'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Local note'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-BottomPaneFocus
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys and values'
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Local note'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-BottomPaneFocus
        Press 'Close tool panel'
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-BottomPaneFocus
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys'
        Press 'Notes for this key'
        Press 'Hide bottom panel'
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' }
        Ui @('send-keys', 'f6', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-FocusedPane 'Local note'
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Assert-FocusedPane 'Registry keys'
        if ($pinChanged) { Press 'Unpin tool panel' }
        Press 'Close tool panel'
    }

    Test-Ui 'Inspector Copy buttons appear on row hover and remain keyboard accessible' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueNameHeading' }
        $field = Pick 'Edit' 'Decimal (unsigned)'
        $copy = Pick 'Button' 'Copy decimal (unsigned)'
        $hidden = Join-Path $ArtifactDirectory 'copy-button-hidden.png'
        $hovered = Join-Path $ArtifactDirectory 'copy-button-hovered.png'
        $restored = Join-Path $ArtifactDirectory 'copy-button-restored.png'
        $focused = Join-Path $ArtifactDirectory 'copy-button-row-focused.png'
        Ui @('hover', 'ToggleValueInspector') | Out-Null
        Ui @('screenshot', $copy.selector, '--output', $hidden) | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-copy-hidden.png')) | Out-Null
        Ui @('hover', $field.selector) | Out-Null
        Ui @('screenshot', $copy.selector, '--output', $hovered) | Out-Null
        if ((Get-FileHash $hidden).Hash -eq (Get-FileHash $hovered).Hash) { throw 'Hovering the readout row did not reveal its Copy button.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-copy-hover.png')) | Out-Null
        Set-Clipboard -Value 'Hover copy sentinel'
        Ui @('click', $copy.selector) | Out-Null
        Assert-Clipboard '1' 'inspector-hover-copy' 'Hover copy sentinel'

        Ui @('focus', $row.selector) | Out-Null
        Ui @('hover', 'ToggleValueInspector') | Out-Null
        Ui @('screenshot', $copy.selector, '--output', $restored) | Out-Null
        if ((Get-FileHash $hidden).Hash -ne (Get-FileHash $restored).Hash) { throw 'The Copy button remained visible after its row lost hover and focus.' }
        Ui @('focus', $field.selector) | Out-Null
        Ui @('screenshot', $copy.selector, '--output', $focused) | Out-Null
        if ((Get-FileHash $hidden).Hash -eq (Get-FileHash $focused).Hash) { throw 'Focusing the readout row did not reveal its Copy button.' }
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', $field.selector) | Out-Null
        $focus = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $focus.hasFocus -or $focus.element.name -ne 'Copy decimal (unsigned)') { throw 'Tab did not reach the Copy button.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-copy-keyboard.png')) | Out-Null
        Set-Clipboard -Value 'Keyboard Copy button sentinel'
        Ui @('send-keys', 'space', '--via', 'send-input') | Out-Null
        Assert-Clipboard '1' 'inspector-keyboard-copy' 'Keyboard Copy button sentinel'
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
    }

    Test-Ui 'Inspector previews keys and unset default values as selections' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        $row = Select-Row '(Default)'
        if ((Read-Ui).automationId -notcontains 'ToolDetailsResize') {
            Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', $row.selector) | Out-Null
        }
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueNameHeading' -and $_.value -eq '(Default)' }).Count -eq 1 }
        if ((Pick 'Edit' 'Value data (full contents)').value -ne '(value not set)') { throw 'The unset default does not have an explicit preview.' }
        if (@(Read-Ui | Where-Object automationId -in @('OpenByteInterpreter', 'InterpretBytes', 'OpenRawBytes', 'ValueHexView')).Count) { throw 'An unset default exposes stored-byte controls.' }
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-unset-default.png')) | Out-Null
        View-Option 'Show keys in list'
        $child = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child,*' })
        if ($child.Count -ne 1) { throw 'Expected one Child key in the registry list.' }
        Ui @('invoke', $child[0].selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'InspectorKeyPath' -and $_.value -eq "$address\Child" }).Count -eq 1 }
        if ((Pick 'Edit' 'Key name').value -ne 'Child') { throw 'The key preview shows a different selection.' }
        if (@(Read-Ui | Where-Object automationId -in @('FullValueData', 'OpenByteInterpreter', 'InterpretBytes', 'OpenRawBytes', 'ValueHexView')).Count) { throw 'A key exposes value-data controls.' }
        Set-Clipboard -Value 'Key preview path sentinel'
        Press 'Copy key path'
        Assert-Clipboard "$address\Child" 'inspector-key-path-copy' 'Key preview path sentinel'
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-key.png')) | Out-Null
        Go-ToKey "$address\Child"
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'InspectorKeyPath' -and $_.value -eq "$address\Child" }).Count -eq 1 }
        Go-ToKey $address
        Select-Row 'Counter' | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OpenByteInterpreter' }
        if (@(Read-Ui | Where-Object automationId -eq 'InspectorKeyPath').Count) { throw 'Returning to a value left stale key details.' }
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        View-Option 'Show keys in list'
    }

    Test-Ui 'Parent key inspector shows the parent heading and destination path' {
        Browser
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $windowPattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Activate-Editor
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        View-Option 'Show keys in list'
        View-Option 'Show parent key ..'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq '.., Parent key, HKEY_CURRENT_USER\Software' }).Count -eq 1 }
        $parent = Pick 'ListItem' '.., Parent key, HKEY_CURRENT_USER\Software'
        Ui @('invoke', $parent.selector, '--action', 'select') | Out-Null
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'InspectorKeyPath' -and $_.value -eq 'HKEY_CURRENT_USER\Software' }).Count -eq 1 }
        if ((Pick 'Edit' 'Key name').value -ne '..') { throw 'The parent preview lost its navigation heading.' }
        if (@(Read-Ui | Where-Object automationId -in @('FullValueData', 'OpenByteInterpreter', 'InterpretBytes', 'OpenRawBytes', 'ValueHexView')).Count) { throw 'A parent key exposes value-data controls.' }
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'inspector-parent-key.png')) | Out-Null
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        View-Option 'Show parent key ..'
        View-Option 'Show keys in list'
    }

    Test-Ui 'Inspector displays the containing key note and opens its editor' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        $row = Select-Row 'Counter'
        if ((Read-Ui).automationId -contains 'ValueInspectorScroll') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if (@(Read-Ui | Where-Object automationId -eq 'EditKeyNote').Count) { throw 'A value dialog without key context displays a note indicator.' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueNameHeading' }
        if (@(Read-Ui | Where-Object automationId -eq 'EditKeyNote').Count) { throw 'An empty key note displays an indicator.' }
        Press 'Notes for this key'
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' }
        Fill 'Local note' 'Inspector key note fixture'
        Press 'Close tool panel'
        Wait-Ui { param($items) $items.automationId -contains 'EditKeyNote' }
        Select-Row 'NeedleOne' | Out-Null
        Wait-Ui {
            param($items)
            $items.name -contains 'Key note' -and $items.name -contains 'Inspector key note fixture' -and
                $items.automationId -contains 'EditKeyNote' -and $items.automationId -contains 'FullValueData'
        }
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-inspector-key-note.png')) | Out-Null
        Ui @('invoke', 'EditKeyNote', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Inspector key note fixture' }).Count -eq 1 }
        Fill 'Local note' ' '
        Press 'Close tool panel'
        Wait-Ui { param($items) $items.automationId -notcontains 'EditKeyNote' }
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }
    }

    Test-Ui 'Byte tools open owned dialogs and return to the inspector' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Select-Row 'Bytes' | Out-Null
        if ((Read-Ui).name -contains 'Show detail panel') { Press 'Show detail panel' }
        Wait-Ui { param($items) $items.automationId -contains 'OpenByteInterpreter' }
        if (@(Read-Ui | Where-Object automationId -in @('InspectorByteOffset', 'InspectorByteFormat', 'InspectorInterpretation')).Count) {
            throw 'The Inspector exposes the interpretation form inline.'
        }
        Ui @('focus', 'OpenByteInterpreter') | Out-Null
        Ui @('send-keys', 'space', '--via', 'send-input') | Out-Null
        $dialogWindow = Wait-ByteToolWindow 'InterpreterHexView'
        Ui @('set-value', 'InspectorByteOffset', '1') -Window $dialogWindow | Out-Null
        Ui @('set-value', 'InspectorByteCount', '4') -Window $dialogWindow | Out-Null
        Ui @('invoke', 'InterpretBytes', '--action', 'invoke') -Window $dialogWindow | Out-Null
        Wait-Ui { param($items)
            @($items | Where-Object { $_.automationId -eq 'InspectorInterpretation' -and $_.value -eq '01020304' }).Count -eq 1 -and
            @($items | Where-Object { $_.automationId -eq 'InterpreterHexView' -and $_.value -eq '01' }).Count -eq 1
        } -Window $dialogWindow
        Set-Clipboard -Value 'Interpretation copy sentinel'
        Ui @('invoke', (Pick 'Button' 'Copy interpretation' -Window $dialogWindow).selector, '--action', 'invoke') -Window $dialogWindow | Out-Null
        Assert-Clipboard '01020304' 'inspector-range-copy' 'Interpretation copy sentinel'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-inspector-interpretation.png')) -Window $dialogWindow | Out-Null

        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'InspectorByteFormat') -Window $dialogWindow | Out-Null
        Ui @('invoke', 'InterpretBytes', '--action', 'invoke') -Window $dialogWindow | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'InspectorInterpretation' -and
            $_.value -eq 'This timestamp format requires exactly 8 bytes.' }).Count -eq 1 } -Window $dialogWindow
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'CloseButton') -Window $dialogWindow | Out-Null
        Wait-Ui { [UxTestWindow]::IsWindowEnabled($mainWindow) -and [UxTestWindow]::GetForegroundWindow() -eq $mainWindow }
        Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolDetailsResize' }

        $row = Select-Row 'Counter'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and $items.automationId -contains 'OpenRawBytes' }
        Ui @('invoke', 'OpenRawBytes', '--action', 'invoke') | Out-Null
        $rawWindow = Wait-ByteToolWindow 'ExpandedValueHexView'
        Ui @('invoke', 'CloseButton', '--action', 'invoke') -Window $rawWindow | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and $items.automationId -contains 'OpenByteInterpreter' }
        Ui @('invoke', 'OpenByteInterpreter', '--action', 'invoke') | Out-Null
        $dialogWindow = Wait-ByteToolWindow 'InterpreterHexView'
        Ui @('invoke', 'CloseButton', '--action', 'invoke') -Window $dialogWindow | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and
            @($items | Where-Object { $_.automationId -eq 'ValueNameHeading' -and $_.value -eq 'Counter' }).Count -eq 1 }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
    }

    Test-Ui 'Preview splitters follow the pointer without accumulating drag overshoot' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+p', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.height -gt $_.width }).Count -eq 1 }
        Assert-SplitterDrag 'ToolDetailsResize' 'x' 'inspector-side-drag'
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 900, 900, 0x16) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.width -gt $_.height }).Count -eq 1 }
        Assert-SplitterDrag 'ToolDetailsResize' 'y' 'inspector-bottom-drag'
        Ui @('invoke', 'CloseValueInspector') | Out-Null
    }

    Test-Ui 'Bottom panel splitter follows the pointer without accumulating drag overshoot' {
        Browser
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Open-Menu 'Bottom panel' -Group 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'BottomPanelResize' }
        Assert-SplitterDrag 'BottomPanelResize' 'y' 'bottom-panel-drag'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'splitters.png')) | Out-Null
        Press 'Hide bottom panel'
    }

    Test-Ui 'Watch viewing commands switch pause and resume icons' {
        Open-Menu 'Watches' -Group 'Tools' -MainTab
        Ui @('invoke', 'NewWatchButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'RegistryTargetAddress' }
        Ui @('set-value', 'RegistryTargetAddress', $address) | Out-Null
        Primary
        Wait-Ui { param($items) $items.name -contains 'Pause viewing' -and $items.name -contains 'Waiting for changes' -and @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -like 'Watching*' }).Count -eq 1 }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('SelectionPreview', 'Selectable preview text') } finally { $key.Dispose() }
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*SelectionPreview*' }).Count -eq 1 }
        Select-Row 'SelectionPreview' | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'AfterText' }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $field = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AfterText'))
        $range = ([System.Windows.Automation.TextPattern]$field.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)).DocumentRange
        $expected = $range.GetText(-1).ReplaceLineEndings("`n").TrimEnd("`n")
        $field.SetFocus(); $range.Select()
        Set-Clipboard -Value 'selection preview sentinel'
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $actual = ([string](Get-Clipboard -Raw)).ReplaceLineEndings("`n").TrimEnd("`n")
            if ($actual -ceq $expected) { break }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 10)
        if ($actual -cne $expected) { throw 'The selectable preview did not preserve its text when copied.' }
        Assert-CaptureContext
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 760, 900, 0x16) | Out-Null
        Assert-CaptureContext
        Ui @('screenshot', 'WatchPicker', '--output', (Join-Path $ArtifactDirectory 'capture-active.png')) | Out-Null
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Ui @('screenshot', (Pick 'Button' 'Pause viewing').selector, '--output', (Join-Path $ArtifactDirectory 'watch-pause-icon.png')) | Out-Null
        Press 'Pause viewing'
        Wait-Ui { param($items) $items.name -contains 'Resume viewing' -and @($items | Where-Object name -like 'Viewing paused*').Count -eq 1 }
        Ui @('screenshot', (Pick 'Button' 'Resume viewing').selector, '--output', (Join-Path $ArtifactDirectory 'watch-resume-icon.png')) | Out-Null
        Press 'Resume viewing'
        Wait-Ui { param($items) $items.name -contains 'Pause viewing' -and $items.name -notcontains 'Resume viewing' }
        Ui @('screenshot', (Pick 'Button' 'Pause viewing').selector, '--output', (Join-Path $ArtifactDirectory 'watch-pause-icon-restored.png')) | Out-Null
        $pauseIcon = Read-ToolbarIconMask (Join-Path $ArtifactDirectory 'watch-pause-icon.png')
        $resumeIcon = Read-ToolbarIconMask (Join-Path $ArtifactDirectory 'watch-resume-icon.png')
        $restoredIcon = Read-ToolbarIconMask (Join-Path $ArtifactDirectory 'watch-pause-icon-restored.png')
        if ($pauseIcon -ceq $resumeIcon) { throw 'The rendered Pause and Resume icons are identical.' }
        if ($pauseIcon -cne $restoredIcon) { throw 'Resuming viewing did not restore the Pause icon.' }
        Press 'Stop watching'
        Wait-Ui { param($items) $items.name -contains 'Watch again' }
        Ui @('screenshot', 'WatchPicker', '--output', (Join-Path $ArtifactDirectory 'capture-stopped.png')) | Out-Null
    }

    Test-Ui 'Journal filter uses the right toolbar group' {
        Open-Menu 'Change journal' -Group 'Tools' -MainTab
        Assert-ToolToolbar
        $items = @(Read-Ui)
        $search = @($items | Where-Object automationId -eq 'ToolSearch')[0]
        $filter = @($items | Where-Object automationId -eq 'JournalFilters')[0]
        $view = @($items | Where-Object automationId -eq 'ToolListViewOptions')[0]
        if (-not $filter -or $filter.x -lt $search.x + $search.width -or $filter.x + $filter.width -gt $view.x) { throw 'Journal filters are not between search and view mode.' }
        if ([Math]::Abs(($filter.y + $filter.height / 2) - ($search.y + $search.height / 2)) -gt 1) { throw 'Journal filters are not centered on the toolbar row.' }
        Ui @('invoke', 'JournalFilters', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'JournalArchiveFilter' -and $items.automationId -contains 'JournalOperationFilter' }
        Ui @('invoke', 'JournalArchiveFilter', '--action', 'expand') | Out-Null
        $options = Read-ContextMenu 'Archived' -Types @('ListItem')
        $archived = @($options.Items | Where-Object name -eq 'Archived')[0]
        Ui @('invoke', $archived.selector, '--action', 'select') -Window $options.Window | Out-Null
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'JournalArchiveFilter' }
        $summary = Ui @('get-property', 'JournalFilters', '--property', 'HelpText', '--json') | ConvertFrom-Json
        if ($summary.properties.HelpText -notlike '*Archived*') { throw 'The relocated filter did not update journal history.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-filter-toolbar.png')) | Out-Null
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 760, 900, 0x16) | Out-Null
        Assert-ToolToolbar
    }

    Test-Ui 'Tool page design supports explicit captures and shared list presentation' {
        Open-Menu 'Watches' -Group 'Tools' -MainTab
        Wait-Ui { param($items) $items.automationId -contains 'NewWatchButton' }
        if ((Read-Ui).name -contains 'Choose capture' -or (Read-Ui).automationId -contains 'BeforeText') { throw 'An empty watch page exposes an empty capture picker or inspector.' }
        Ui @('invoke', 'NewWatchButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'RegistryTargetAddress' }
        Ui @('set-value', 'RegistryTargetAddress', '\\remote\HKLM\Software') | Out-Null
        Primary
        Wait-Ui { param($items) $items.name -contains 'Live watches support local registry keys only.' }
        Ui @('set-value', 'RegistryTargetAddress', $address) | Out-Null
        Ui @('invoke', 'WatchOneValue', '--action', 'toggle-on') | Out-Null
        Ui @('invoke', 'WatchDefaultValue', '--action', 'toggle-on') | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'RegistryTargetAddress' -and @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -like 'Watching*' }).Count -eq 1 }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('', 'Captured default value') } finally { $key.Dispose() }
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*(Default)*' }).Count -eq 1 }
        Select-Row '(Default)' | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'AfterText' }
        Assert-CaptureContext
        Assert-DetailStateAfterResize
        Set-Clipboard -Value 'compact action sentinel'
        Copy-DetailBytes 'After'
        Assert-Clipboard ([Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes("Captured default value`0"))) 'compact-value-action' 'compact action sentinel'
        Assert-ToolToolbar
        $more = @((Read-Ui) | Where-Object automationId -eq 'MoreButton')[0]
        Ui @('invoke', $more.selector, '--action', 'invoke') | Out-Null
        $overflow = Read-ContextMenu 'Clear observations' -Types @('Button','MenuItem')
        $command = @($overflow.Items | Where-Object name -eq 'Clear observations')[0]
        $leftEdge = $command.x
        $rightEdge = $command.x + $command.width
        if ($leftEdge -gt $more.x + $more.width + 24 -or $rightEdge -lt $more.x - 24) { throw 'The overflow menu is detached from its left-side button.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'left-overflow.png')) -Window $overflow.Window | Out-Null
        Close-ContextMenu $overflow
        Assert-PreviewResize
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 760, 900, 0x16) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.width -gt $_.height }).Count -eq 1 }
        Assert-ToolToolbar
        Assert-CaptureContext
        $horizontal = @((Read-Ui) | Where-Object automationId -eq 'ToolDetailsResize')[0]
        Ui @('send-keys', 'up', '--via', 'send-input', '--target', 'ToolDetailsResize') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.y -lt $horizontal.y - 10 }).Count -eq 1 }
        Press 'Pause viewing'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'WatchViewingStatus' -and $_.name -like 'Viewing paused*Capture continues' }).Count -eq 1 }
        if (@((Read-Ui) | Where-Object { $_.automationId -eq 'Context' -and $_.name -like 'Watching*' }).Count -ne 1) { throw 'Pausing the view obscured the running capture state.' }
        Press 'Resume viewing'
        Ui @('invoke', 'WatchFollowLatest', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'WatchViewingStatus' }
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolDetailsResize' -and $_.height -gt $_.width }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'watches-toolbar.png')) | Out-Null
        $retained = Read-Details
        Press 'Stop watching'
        Wait-Ui { param($items) $items.name -contains 'Watch again' }
        if ((Read-Details) -ne $retained) { throw 'Stopping capture discarded its selected evidence.' }
        Press 'Watch again'
        Wait-Ui { param($items) $items.name -contains 'Choose capture' -and $items.name -contains 'Waiting for changes' }
        $capture = Pick 'ComboBox' 'Choose capture'
        Ui @('invoke', $capture.selector, '--action', 'expand') | Out-Null
        $popup = Read-ContextMenu -Types @('ListItem')
        $stopped = @($popup.Items | Where-Object name -like '*Stopped*')
        if ($stopped.Count -ne 1) { throw 'Stopped capture is not distinguishable in the capture selector.' }
        Ui @('invoke', $stopped[0].selector, '--action', 'select') -Window $popup.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Watch again' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*(Default)*' }).Count -eq 1 }
        Select-Row '(Default)' | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'AfterText' }
        if ((Read-Details) -ne $retained) { throw 'Switching back to a stopped capture lost its observations.' }
        foreach ($mode in 'Stacked rows', 'Columns', 'Columns') {
            Ui @('invoke', 'ToolListViewOptions', '--action', 'invoke') | Out-Null
            $popup = Read-ContextMenu $mode
            $choice = @($popup.Items | Where-Object name -eq $mode)[0]
            Ui @('invoke', $choice.selector) -Window $popup.Window | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ToolListViewOptions' -and $_.name -eq ('List presentation: ' + $mode) }).Count -eq 1 }
            Wait-Ui { param($items) $items.automationId -contains 'AfterText' }
            if ((Read-Details) -ne $retained) { throw 'Changing list presentation lost selection.' }
        }
        Open-Menu 'Notes' -Group 'Tools' -MainTab
        Press 'New note…'
        if ((Pick 'Edit' 'Registry key').value) { throw 'New note silently inherited browser context.' }
        Ui @('set-value', 'RegistryTargetAddress', $address) | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' }
        Ui @('set-value', 'NoteEditor', 'Release note for the explicit key') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteStatus' -and $_.name -eq 'Saved' }).Count -eq 1 }
        Assert-ToolToolbar
        Assert-PreviewResize
        foreach ($page in 'Favorites','Change journal') {
            if ($page -eq 'Favorites') {
                Open-Menu $page -Group 'Tools'
                Fill 'Search tool items' 'preview-search-check'
                Fill 'Search tool items' ''
                Press 'Open tool in tab'
            } else { Open-Menu $page -Group 'Tools' -MainTab }
            Assert-ToolToolbar
            if ((Read-Ui).automationId -notcontains 'ToolListViewOptions') { throw "$page is missing the shared list presentation control." }
            if ($page -eq 'Favorites') {
                Set-Preview $true
                Assert-PreviewResize
                Set-Preview $false
            }
        }
        foreach ($page in 'Saved searches','Workspaces') {
            Open-Menu $page -Group 'Tools'
            Assert-ToolToolbar
            $items = @(Read-Ui)
            if ($items.automationId -notcontains 'ToolListViewOptions' -or $items.automationId -notcontains 'ToolSearch') { throw "$page is missing the shared toolbar." }
        }
        Press 'Save workspace…'; Fill 'Workspace name' 'Toolbar preview fixture'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Toolbar preview fixture*' }).Count -eq 1 }
        Select-Row 'Toolbar preview fixture' | Out-Null
        Assert-PreviewResize
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'workspace-toolbar.png')) | Out-Null
        Browser
        Fill 'Search registry' 'needle'; Press 'Start search'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.name -contains 'Find: needle' }
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.automationId -contains 'SearchResultFilter' }
        Ui @('set-value', 'SearchResultFilter', 'NeedleOne') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleOne*' }).Count -eq 1 }
        Assert-ToolToolbar
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.name -contains 'App theme' -and $items.name -contains 'Registry text' }
    }

    Test-Ui $workspaceRefresh {
        Open-Menu 'Workspaces' -Group 'Tools'
        Select-Row 'Bulk workspace 001' | Out-Null
        $viewport = Get-ResultViewport 'Investigation results' 'Bulk workspace 001'
        Press 'Save workspace…'
        Fill 'Workspace name' 'Bulk workspace 002'
        Primary
        Wait-Ui { param($items) $items.name -contains 'Workspace saved' }
        Assert-ResultViewport $viewport
        if ((Read-Details) -notlike '*Bulk workspace 001*' -or -not (Pick 'Button' 'Open workspace').isEnabled) {
            throw 'Refreshing workspaces lost its offscreen selection.'
        }
        $list = Pick 'List' 'Investigation results'
        Ui @('scroll', $list.selector, '--to', 'top') | Out-Null
        $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Bulk workspace 001,*' })[0]
        $selection = Ui @('get-property', $row.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        if ($selection.properties.IsSelected -ne 'True') { throw 'The surviving workspace lost its list selection.' }
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Press 'Remove selected'
        Wait-Ui { param($items) $items.name -contains 'No saved workspaces' }
        Open-Menu 'Close tab'
    }

    Test-Ui 'Bulk grouped journal refresh preserves selection and scroll' {
        Open-Menu 'Change journal' -Group 'Tools' -MainTab
        $folder = Join-Path $dataFolder 'Journal'
        $null = [IO.Directory]::CreateDirectory($folder)
        $day = [DateTimeOffset]::Now.Date.AddDays(-5).AddHours(12)
        $files = foreach ($n in 1..97) {
            $id = [guid]::NewGuid()
            $name = 'Bulk history {0:D3}' -f $n
            $entry = @{ Id = $id; Operation = 'Bulk activity'; Time = $day.AddDays([Math]::Floor($n / 20)).AddMinutes($n).ToString('o')
                Before = @(); After = @(); Activity = $name; ActivityLocation = @{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = "$subKey\$name" } }
            $file = Join-Path $folder ($id.ToString('N') + '.json')
            if ($n -le 80) { [IO.File]::WriteAllText($file, ($entry | ConvertTo-Json -Compress)) }
            @{ Path = $file; Entry = $entry }
        }
        Press 'Refresh'
        Wait-Ui { param($items) $items.name -contains '80 items' }
        Select-Row 'Bulk history 080' | Out-Null
        $viewport = Get-ResultViewport 'Tool items' 'Bulk history 080'
        foreach ($file in $files[80..95]) { [IO.File]::WriteAllText($file.Path, ($file.Entry | ConvertTo-Json -Compress)) }
        Press 'Refresh'
        Wait-Ui { param($items) $items.name -contains '96 items · 1 selected' }
        Assert-ResultViewport $viewport
        if ((Read-Details) -notlike '*Bulk history 080*') { throw 'The grouped journal selected a different activity after reloading the files.' }
        $row = Pick 'ListItem' $viewport.Name
        Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
        $details = Read-Details
        $list = Pick 'List' 'Tool items'
        Ui @('scroll', $list.selector, '--to', 'top') | Out-Null
        [IO.File]::WriteAllText($files[96].Path, ($files[96].Entry | ConvertTo-Json -Compress))
        Press 'Refresh'
        Wait-Ui { param($items) $items.name -contains '97 items · 1 selected' }
        if (-not ((Read-Ui).name -like '*Bulk history 097*') -or (Read-Details) -cne $details) {
            throw 'Refreshing at the top of the journal lost the newest row or its offscreen selection.'
        }
        Open-Menu 'Close tab'
        foreach ($file in $files) { [IO.File]::Delete($file.Path) }
    }

    Test-Ui 'Settings and Favorites keep their menu after closing the last registry tab' {
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Browser
        Open-Menu 'Close tab'
        foreach ($page in 'Settings', 'Favorites') {
            if ($page -eq 'Favorites') { Open-Menu 'Favorites' 'Tools' -MainTab }
            Wait-Ui { param($items) $items.automationId -notcontains 'EntriesList' -and @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $page }).Count -eq 1 }
            Ui @('send-keys', 'f10', '--via', 'send-input', '--target', 'AddButton') | Out-Null
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            if ($focused.element.automationId -ne 'GlobalMenuButton') { throw "F10 missed the $page menu." }
            Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', 'AddButton') | Out-Null
            $popup = Read-ContextMenu 'New tab'
            if ($popup.Items.name -notcontains 'Tools') { throw "Tools is missing from the $page menu." }
            Close-ContextMenu $popup
        }
        Open-Menu 'Workspaces' -Group 'Tools'
        Wait-Ui { param($items) $items.name -contains 'Save workspace…' }
        Open-Menu 'New tab'
        Expect-HistoryLocation 'Computer'
        foreach ($name in 'Workspaces', 'Favorites', 'Settings') {
            Ui @('invoke', (Pick 'TabItem' $name).selector, '--action', 'select') | Out-Null
            Open-Menu 'Close tab'
        }
        Expect-HistoryLocation 'Computer'
        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        $input = Pick 'Edit' 'Address'
        Ui @('set-value', $input.selector, $address) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
        Expect-HistoryLocation $address
    }

    Test-Ui 'Copy key path shortcut follows focused registry context' {
        View-Option 'Show keys in list'
        $child = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child, Key,*' })[0]
        if (-not $child) { throw 'Missing child key for shortcut test.' }
        Ui @('invoke', $child.selector, '--action', 'select') | Out-Null
        Set-Clipboard -Value 'registry path shortcut sentinel'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $child.selector) | Out-Null
        Assert-Clipboard ($address + '\Child') 'path-list-key'
        $tree = Pick 'TreeItem' $address
        Set-Clipboard -Value 'registry path shortcut sentinel'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $tree.selector) | Out-Null
        Assert-Clipboard $address 'path-tree-key'
        Set-Clipboard -Value 'registry path shortcut sentinel'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $child.selector) | Out-Null
        Set-Clipboard -Value 'registry path shortcut sentinel'
        $searchEditor = Pick 'Edit' 'Search registry'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $searchEditor.selector) | Out-Null
        Assert-Clipboard $address 'path-outside-list'
        $value = Select-Row 'Counter'
        Set-Clipboard -Value 'registry path shortcut sentinel'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $value.selector) | Out-Null
        Assert-Clipboard $address 'path-value-container'
        View-Option 'Show keys in list'
    }

    Test-Ui 'Registry Ctrl C and V follow divergent tree and list targets' {
        Browser
        $clipboardPath = $subKey + '\ClipboardRouting'
        $clipboardAddress = $address + '\ClipboardRouting'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($clipboardPath)
        try {
            $source = $key.CreateSubKey('CtrlSource')
            try { $source.SetValue('RawBytes', [byte[]](0, 255, 42), [Microsoft.Win32.RegistryValueKind]::Binary) }
            finally { $source.Dispose() }
            $tree = $key.CreateSubKey('CtrlTree'); $tree.Dispose()
            $list = $key.CreateSubKey('CtrlList')
            try { $list.SetValue('ListMarker', 'list clipboard text') }
            finally { $list.Dispose() }
        } finally { $key.Dispose() }
        Go-ToKey ($clipboardAddress + '\CtrlTree')
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq ($clipboardAddress + '\CtrlTree') }).Count -eq 1 }
        View-Option 'Automatically sync tree'
        Go-ToKey ($clipboardAddress + '\CtrlList')
        $row = Select-Row 'ListMarker'
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-Clipboard 'list clipboard text' 'registry-list-value'
        $tree = Pick 'TreeItem' ($clipboardAddress + '\CtrlTree')
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input', '--target', $tree.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Paste into CtrlTree' }
        if ((Pick 'Edit' 'Name of the copy').value -ne 'ListMarker') { throw 'Tree Paste did not receive the list value clipboard.' }
        Fill 'Name of the copy' 'FromList'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($clipboardPath + '\CtrlTree')
        try { if ($key.GetValue('FromList') -cne 'list clipboard text') { throw 'Tree Ctrl+V wrote to the current list container.' } }
        finally { $key.Dispose() }
        if (@(Read-Ui | Where-Object automationId -match '^Breadcrumb\d+$')[-1].name -ne 'CtrlList') { throw 'Tree Paste navigated away from the current list.' }

        $tree = Pick 'TreeItem' ($clipboardAddress + '\CtrlTree')
        Ui @('send-keys', 'up', '--via', 'send-input', '--target', $tree.selector) | Out-Null
        $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
        if ($focused.name -ne ($clipboardAddress + '\CtrlSource')) { throw 'Keyboard tree selection did not reach the independent source.' }
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
        Assert-Clipboard ($clipboardAddress + '\CtrlSource') 'registry-tree-key'
        Ui @('send-keys', 'down', '--via', 'send-input') | Out-Null
        $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
        if ($focused.name -ne ($clipboardAddress + '\CtrlTree')) { throw 'Keyboard tree selection did not return to its destination.' }
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Paste into CtrlTree' }
        if ((Pick 'Edit' 'Name of the copy').value -ne 'CtrlSource') { throw 'Tree Ctrl+C copied only text instead of a pasteable key.' }
        Fill 'Name of the copy' 'TreeClone'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($clipboardPath + '\CtrlTree\TreeClone')
        try {
            if (-not $key -or [Convert]::ToHexString($key.GetValue('RawBytes')) -ne '00FF2A') { throw 'Tree key Paste lost the copied registry data.' }
        } finally { if ($key) { $key.Dispose() } }
        $row = Select-Row 'ListMarker'
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Paste into CtrlList' }
        Fill 'Name of the copy' 'ListClone'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($clipboardPath + '\CtrlList\ListClone')
        try {
            if (-not $key -or [Convert]::ToHexString($key.GetValue('RawBytes')) -ne '00FF2A') { throw 'List Ctrl+V did not paste into its current container.' }
        } finally { if ($key) { $key.Dispose() } }
        $row = Select-Row 'ListMarker'
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Go-ToKey ($clipboardAddress.ToLowerInvariant() + '\ctrltree')
        $tree = Pick 'TreeItem' ($clipboardAddress + '\CtrlTree')
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input', '--target', $tree.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Name of the copy' }
        Fill 'Name of the copy' 'CaseAliasPaste'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'CaseAliasPaste,*' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'clipboard-divergent-targets.png')) | Out-Null
        View-Option 'Automatically sync tree'
        Go-ToKey $address
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteSubKeyTree('ClipboardRouting') }
        finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Registry clipboard shortcuts preserve text editing and modified combinations' {
        Browser
        function Assert-NativeClipboard([string] $Name, [string] $Text) {
            Fill $Name $Text
            $field = Pick 'Edit' $Name
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $field.selector) | Out-Null
            Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
            $evidenceName = 'native-' + ($Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')
            Assert-Clipboard $Text ($evidenceName + '-copy')
            $pastedText = $Text + ' pasted from guest clipboard'
            Set-Clipboard -Value $pastedText
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
            Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq $Name -and $_.value -ceq $pastedText }).Count -eq 1 }
        }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-Clipboard '0x00000001 (1)' 'registry-counter-seed'
        Set-Clipboard -Value 'modified shortcuts sentinel'
        foreach ($keys in @('ctrl+alt+c', 'ctrl+alt+v', 'ctrl+shift+v', 'ctrl+shift+i', 'ctrl+alt+i')) {
            Ui @('send-keys', $keys, '--via', 'send-input', '--target', $row.selector) | Out-Null
        }
        if (@(Read-Ui | Where-Object automationId -eq 'PrimaryButton').Count) { throw 'A modified clipboard combination invoked a registry command.' }
        Assert-Clipboard 'modified shortcuts sentinel' 'modified-registry-shortcuts'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-Clipboard $address 'registry-path-only'
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Name of the copy' }
        if ((Pick 'Edit' 'Name of the copy').value -ne 'Counter') { throw 'Path-only Copy replaced the registry clipboard.' }
        Assert-NativeClipboard 'Name of the copy' 'native dialog text'
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Press 'Filter'
        Assert-NativeClipboard 'Filter this key' 'native filter text'
        Press 'Clear filter'
        Ui @('send-keys', 'ctrl+l', '--via', 'send-input', '--target', 'ToolsButton') | Out-Null
        Assert-NativeClipboard 'Address' 'native address text'
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Press 'Notes for this key'
        $previousNote = (Pick 'Edit' 'Local note').value
        Assert-NativeClipboard 'Local note' 'native note text'
        Fill 'Local note' $previousNote
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'clipboard-native-text.png')) | Out-Null
        Press 'Close tool panel'
    }

    Test-Ui 'Registry sort exposes column direction and preserves keyboard focus' {
        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.SetValue('SortFixtureA', 'zulu')
            $key.SetValue('SortFixtureB', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('SortFixtureC', 'alpha')
        } finally { $key.Dispose() }
        function Assert-Sort([string] $Column, [string] $Direction, [string] $Order) {
            Wait-Ui {
                param($items)
                $state = Read-SortStatus $Column
                $rows = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'SortFixture*' })
                $actualOrder = ($rows | ForEach-Object { ($_.name -split ',')[0] }) -join ','
                $state -ceq ('Sorted ' + $Direction) -and $actualOrder -ceq $Order
            }
            foreach ($name in @('Name', 'Type', 'Data')) {
                $state = Read-SortStatus $name
                $expected = if ($name -eq $Column) { 'Sorted ' + $Direction } else { 'Not sorted' }
                if ($state -cne $expected) { throw "Incorrect accessible $name sort state: $state." }
            }
            $rows = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'SortFixture*' })
            $actualOrder = ($rows | ForEach-Object { ($_.name -split ',')[0] }) -join ','
            if ($actualOrder -cne $Order) { throw 'The displayed row order disagrees with the sort state.' }
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            if (-not $focused.hasFocus -or $focused.element.automationId -ne ($Column + 'SortButton')) { throw 'Sorting moved focus away from its column button.' }
        }
        Press 'Refresh'; Press 'Filter'; Fill 'Filter this key' 'SortFixture'
        Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'NameSortButton') | Out-Null
        Assert-Sort 'Name' 'descending' 'SortFixtureC,SortFixtureB,SortFixtureA'
        Ui @('send-keys', 'tab space', '--via', 'send-input') | Out-Null
        Assert-Sort 'Type' 'ascending' 'SortFixtureB,SortFixtureA,SortFixtureC'
        Ui @('send-keys', 'tab enter', '--via', 'send-input') | Out-Null
        Assert-Sort 'Data' 'ascending' 'SortFixtureB,SortFixtureC,SortFixtureA'
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Assert-Sort 'Data' 'descending' 'SortFixtureA,SortFixtureC,SortFixtureB'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-sort-keyboard.png')) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', 'NameSortButton') | Out-Null
        Press 'Clear filter'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { foreach ($name in @('SortFixtureA', 'SortFixtureB', 'SortFixtureC')) { $key.DeleteValue($name) } }
        finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Registry list header chooses and reorders columns and preserves the grouped rows after restart' {
        Browser
        $original = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (-not $original.ShowKeysInList) { View-Option 'Show keys in list' }
        if (-not $original.ShowParentKeyInList) { View-Option 'Show parent key ..' }
        Header-Command 'Name' 'Children' -Keyboard
        Wait-Ui { param($items) $items.automationId -contains 'ChildrenSortButton' -and $items.name -contains '0 keys · 0 values' } -All
        Header-Command 'Name' 'Type'
        Wait-Ui { param($items) $items.automationId -notcontains 'TypeSortButton' }
        Header-Command 'Children' 'Move column left' -Keyboard
        Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).ListColumns -join ',' -ceq 'Name,Children,Data' }
        $nameButton = @(Read-Ui | Where-Object automationId -eq 'NameSortButton')[0]
        $destination = '{0},{1}' -f [int]($nameButton.x + $nameButton.width / 2), [int]($nameButton.y + $nameButton.height / 2)
        Ui @('drag', 'ChildrenSortButton', $destination) | Out-Null
        Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).ListColumns -join ',' -ceq 'Children,Name,Data' }
        Select-Row 'Counter' | Out-Null
        Ui @('send-keys', 'enter', '--target', 'ChildrenSortButton', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'enter', '--target', 'ChildrenSortButton', '--via', 'send-input') | Out-Null
        Wait-Ui {
            param($items)
            $list = @($items | Where-Object automationId -eq 'EntriesList')[0]
            $rows = @(Elements $list.children | Where-Object type -eq 'ListItem')
            $rows.Count -ge 4 -and $rows[0].name.StartsWith('..,') -and $rows[1].name.StartsWith('Child,') -and
                $rows[2].name.StartsWith('(Default),') -and (Read-SortStatus 'Children') -eq 'Sorted descending'
        }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $id = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'EntriesList')
        $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $id)
        $selected = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($selected.Count -ne 1 -or $selected[0].Current.Name -notlike 'Counter,*') { throw 'Sorting lost the selected value.' }
        Restart-Editor
        Wait-Ui {
            param($items)
            $headers = @($items | Where-Object automationId -in 'ChildrenSortButton','NameSortButton','DataSortButton' | Sort-Object x)
            $headers.automationId -join ',' -ceq 'ChildrenSortButton,NameSortButton,DataSortButton' -and
                $items.automationId -notcontains 'TypeSortButton' -and (Read-SortStatus 'Children') -eq 'Sorted descending'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-column-order.png')) | Out-Null
        Header-Command 'Name' 'Children'
        Header-Command 'Name' 'Type'
        Header-Command 'Type' 'Move column left'
        if (-not $original.ShowParentKeyInList) { View-Option 'Show parent key ..' }
        if (-not $original.ShowKeysInList) { View-Option 'Show keys in list' }
    }

    Test-Ui 'Registry metadata columns display key notes, last writes, value sizes and native link targets' {
        Browser
        $original = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if ($original.ShowKeysInList) { View-Option 'Show keys in list' }
        Header-Command 'Name' 'Key last modified'
        Wait-Ui {
            param($items)
            $stamps = foreach ($name in '(Default)', 'Counter', 'NeedleOne') {
                $row = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith("$name,") })[0]
                $stamp = @(Elements $row.children | Where-Object { $_.type -eq 'Text' -and $_.name -match '\d{1,2}:\d{2}:\d{2}' })
                if ($stamp.Count -ne 1) { return $false }
                $stamp[0].name
            }
            @($stamps | Select-Object -Unique).Count -eq 1
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-key-last-modified.png')) | Out-Null
        View-Option 'Show keys in list'
        Go-ToKey "$address\Child"
        Press 'Notes for this key'
        $oldNote = (Pick 'Edit' 'Local note').value
        Fill 'Local note' 'Metadata column fixture'
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Press 'Close tool panel'
        Go-ToKey $address
        $columns = 'Notes','Size (bytes)','Link target'
        foreach ($column in $columns) { Header-Command 'Name' $column }
        Wait-Ui {
            param($items)
            $child = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child, Key,*' })[0]
            $counter = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' })[0]
            $text = @(Elements $child.children | Where-Object type -eq 'Text').name
            $text -contains 'Metadata column fixture' -and @($text -match '\d{1,2}:\d{2}').Count -gt 0 -and
                @(Elements $counter.children | Where-Object type -eq 'Text').name -contains '4' -and
                @(Elements $child.children | Where-Object { $_.automationId -eq 'KeyNoteMarker' -and $_.name -eq 'Open key note' }).Count -eq 1
        } -All
        $child = @(Read-Ui -All | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child, Key,*' })[0]
        $marker = @(Elements $child.children | Where-Object automationId -eq 'KeyNoteMarker')[0]
        Ui @('invoke', $marker.selector, '--action', 'invoke') | Out-Null
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Metadata column fixture' }).Count -eq 1 -and
                @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child, Key,*' }).Count -eq 1
        }
        Press 'Close tool panel'
        Go-ToKey 'HKEY_LOCAL_MACHINE\SYSTEM'
        Wait-Ui {
            param($items)
            $link = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'CurrentControlSet, Key,*' })[0]
            @(Elements $link.children | Where-Object { $_.type -eq 'Text' -and $_.name -like '\Registry\Machine\System\ControlSet*' }).Count -eq 1
        } -All
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-key-metadata.png')) | Out-Null
        foreach ($column in @('Key last modified') + $columns) { Header-Command 'Name' $column }
        Go-ToKey "$address\Child"
        Press 'Notes for this key'
        Fill 'Local note' ([string]$oldNote)
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Press 'Close tool panel'
        Go-ToKey $address
        if (-not $original.ShowKeysInList) { View-Option 'Show keys in list' }
    }

    Test-Ui 'Registry list uses the custom font only for Data and hides its toolbar across restart' {
        Browser
        function Cell-Heights {
            $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'NeedleOne,*' })[0]
            $cells = @(Elements $row.children | Where-Object type -eq 'Text')
            $heights = @{}
            foreach ($label in 'NeedleOne','REG_SZ','needle one') {
                $cell = @($cells | Where-Object name -ceq $label)
                if ($cell.Count -ne 1 -or $cell[0].height -le 0) { throw "Missing measured cell '$label'." }
                $heights[$label] = $cell[0].height
            }
            $heights
        }
        $before = Cell-Heights
        Open-Menu 'Settings'
        $expand = Pick 'Button' 'Data column font'
        Ui @('invoke', $expand.selector, '--action', 'expand') | Out-Null
        Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'CustomDataFontSwitch') | Out-Null
        $family = Pick 'ComboBox' 'Data column font family'
        Set-TestWindowForeground
        [UxTestWindow]::LeftClickAt([int]($family.x + 20), [int]($family.y + $family.height / 2))
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'Courier New', '--verbatim', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        $sizeBox = @(Read-Ui -All | Where-Object automationId -eq 'DataFontSizeBox')[0]
        $up = @(Elements $sizeBox.children | Where-Object automationId -eq 'UpSpinButton')[0]
        foreach ($step in 1..5) { Ui @('invoke', $up.selector, '--action', 'invoke') | Out-Null }
        Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'ListToolbarSwitch') | Out-Null
        Wait-Ui {
            param($items)
            $prefs = Get-Content $settingsFile -Raw | ConvertFrom-Json
            $prefs.ShowListToolbar -eq $false -and $prefs.DataFont.Family -ceq 'Courier New' -and $prefs.DataFont.Size -eq $prefs.FontSize + 5
        }
        Browser
        Wait-Ui { param($items) $items.automationId -notcontains 'Commands' -and $items.automationId -contains 'EntriesList' }
        $after = Cell-Heights
        if ($after['needle one'] -le $before['needle one'] -or $after.NeedleOne -ne $before.NeedleOne -or $after.REG_SZ -ne $before.REG_SZ) {
            throw 'The Data font size did not change independently of Name and Type.'
        }
        Ui @('send-keys', 'ctrl+alt+f', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        $filter = Pick 'Edit' 'Filter this key'
        Ui @('send-keys', 'escape', '--via', 'send-input', '--target', $filter.selector) | Out-Null
        Restart-Editor
        if (@(Read-Ui | Where-Object automationId -eq 'Commands').Count -or (Cell-Heights)['needle one'] -ne $after['needle one']) {
            throw 'Restart lost the compact list or its Data font.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-data-font-compact.png')) | Out-Null
        Open-Menu 'Settings'
        Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'ListToolbarSwitch') | Out-Null
        $expand = Pick 'Button' 'Data column font'
        Ui @('invoke', $expand.selector, '--action', 'expand') | Out-Null
        Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'CustomDataFontSwitch') | Out-Null
        Open-Menu 'Close tab'
        Browser
        Wait-Ui { param($items) $items.automationId -contains 'Commands' }
        if ((Cell-Heights)['needle one'] -ne $before['needle one']) { throw 'Data did not return to the standard UI font.' }
    }

    Test-Ui 'Export completion offers its folder without moving the workspace' {
        Browser
        $before = @(Read-Ui | Where-Object automationId -eq 'EntriesList')[0]
        $output = Join-Path $ArtifactDirectory 'completed export.reg'
        Press 'Export…'; File-Picker 'Save' $output
        Wait-Ui { param($items) $items.name -contains 'Export complete' -and $items.name -contains 'Open folder' }
        $items = Read-Ui
        $after = @($items | Where-Object automationId -eq 'EntriesList')[0]
        if (-not $before -or -not $after -or $before.x -ne $after.x -or $before.y -ne $after.y -or
            $before.width -ne $after.width -or $before.height -ne $after.height) {
            throw 'The export toast changed the registry workspace geometry.'
        }
        if (-not [IO.File]::Exists($output)) { throw 'The export completion appeared before the output existed.' }
        $folder = @($items | Where-Object { $_.name -eq 'Open folder' -and $_.automationId -like 'NotificationAction_*' })
        if ($folder.Count -ne 1 -or -not $folder[0].isEnabled) { throw 'The completed export has no usable folder action.' }
        $folderId = $folder[0].automationId
        Ui @('send-keys', 'f5', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains $folderId }
        Go-ToKey 'HKEY_CURRENT_USER\Software'
        Wait-Ui { param($items) $items.automationId -contains $folderId }
        Go-ToKey $address
        Wait-Ui { param($items) $items.automationId -contains $folderId }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'export-folder-action.png')) | Out-Null
        Open-Menu 'Recent notifications'
        Wait-Ui { param($items) $items.automationId -contains 'NotificationHistoryList' -and $items.name -contains 'Open folder' }
        Ui @('invoke', 'DismissAllNotifications', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'NotificationHistoryList' }
    }

    Test-Ui 'Browser status emits completion cancel and error notifications without moving focus' {
        Browser
        function Wait-Notification($Observer, [string] $Title) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            do {
                if (@($Observer.Events | Where-Object { $_.Text.Contains($Title, [StringComparison]::Ordinal) }).Count) { return }
                Start-Sleep -Milliseconds 100
            } while ($timer.Elapsed.TotalSeconds -lt 10)
            throw "No UIA Notification event contained '$Title'."
        }
        $observer = [UxNotificationObserver]::new([IntPtr]$mainWindow)
        try {
            $workspaceBefore = @(Read-Ui | Where-Object automationId -eq 'EntriesList')[0]
            $snapshotFile = Join-Path $ArtifactDirectory 'status-notification.regsnap'
            Snapshot-Action 'Capture branch snapshot…'
            File-Picker 'Save' $snapshotFile
            Wait-Notification $observer 'Snapshot saved'
            if (@($observer.Events | Where-Object { $_.Text.Contains('Snapshot saved') }).Count -ne 1) { throw 'Snapshot completion was announced more than once.' }
            $workspaceAfter = @(Read-Ui | Where-Object automationId -eq 'EntriesList')[0]
            if (-not $workspaceBefore -or -not $workspaceAfter -or
                $workspaceBefore.x -ne $workspaceAfter.x -or $workspaceBefore.y -ne $workspaceAfter.y -or
                $workspaceBefore.width -ne $workspaceAfter.width -or $workspaceBefore.height -ne $workspaceAfter.height) {
                throw 'A completion toast changed the registry workspace geometry.'
            }
            $before = @($observer.Events | Where-Object { $_.Activity -in 'RegistryStatus','InAppNotification' }).Count
            Press 'Filter'; Fill 'Filter this key' 'Counter'
            Select-Row 'Counter' | Out-Null
            if (@($observer.Events | Where-Object { $_.Activity -in 'RegistryStatus','InAppNotification' }).Count -ne $before) { throw 'Filtering or row selection announced an operation status.' }
            Press 'Clear filter'

            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $key.SetValue('CancelPayload', [byte[]]::new(16 * 1024 * 1024), [Microsoft.Win32.RegistryValueKind]::Binary) }
            finally { $key.Dispose() }
            $exported = Join-Path $ArtifactDirectory 'canceled-status.reg'
            try {
                $observer.CancelNextExport = $true
                Press 'Export…'; File-Picker 'Save' $exported
                Wait-Notification $observer 'Exporting'
                $null = [Threading.SpinWait]::SpinUntil(
                    [Func[bool]] { $observer.CancelWasInvoked -or $observer.CancelError.Length -gt 0 }, 10000)
                if (-not $observer.CancelWasInvoked) { throw "The progress action did not run: $($observer.CancelError)" }
                Wait-Notification $observer 'Export canceled'
                if ([IO.File]::Exists($exported) -and ([IO.FileInfo]::new($exported)).Length -gt 0) { throw 'Canceled export committed nonempty output.' }
            } finally {
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
                try { $key.DeleteValue('CancelPayload', $false) }
                finally { $key.Dispose() }
            }
            $errorKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey + '\StatusError')
            $errorKey.Dispose()
            Go-ToKey ($address + '\StatusError')
            Press 'Filter'; Fill 'Filter this key' 'status filter'
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $key.DeleteSubKeyTree('StatusError') }
            finally { $key.Dispose() }
            Ui @('send-keys', 'f5', '--via', 'send-input') | Out-Null
            Wait-Notification $observer "Couldn't complete the operation"
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            if (-not $focused.hasFocus -or $focused.element.automationId -ne 'FilterBox') { throw 'An operation error announcement stole keyboard focus.' }
            $errors = @($observer.Events | Where-Object { $_.Text.Contains("Couldn't complete the operation") })
            if ($errors.Count -ne 1 -or $errors[0].Processing -ne 1) { throw 'The error did not produce one important UIA notification.' }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'status-notification-error.png')) | Out-Null
            Open-Menu 'Recent notifications'
            Wait-Ui { param($items) $items.automationId -contains 'NotificationHistoryList' }
            if (-not (Read-Ui | Where-Object { $_.name -eq "Couldn't complete the operation" })) {
                throw 'Application-owned history lost the active error.'
            }
            Ui @('invoke', 'DismissAllNotifications', '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) $items.automationId -notcontains 'NotificationHistoryList' }
            Go-ToKey $address
        } finally {
            $observer.Dispose()
            $observer.Events | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'status-notifications.json') -Encoding utf8
        }
    }

    Test-Ui 'Browser shortcuts open Settings and toolbar menus' {
        [UxTestWindow]::AssertMenuChimeSuppressed($mainWindow)
        Ui @('send-keys', 'ctrl+vk=0xbc', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Ui @('send-keys', 'ctrl+vk=0xbc', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -ne 1) {
            throw 'Ctrl+comma opened duplicate Settings tabs.'
        }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq 1 }

        foreach ($case in @(@('ctrl+n', 'Key'), @('alt+n', 'Key'), @('alt+v', 'Value preview'), @('alt+m', 'Selection'))) {
            Ui @('send-keys', $case[0], '--via', 'send-input', '--target', 'EntriesList') | Out-Null
            $popup = Read-ContextMenu $case[1] -Types @('MenuItem', 'Button')
            Close-ContextMenu $popup
        }
        Ui @('send-keys', 'alt+i', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FilterBox' -and $items.automationId -contains 'FilterCount' }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'FilterCount' }
        Ui @('send-keys', 'alt+e', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Address' }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'alt+a', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Find in registry' }
        Press 'Cancel'
        Wait-Ui { param($items) $items.name -notcontains 'Find in registry' }

        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            foreach ($shortcut in 'f5', 'alt+r') {
                $key.SetValue('ShortcutRefresh', $shortcut)
                Ui @('send-keys', $shortcut, '--via', 'send-input', '--target', 'EntriesList') | Out-Null
                Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "ShortcutRefresh,*$shortcut*" }).Count -eq 1 }
            }
        } finally {
            $key.DeleteValue('ShortcutRefresh', $false)
            $key.Dispose()
            Ui @('send-keys', 'f5', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        }
        Ui @('send-keys', 'alt+t', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'CloseToolButton' }
        Ui @('send-keys', 'alt+t', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'CloseToolButton' }
        Ui @('send-keys', 'vk=0x12', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'browser-access-keys.png')) | Out-Null
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
    }

    Test-Ui 'Ctrl N respects list focus Computer and hidden toolbar' {
        Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        $before = Ui @('get-focused', '--json') | ConvertFrom-Json
        Ui @('send-keys', 'ctrl+n', '--via', 'send-input') | Out-Null
        $after = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $after.hasFocus -or $after.element.selector -ne $before.element.selector) { throw 'Ctrl+N left the search field.' }

        Go-ToKey 'Computer'
        Ui @('focus', 'EntriesList') | Out-Null
        $before = Ui @('get-focused', '--json') | ConvertFrom-Json
        Ui @('send-keys', 'ctrl+n', '--via', 'send-input') | Out-Null
        $after = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $after.hasFocus -or $after.element.selector -ne $before.element.selector) { throw 'Ctrl+N opened New at Computer.' }
        Go-ToKey $address

        $originalToolbar = (Get-Content $settingsFile -Raw | ConvertFrom-Json).ShowListToolbar
        try {
            Open-Menu 'Settings'
            Ui @('invoke', 'ListToolbarSwitch', '--action', 'toggle-off') | Out-Null
            Browser
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'New' }).Count -eq 0 }
            Ui @('send-keys', 'ctrl+n', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
            $popup = Read-ContextMenu 'Key'
            Close-ContextMenu $popup
        } finally {
            Open-Menu 'Settings'
            $action = if ($originalToolbar) { 'toggle-on' } else { 'toggle-off' }
            Ui @('invoke', 'ListToolbarSwitch', '--action', $action) | Out-Null
            Open-Menu 'Close tab'
            Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
        }
    }

    Test-Ui 'Application menu stays in page headers with keyboard access' {
        function Assert-PageMenu {
            $items = Read-Ui
            $menu = @($items | Where-Object automationId -eq 'GlobalMenuButton')
            $tab = @($items | Where-Object type -eq 'TabItem')[0]
            if ($menu.Count -ne 1 -or $menu[0].y -lt $tab.y + $tab.height) { throw 'Expected one application menu below the title bar.' }
            Ui @('send-keys', 'f10', '--via', 'send-input', '--target', 'AddButton') | Out-Null
            $focus = Ui @('get-focused', '--json') | ConvertFrom-Json
            if (-not $focus.hasFocus -or $focus.element.automationId -ne 'GlobalMenuButton') { throw 'F10 did not focus the active page menu.' }
            Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', 'AddButton') | Out-Null
            $popup = Read-ContextMenu
            if ($popup.Items.name -notcontains 'Settings') { throw 'Alt+F did not open the application menu.' }
            Close-ContextMenu $popup
        }
        $permissions = Pick 'Button' 'Permissions…'
        $view = Pick 'Button' 'View'
        if (-not $permissions.isEnabled -or [Math]::Abs($permissions.y - $view.y) -gt 2) { throw 'Permissions is not available on the list toolbar.' }
        Assert-PageMenu
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-actions.png')) | Out-Null
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Assert-PageMenu
        Open-Menu 'Favorites' 'Tools' -MainTab
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        Assert-PageMenu
        Open-Menu 'Workspaces' -Group 'Tools'
        Wait-Ui { param($items) $items.name -contains 'Save workspace…' }
        Assert-PageMenu
        foreach ($index in 1..3) { Open-Menu 'Close tab' }
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq 1 }
    }

    Test-Ui 'Keyboard shortcut reference shares Settings and returns to preferences' {
        function Assert-ShortcutCentering($items) {
            $viewport = $items | Where-Object automationId -eq 'ShortcutsScroll'
            $action = $items | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Open the application menu' }
            $keys = $items | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Alt+F' }
            if (-not $viewport -or -not $action -or -not $keys) { throw 'Missing shortcut layout bounds.' }
            $center = ($action.x + $keys.x + $keys.width) / 2
            if ([Math]::Abs($center - ($viewport.x + $viewport.width / 2)) -gt 4) {
                throw 'Shortcut cards are not centered in the viewport.'
            }
        }
        $before = [IO.File]::ReadAllText($settingsFile)
        Set-TestWindowForeground
        Open-Menu 'Keyboard shortcuts'
        Wait-Ui { param($items) $items.automationId -contains 'ShortcutsBackButton' }
        $items = Read-Ui -All
        if ($items.automationId -contains 'PrimaryButton') { throw 'Shortcut reference still opened a modal dialog.' }
        foreach ($label in @('Tabs and navigation', 'Search and filtering', 'Mouse actions', 'Ctrl+T', 'Ctrl+Shift+C')) {
            if ($items.name -notcontains $label) { throw "Missing shortcut reference entry '$label'." }
        }
        if ($items.name -contains 'Ctrl+Shift+P') { throw 'Shortcut reference advertises the reserved command-palette binding.' }
        Open-Menu 'Keyboard shortcuts'
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -ne 1) { throw 'Shortcut reference opened duplicate Settings tabs.' }
        Assert-ShortcutCentering (Read-Ui)
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'shortcuts-wide.png')) | Out-Null

        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 720, 900, 0x16) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ShortcutsBackButton' -and $items.name -contains 'Ctrl+T' }
        $items = Read-Ui
        $viewport = $items | Where-Object automationId -eq 'ShortcutsScroll'
        $shortcut = $items | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Ctrl+Tab / Ctrl+Shift+Tab' }
        if (-not $shortcut -or $shortcut.x + $shortcut.width -gt $viewport.x + $viewport.width + 1) { throw 'Resizing clipped the shortcut labels.' }
        Assert-ShortcutCentering $items
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'shortcuts-narrow.png')) | Out-Null
        Browser
        $settings = @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' })[0]
        Ui @('invoke', $settings.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ShortcutsBackButton' }

        Press 'Back to settings'
        Wait-Ui { param($items) $items.automationId -contains 'KeyboardShortcutsButton' -and $items.automationId -notcontains 'ShortcutsBackButton' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'settings-help.png')) | Out-Null
        Ui @('invoke', 'KeyboardShortcutsButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ShortcutsBackButton' }
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' -and $items.automationId -notcontains 'ShortcutsBackButton' }
        if ([IO.File]::ReadAllText($settingsFile) -cne $before) { throw 'Reading shortcut help changed saved preferences.' }

        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq 1 }
        [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) | Out-Null
        Ui @('invoke', 'AdvancedSearchButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Find in registry' }
        Press 'Cancel'
        Wait-Ui { param($items) $items.name -notcontains 'Find in registry' }
    }

    Test-Ui 'Help and about expands the final Settings section and reuses its tab' {
        $before = [IO.File]::ReadAllText($settingsFile)
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'SettingsScroll' }
        Ui @('scroll', 'SettingsScroll', '--to', 'bottom') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'github.com/jiripolasek/RegistryEditor' } -All
        $items = Read-Ui -All
        foreach ($url in @('github.com/jiripolasek/RegistryEditor', 'jiripolasek.com')) {
            if ($items.name -notcontains $url) { throw "The default-expanded About section is missing '$url'." }
        }
        Open-Menu 'Help and about'
        Wait-Ui { param($items) $items.automationId -contains 'AboutExpander' -and $items.name -like 'Version *' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'settings-about.png')) | Out-Null
        Ui @('invoke', (Pick 'Button' 'Modern Registry Editor').selector, '--action', 'collapse') | Out-Null
        Open-Menu 'Keyboard shortcuts'
        Wait-Ui { param($items) $items.automationId -contains 'ShortcutsBackButton' }
        Open-Menu 'Help and about'
        Wait-Ui { param($items) $items.name -like 'Version *' -and $items.automationId -notcontains 'ShortcutsBackButton' }
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -ne 1) { throw 'Help and about opened duplicate Settings tabs.' }
        if ([IO.File]::ReadAllText($settingsFile) -cne $before) { throw 'Opening About changed saved preferences.' }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
    }

    Test-Ui 'Tree tracking can be paused synced manually and restored from Settings' {
        function Open-ListKey([string] $Name) {
            $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "$Name, Key,*" })
            if ($row.Count -ne 1) { throw "Expected one registry key row for $Name." }
            Ui @('invoke', $row[0].selector, '--action', 'select') | Out-Null
            Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row[0].selector) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object automationId -match '^Breadcrumb\d+$')[-1].name -eq $Name }
        }
        Assert-TreeSelection $address
        View-Option 'Automatically sync tree'
        if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).AutoSyncTree -ne $false) { throw 'Disabling automatic tree tracking did not persist.' }
        View-Option 'Show keys in list'
        Open-ListKey 'Child'
        Assert-TreeSelection $address
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tree-detached.png')) | Out-Null
        View-Option 'Sync tree to current key'
        Assert-TreeSelection ($address + '\Child')
        if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).AutoSyncTree) { throw 'Manual sync re-enabled automatic tracking.' }
        $crumbs = @(Read-Ui | Where-Object automationId -match '^Breadcrumb\d+$')
        if ($crumbs[-1].name -ne 'Child') { throw 'Syncing the tree changed the registry location.' }
        Press 'Up'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' }).Count -eq 1 }
        Assert-TreeSelection ($address + '\Child')
        Select-Row 'Counter' | Out-Null
        $detached = Pick 'TreeItem' ($address + '\Child')
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $detached.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'New name').value -ne 'Child') { throw 'Tree F2 targeted the list selection.' }
        Press 'Cancel'
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $detached.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        $confirmation = @(Read-Ui | Where-Object { $_.type -eq 'Text' } | ForEach-Object { ([string]$_.name -split '\r?\n')[0] })
        if ($confirmation -notcontains ($address + '\Child')) { throw 'Tree Delete targeted a different key.' }
        Press 'Cancel'
        Expect-HistoryLocation $address
        View-Option 'Navigation pane'
        if ((Read-Ui).automationId -contains 'RegistryTree') { throw 'The tree was not hidden.' }
        View-Option 'Sync tree to current key'
        Assert-TreeSelection $address

        $late = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey + '\LateChild')
        $late.Dispose()
        Press 'Refresh'
        Open-ListKey 'LateChild'
        Assert-TreeSelection $address
        View-Option 'Sync tree to current key'
        Assert-TreeSelection ($address + '\LateChild')
        Press 'Up'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'LateChild,*' }).Count -eq 1 }
        $lateRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'LateChild,*' })[0]
        Ui @('invoke', $lateRow.selector, '--action', 'select') | Out-Null
        Press 'Selection'; $menu = Read-ContextMenu
        Ui @('invoke', 'EntryDeleteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'LateChild,*' }).Count -eq 0 }
        Assert-TreeSelection $address

        $branch = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey + '\Virtualized')
        try {
            foreach ($index in 0..159) { $childKey = $branch.CreateSubKey(('Item{0:D3}' -f $index)); $childKey.Dispose() }
            $childKey = $branch.CreateSubKey('zzTarget'); $childKey.Dispose()
        } finally { $branch.Dispose() }
        Press 'Refresh'; Open-ListKey 'Virtualized'
        Press 'Filter'; Fill 'Filter this key' 'zzTarget'
        Open-ListKey 'zzTarget'
        View-Option 'Sync tree to current key'
        Assert-TreeSelection ($address + '\Virtualized\zzTarget')
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tree-offscreen-target.png')) | Out-Null
        Press 'Up'; Press 'Up'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Virtualized,*' }).Count -eq 1 }
        $branchRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Virtualized,*' })[0]
        Ui @('invoke', $branchRow.selector, '--action', 'select') | Out-Null
        Press 'Selection'; $menu = Read-ContextMenu
        Ui @('invoke', 'EntryDeleteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Virtualized,*' }).Count -eq 0 }
        Assert-TreeSelection $address
        Open-ListKey 'Child'
        Assert-TreeSelection $address
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'AutoSyncTreeSwitch' } -All
        $saved = [IO.File]::ReadAllText($settingsFile)
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            try { Ui @('invoke', 'AutoSyncTreeSwitch', '--action', 'toggle-on') | Out-Null }
            catch { if ($_ -notmatch 'reported ToggleState_Off') { throw } }
            Wait-Ui { param($items) $items.name -contains 'Settings could not be saved' } -All
            if ([IO.File]::ReadAllText($settingsFile) -cne $saved) { throw 'Failed tree-tracking save replaced settings.' }
        } finally { $locked.Dispose() }
        Ui @('invoke', 'AutoSyncTreeSwitch', '--action', 'toggle-on') | Out-Null
        Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).AutoSyncTree -eq $true }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tree-settings.png')) | Out-Null
        $browserTab = Pick 'TabItem' 'Child'
        Ui @('invoke', $browserTab.selector, '--action', 'select') | Out-Null
        Assert-TreeSelection ($address + '\Child')
        Press 'Up'
        Assert-TreeSelection $address
        View-Option 'Show keys in list'
        Ui @('invoke', (Pick 'TabItem' 'Settings').selector, '--action', 'select') | Out-Null
        Open-Menu 'Close tab'
        Assert-TreeSelection $address
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tree-tracking.png')) | Out-Null
    }

    foreach ($iconButton in 'Left', 'Right') {
        Test-Ui "A single $iconButton click on the application icon opens the native window menu" {
            $assets = Join-Path (Split-Path -Parent $Executable) 'Assets'
            foreach ($file in 'AppIcon.ico', 'TitleBarIcon.png') {
                if (-not [IO.File]::Exists((Join-Path $assets $file))) { throw "Missing title-bar asset $file." }
            }
            Browser
            Set-TestWindowForeground
            $caption = Get-TestActivationPoint
            [UxTestWindow]::LeftClickAt($caption.X, $caption.Y)
            $icon = Pick 'Image' 'Modern Registry Editor'
            $x, $y = [int]($icon.x + $icon.width / 2), [int]($icon.y + $icon.height / 2)
            $before = [UxTestWindow]::DescribeMouse($mainWindow, $x, $y)
            if ($iconButton -eq 'Right') { [UxTestWindow]::RightClickAt($x, $y) }
            else { [UxTestWindow]::LeftClickAt($x, $y) }
            $after = [UxTestWindow]::DescribeMouse($mainWindow, $x, $y)
            @($before, $after) | Set-Content (Join-Path $ArtifactDirectory 'icon-hit-test.txt')
            try {
                if (-not [Threading.SpinWait]::SpinUntil([Func[bool]] { [UxTestWindow]::IsSystemMenuOpen($mainWindow) }, 3000)) {
                    Invoke-WinAppChecked @('ui', 'list-windows', '--json') | Set-Content (Join-Path $ArtifactDirectory 'icon-menu-windows.json')
                    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'icon-menu.png')) | Out-Null
                    throw 'Clicking the visible application icon did not open the system menu for this HWND.'
                }
            } finally { Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null }
            if ([UxTestWindow]::IsSystemMenuOpen($mainWindow)) { throw 'Escape did not dismiss the window menu.' }
        }
    }

    Test-Ui 'Window chrome retains menu tab and layout behavior' {
        & (Join-Path $PSScriptRoot 'Layout.Ui.Tests.ps1') -AppPid $appProcess.Id -ArtifactDirectory (Join-Path $ArtifactDirectory 'layout')
        if ($LASTEXITCODE -ne 0) { throw 'Browser layout checks failed.' }
        & (Join-Path $PSScriptRoot 'Shell.Ui.Tests.ps1') -AppPid $appProcess.Id -SandboxRun $SandboxRun -ArtifactDirectory (Join-Path $ArtifactDirectory 'shell')
        if ($LASTEXITCODE -ne 0) { throw 'Window shell checks failed.' }
    }

    Test-Ui 'Overflowing tabs leave the caption buttons clear' {
        Go-ToKey $address
        $scale = [UxTestWindow]::GetDpiForWindow($mainWindow) / 96.0
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, [int](760 * $scale), [int](650 * $scale), 0x16)) {
            throw 'Could not narrow the test window.'
        }
        foreach ($tab in 1..7) {
            Ui @('send-keys', 'ctrl+t', '--via', 'send-input', '--target', 'ToolsButton') | Out-Null
        }
        Restart-Editor
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        if ($saved.WindowSessions[-1].Tabs.Count -lt 8) {
            throw 'The restarted window did not have enough tabs to overflow.'
        }
        $items = @(Read-Ui)
        $add = $items | Where-Object automationId -eq 'AddButton'
        $minimize = $items | Where-Object automationId -eq 'Minimize'
        if (-not $add -or -not $minimize -or $minimize.x - ($add.x + $add.width) -lt 48 * $scale - 2) {
            throw 'Overflowing tabs intrude into the title-bar drag area or the caption buttons.'
        }
        foreach ($tab in 1..7) {
            Ui @('send-keys', 'ctrl+w', '--via', 'send-input', '--target', 'ToolsButton') | Out-Null
        }
        [void][UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)
    }

    Test-Ui 'Browser status bar accepts mouse clicks' {
        function Click-StatusButton([string] $Id) {
            $button = @(Read-Ui | Where-Object automationId -eq $Id)
            if ($button.Count -ne 1 -or $button[0].width -le 0 -or $button[0].height -le 0) {
                throw "Missing visible status button '$Id'."
            }
            $x = [int]($button[0].x + $button[0].width / 2)
            $y = [int]($button[0].y + $button[0].height / 2)
            Ui @('click', '--at', "$x,$y") | Out-Null
        }

        Browser
        Set-TestWindowForeground
        Click-StatusButton 'RegistryViewStatus'
        $null = Read-ContextMenu '32-bit registry'
        Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null

        Click-StatusButton 'AccessStatus'
        $null = Read-ContextMenu 'Open as administrator' -Types @('Button')
        Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null

        Click-StatusButton 'ConnectionStatus'
        Wait-Ui { param($items) $items.name -contains 'Connect network registry' }
        Press 'Cancel'
    }

    Test-Ui 'List Alt arrow shortcuts navigate from a focused row' {
        function Expect-ListLocation([string] $Name) {
            Wait-Ui {
                param($items)
                $crumbs = @($items | Where-Object automationId -match '^Breadcrumb\d+$')
                $crumbs.Count -gt 0 -and $crumbs[-1].name -eq $Name
            }
        }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+up', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Expect-ListLocation 'Software'
        $list = Pick 'List' 'Registry keys and values'
        $row = @((Elements $list.children) | Where-Object type -eq 'ListItem')[0]
        Ui @('send-keys', 'alt+left', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Expect-ListLocation $fixtureName
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'alt+right', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Expect-ListLocation 'Software'
        Press 'Back'; Expect-ListLocation $fixtureName
        $tree = Pick 'TreeItem' $address
        Ui @('send-keys', 'alt+up', '--via', 'send-input', '--target', $tree.selector) | Out-Null
        Expect-ListLocation 'Software'
        Press 'Back'; Expect-ListLocation $fixtureName
    }

    Test-Ui 'Browser list keeps keyboard focus across row replacements' {
        Browser
        Go-ToKey $address
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$subKey\Child", $true)
        try { $key.SetValue('FocusFixture', 'disposable') } finally { $key.Dispose() }
        $inspectorWasOpen = (Read-Ui).automationId -contains 'CloseValueInspector'
        try {
            foreach ($inspectorOpen in $true, $false) {
                if (((Read-Ui).automationId -contains 'CloseValueInspector') -ne $inspectorOpen) {
                    Ui @('invoke', 'ToggleValueInspector') | Out-Null
                }

                $row = Select-Row 'Child,'
                if ($inspectorOpen) {
                    Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row.selector) | Out-Null
                } else {
                    Ui @('click', $row.selector, '--double') | Out-Null
                }
                Wait-Ui { param($items) @($items | Where-Object type -eq 'ListItem').name -like 'FocusFixture,*' }
                Assert-FocusedPane 'Registry keys and values'
                $rows = @(Elements (Pick 'List' 'Registry keys and values').children | Where-Object type -eq 'ListItem' | Sort-Object y)
                $selected = Ui @('get-property', $rows[0].selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
                if ($selected.properties.IsSelected -ne 'True') { throw 'Navigation did not select the first row in Child.' }
                Ui @('send-keys', 'down', '--via', 'send-input') | Out-Null
                $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
                if ($rows.Count -lt 2 -or $focused.name -ne $rows[1].name) { throw "Down reached '$($focused.name)' after entering Child." }

                foreach ($shortcut in 'alt+left', 'alt+right', 'alt+up') {
                    Ui @('send-keys', $shortcut, '--via', 'send-input') | Out-Null
                    $destination = if ($shortcut -eq 'alt+right') { 'Child' } else { $fixtureName }
                    Wait-Ui { param($items) @($items | Where-Object automationId -match '^Breadcrumb\d+$')[-1].name -eq $destination }
                    Assert-FocusedPane 'Registry keys and values'
                    if ($shortcut -ne 'alt+up') {
                        $expectedName = if ($shortcut -eq 'alt+left') { $row.name } else { $rows[1].name }
                        $restored = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
                        $selected = Ui @('get-property', $restored.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
                        if ($restored.name -ne $expectedName -or $selected.properties.IsSelected -ne 'True') {
                            throw "$shortcut did not restore selection and focus to '$expectedName'."
                        }
                    }
                }
            }

            Go-ToKey "$address\Child"
            Press 'Filter'; Fill 'Filter this key' 'FocusFixture'
            Wait-Ui {
                param($items)
                $rows = @($items | Where-Object type -eq 'ListItem')
                $rows.Count -eq 1 -and $rows[0].name -like 'FocusFixture,*'
            }
            $row = Select-Row 'FocusFixture'
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$subKey\Child", $true)
            try { $key.DeleteValue('FocusFixture') } finally { $key.Dispose() }
            Ui @('send-keys', 'f5', '--via', 'send-input', '--target', $row.selector) | Out-Null
            Wait-Ui { param($items) $items.name -contains 'No matching items' }
            Assert-FocusedPane 'Registry keys and values'
            $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
            if ($focused.automationId -ne 'EntriesList') { throw "The empty registry list left focus on '$($focused.name)'." }
        } finally {
            if (((Read-Ui).automationId -contains 'CloseValueInspector') -ne $inspectorWasOpen) {
                Ui @('invoke', 'ToggleValueInspector') | Out-Null
            }
            Go-ToKey $address
        }
    }

    Test-Ui 'Numeric editor binds the displayed base and validated bytes' {
        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('NumericFixture', 1, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
        Press 'Refresh'
        Wait-Ui { param($items) $items.name -contains 'NumericFixture, REG_DWORD, 0x00000001 (1)' }
        $row = Select-Row 'NumericFixture'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $focused.hasFocus -or $focused.element.automationId -ne 'ValueDataEditor') { throw 'Editing a value did not focus its data.' }
        $editor = Pick 'Window' 'Edit value NumericFixture'
        $editorItems = @(Elements $editor.children)
        if (@($editorItems | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Value name' }).Count) {
            throw 'The existing value name still looks editable.'
        }
        $data = Pick 'Edit' 'Value data'
        $surface = @($editorItems | Where-Object automationId -eq 'ContentScrollViewer')[0]
        $heading = @($editorItems | Where-Object automationId -eq 'DialogHeading')[0]
        $cancel = @($editorItems | Where-Object automationId -eq 'CloseButton')[0]
        $padding = 24 * [UxTestWindow]::GetDpiForWindow([IntPtr]$mainWindow) / 96.0
        if ([Math]::Abs($heading.x - $surface.x - $padding) -gt 2 -or
            [Math]::Abs($data.x - $heading.x) -gt 2 -or
            [Math]::Abs($surface.x + $surface.width - $cancel.x - $cancel.width - $padding) -gt 2 -or
            [Math]::Abs($surface.y + $surface.height - $cancel.y - $cancel.height - $padding) -gt 2) {
            throw 'The dialog heading, data field and footer do not use WinUI dialog padding.'
        }
        $hex = Pick 'RadioButton' 'Hexadecimal'
        $decimal = Pick 'RadioButton' 'Decimal'
        if ($hex.x -le $data.x + $data.width -or [Math]::Abs($hex.y - $data.y) -gt $data.height) { throw 'Numeric data and base are not grouped beside each other.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'numeric-editor.png')) | Out-Null
        Ui @('send-keys', 'FF', '--verbatim', '--via', 'send-input') | Out-Null
        if ((Pick 'Edit' 'Value data').value -cne 'FF') { throw 'Immediate typing did not replace the selected value data.' }
        Ui @('send-keys', 'down', '--via', 'send-input', '--target', $hex.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Value data' -and $_.value -eq '255' }).Count -eq 1 }
        Fill 'Value data' 'invalid'
        Ui @('invoke', $hex.selector, '--action', 'select') | Out-Null
        $decimalOption = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow).FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'DecimalBaseOption'))
        $selected = $decimalOption.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        if ((Pick 'Edit' 'Value data').value -cne 'invalid' -or -not $selected) { throw 'An invalid base switch changed the data or selected base.' }
        Primary
        Wait-Ui {
            param($items)
            $items.automationId -contains 'PrimaryButton' -and
                @($items | Where-Object {
                    $_.automationId -eq 'DialogError' -and $_.name -eq 'Enter a whole decimal number from 0 to 4294967295.'
                }).Count -eq 1
        }
        Fill 'Value data' '16'
        Ui @('invoke', $hex.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Value data' -and $_.value -eq '10' }).Count -eq 1 }
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'NumericFixture, REG_DWORD, 0x00000010 (16)' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            if ($key.GetValue('NumericFixture') -ne 16) { throw 'The numeric editor wrote a value in the wrong base.' }
            $key.DeleteValue('NumericFixture')
        } finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Hex editor supports keyboard editing and exposes the active byte to automation' {
        Browser
        $row = Select-Row 'Bytes, REG_BINARY'
        Ui @('click', $row.selector, '--double') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueHexPreview' }
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if ($focused.element.automationId -ne 'ValueHexPreview') { throw 'Opening a binary value did not focus its editor.' }
        $line = @(Read-Ui | Where-Object { $_.name -like '00000000 *' })[0]
        if (-not $line) { throw 'The first hex row was missing from the automation tree.' }
        $characterWidth = $line.width / $line.name.TrimEnd().Length
        [UxTestWindow]::LeftClickAt([int]($line.x + 27 * $characterWidth), [int]($line.y + $line.height / 2))
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueHexPreview' -and $_.value -eq '05' }).Count -eq 1 }
        function Read-SelectionColors([string] $column) {
            $path = Join-Path $ArtifactDirectory "hex-selection-$column.png"
            Ui @('screenshot', $line.selector, '--output', $path) | Out-Null
            $bitmap = [Drawing.Bitmap]::new($path)
            try {
                $cell = $bitmap.Width / $line.name.TrimEnd().Length
                # Compare the interiors and top edges at byte 06 with unselected ASCII byte 08.
                foreach ($character in 29, 67, 69) {
                    $bitmap.GetPixel([int](($character + 0.5) * $cell), 2).ToArgb()
                }
                foreach ($character in 29, 67) {
                    $bitmap.GetPixel([int](($character + 0.5) * $cell), 0).ToArgb()
                }
            } finally { $bitmap.Dispose() }
        }
        Add-Type -AssemblyName System.Drawing
        Ui @('send-keys', 'shift+right shift+right', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Hex, offset 7, byte 07, 3 bytes selected' }
        $active, $mirror, $plain, $hexEdge, $asciiEdge = Read-SelectionColors 'hex'
        function Read-HoverStroke([string] $name) {
            $path = Join-Path $ArtifactDirectory "hex-hover-$name.png"
            Ui @('screenshot', $line.selector, '--output', $path) | Out-Null
            $bitmap = [Drawing.Bitmap]::new($path)
            try {
                $cell = $bitmap.Width / $line.name.TrimEnd().Length
                foreach ($character in 29, 67) {
                    # Sample inside the hover underline, above the mirrored selection's bottom border.
                    $bitmap.GetPixel([int](($character + 0.5) * $cell), $bitmap.Height - 3).ToArgb()
                }
            } finally { $bitmap.Dispose() }
        }
        $before = Read-HoverStroke 'before'
        $rowY = [int]($line.y + $line.height / 2)
        foreach ($character in 29, 67) {
            $point = '{0},{1}' -f [int]($line.x + ($character + 0.5) * $characterWidth), $rowY
            Ui @('hover', '--at', $point) | Out-Null
            $null = Read-ContextMenu 'Offset 0x00000006 (6)' -Types @('ToolTip','Text')
            $over = Read-HoverStroke $character
            if ($over[0] -eq $before[0] -or $over[1] -eq $before[1]) { throw 'Hovering either representation should mark both selected byte cells.' }
        }
        [UxTestWindow]::RightClickAt([int]($line.x + 70.5 * $characterWidth), $rowY)
        $menu = Read-ContextMenu 'Copy offset'
        $copyOffset = @($menu.Items | Where-Object name -eq 'Copy offset')[0]
        Ui @('invoke', $copyOffset.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Assert-Clipboard '0x00000009' 'hex-pointer-offset'
        Ui @('send-keys', 'ctrl+shift+c', '--via', 'send-input', '--target', 'ValueHexPreview') | Out-Null
        Assert-Clipboard '0x00000007' 'hex-keyboard-offset'
        Ui @('send-keys', 'ctrl+g', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'HexGoToOffset' }
        $closeOffset = Pick 'Button' 'Close Go to offset'
        [UxTestWindow]::LeftClickAt([int]($closeOffset.x + $closeOffset.width / 2), [int]($closeOffset.y + $closeOffset.height / 2))
        Wait-Ui { param($items) $items.automationId -notcontains 'HexGoToOffset' -and $items.name -contains 'Hex, offset 7, byte 07, 3 bytes selected' }
        Ui @('send-keys', 'f6', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'ASCII, offset 7, byte 07, 3 bytes selected' }
        $hexFill, $asciiFill, $plainAfter, $hexEdgeAfter, $asciiEdgeAfter = Read-SelectionColors 'ascii'
        if ($active -eq $plain -or $mirror -ne $plain -or $asciiEdge -eq $plain -or $plainAfter -ne $plain -or
            $hexFill -ne $plain -or $asciiFill -ne $active -or $hexEdgeAfter -ne $asciiEdge -or $asciiEdgeAfter -ne $hexEdge) {
            throw 'F6 should swap the filled input column and the outline around the matching bytes.'
        }
        Ui @('send-keys', 'f6 ctrl+home', '--via', 'send-input') | Out-Null
        foreach ($keyName in 'a', '5', 'ctrl+end', 'f6', 'z', 'y') {
            Ui @('send-keys', $keyName, '--via', 'send-input') | Out-Null
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'hex-editor.png')) | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ValueDataEditor' -and $items.name -contains 'Bytes: 18' -and
            ($items.name -like '00000000   A5 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F *') -and ($items.name -like '00000010   7A 79 *') }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }

        if ((Read-Ui).automationId -contains 'ToolDetailsResize') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        $row = Select-Row 'Bytes, REG_BINARY'
        Activate-Editor
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OpenRawBytes' }
        Ui @('invoke', 'OpenRawBytes', '--action', 'invoke') | Out-Null
        $rawWindow = Wait-ByteToolWindow 'ExpandedValueHexView'
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$rawWindow)
        $view = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ExpandedValueHexView'))
        $byte = $view.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if (-not $byte.Current.IsReadOnly -or $byte.Current.Value -ne '00') { throw 'The inspector did not expose the original read-only byte.' }
        Ui @('invoke', 'CloseButton', '--action', 'invoke') -Window $rawWindow | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and $items.automationId -contains 'OpenRawBytes' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
    }

    Test-Ui 'Hex editor edits the active byte and confirms registry clipboard conversions' {
        Browser
        $row = Select-Row 'Bytes, REG_BINARY'
        $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $rowCondition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $row.name))
        Set-TestWindowForeground
        $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $rowCondition).SetFocus()
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueHexPreview' }
        $editor = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ValueHexPreview'))
        function Editor-Keys([string] $Keys) {
            Set-TestWindowForeground
            $editor.SetFocus()
            Ui @('send-keys', $Keys, '--via', 'send-input') | Out-Null
        }
        Editor-Keys 'ctrl+home shift+right shift+right'
        Wait-Ui { param($items) $items.name -contains 'Hex, offset 2, byte 02, 3 bytes selected' }
        if (-not [UxTestAccessibility]::SetByteAndObserveValue($editor, 'A5')) { throw 'The byte edit did not raise a ValuePattern change.' }
        Wait-Ui { param($items) $items.name -like '00000000   00 01 A5 03 *' }
        Editor-Keys 'ctrl+home shift+right shift+right b 6'
        Wait-Ui { param($items) $items.name -like '00000000   00 01 B6 03 *' }
        Editor-Keys 'ctrl+z'
        Wait-Ui { param($items) $items.name -like '00000000   00 01 A5 03 *' }
        Editor-Keys 'ctrl+y ctrl+a delete'
        Wait-Ui { param($items) $items.name -contains 'Bytes: 0' }
        Editor-Keys 'ctrl+z'
        Wait-Ui { param($items) $items.name -contains 'Bytes: 16' -and $items.name -like '00000000   00 01 B6 03 *' }
        Editor-Keys 'ctrl+g'
        Wait-Ui { param($items) $items.automationId -contains 'HexGoToOffset' }
        $line = @(Read-Ui | Where-Object name -like '00000000 *')[0]
        $cell = $line.width / $line.name.TrimEnd().Length
        [UxTestWindow]::LeftClickAt([int]($line.x + 20.5 * $cell), [int]($line.y + $line.height / 2))
        Wait-Ui { param($items) $items.automationId -notcontains 'HexGoToOffset' -and $items.name -contains 'Hex, offset 3, byte 03, high digit' }
        Ui @('send-keys', 'right f f', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -like '00000000   00 01 B6 03 FF *' }

        Editor-Keys 'ctrl+home right'
        Set-Clipboard -Value ('hex:de,\' + [Environment]::NewLine + '  ad')
        Editor-Keys 'ctrl+v'
        $offer = Read-ContextMenu 'Apply conversion' -Types @('Button')
        Ui @('send-keys', 'esc', '--via', 'send-input') -Window $offer.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Bytes: 16' -and $items.name -like '00000000   00 01 B6 03 FF *' }
        Editor-Keys 'ctrl+v'
        $offer = Read-ContextMenu 'Apply conversion' -Types @('Button')
        Ui @('invoke', 'ApplyHexPasteConversion', '--action', 'invoke') -Window $offer.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Bytes: 18' -and $items.name -like '00000000   00 DE AD 01 B6 03 FF *' }

        Set-Clipboard -Value '01,A4,FF'
        Editor-Keys 'ctrl+shift+v'
        $offer = Read-ContextMenu 'Apply conversion' -Types @('Button')
        Ui @('invoke', 'ApplyHexPasteConversion', '--action', 'invoke') -Window $offer.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Bytes: 18' -and $items.name -like '00000000   00 DE AD 01 A4 FF FF *' }
        Set-Clipboard -Value 'hex(2):55,66'
        Editor-Keys 'ctrl+v'
        Wait-Ui { param($items) $items.name -contains 'Paste complete hexadecimal byte pairs.' -and $items.name -like '00000000   00 DE AD 01 A4 FF FF *' }
        Press 'Cancel'
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
    }

    Test-Ui 'Bound New Copy and Paste commands retain value type across tabs' {
        Browser
        Press 'New'
        $menu = Read-ContextMenu
        $binary = Pick 'MenuItem' 'Binary value' -Window $menu.Window
        Ui @('invoke', $binary.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ValueHexPreview' -and $items.automationId -notcontains 'ValueDataEditor' }
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $focused.hasFocus -or $focused.element.type -ne 'Edit' -or $focused.element.name -ne 'Value name') { throw 'Creating a value did not focus its name.' }
        Ui @('set-value', $focused.element.selector, 'TypedBytes') | Out-Null
        Ui @('send-keys', 'g', '--target', 'ValueHexPreview', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Bytes: 0' }
        Ui @('send-keys', '0 0 f f 4 2', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Bytes: 3' -and ($items.name -like '00000000   00 FF 42 *') }
        Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'TypedBytes, REG_BINARY,*' }).Count -eq 1 }
        $row = Select-Row 'TypedBytes'
        $menu = Context-Menu $row.selector
        Ui @('invoke', 'EntryCopyMenu', '--action', 'invoke') -Window $menu.Window | Out-Null

        View-Option 'Show keys in list'
        $child = Select-Row 'Child, Key,'
        $tab = Middle-Open $child 'Child'
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object automationId -match '^Breadcrumb\d+$')[-1].name -eq 'Child' }
        Press 'Paste'
        Wait-Ui { param($items) $items.name -contains 'Name of the copy' }
        if ((Pick 'Edit' 'Name of the copy').value -ne 'TypedBytes') { throw 'Paste lost the copied value across tabs.' }
        Fill 'Name of the copy' 'PastedBytes'
        Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'PastedBytes, REG_BINARY,*' }).Count -eq 1 }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey + '\Child', $true)
        try {
            if ($key.GetValueKind('PastedBytes') -ne 'Binary' -or [Convert]::ToHexString($key.GetValue('PastedBytes')) -ne '00FF42') {
                throw 'Paste changed the registry type or bytes.'
            }
            $key.DeleteValue('PastedBytes')
        } finally { $key.Dispose() }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteValue('TypedBytes') } finally { $key.Dispose() }
        Close-MiddleTab $tab
        Press 'Refresh'
        View-Option 'Show keys in list'
    }

    Test-Ui 'Enter submits ordinary registry forms and preserves multiline and delete behavior' {
        Browser
        function Choose-NewValue([string] $kind) {
            Press 'New'
            $menu = Read-ContextMenu $kind
            $item = Pick 'MenuItem' $kind -Window $menu.Window
            Ui @('invoke', $item.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        }
        Choose-NewValue 'String value'
        Fill 'Value name' 'EnterFixture'
        $input = Pick 'Edit' 'Value name'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'EnterFixture,*' }).Count -eq 1 }
        $row = Select-Row 'EnterFixture'
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if ($key.GetValueNames() -notcontains 'EnterFixture') { throw 'Enter confirmed Delete.' } } finally { $key.Dispose() }
        Choose-NewValue 'String value'
        Fill 'Value name' 'OptInStringFixture'
        $singleLineHeight = (Pick 'Edit' 'Value data').height
        $state = Ui @('get-property', 'AllowValueLineBreaks', '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?Off$|^0$') { throw 'String editing did not start in single-line mode.' }
        Fill 'Value data' 'original'
        $data = Pick 'Edit' 'Value data'
        Set-Clipboard -Value 'replacement'
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ValueDataEditor' -and $_.value -ceq 'replacement' }).Count -eq 1 }
        Set-Clipboard -Value "pasted one`r`npasted two"
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Select Allow line breaks to enter multiline text.' }
        if ((Pick 'Edit' 'Value data').value -cne 'replacement') { throw 'Rejected multiline paste changed the draft.' }
        Ui @('invoke', 'AllowValueLineBreaks', '--action', 'toggle-on') | Out-Null
        if ((Pick 'Edit' 'Value data').height -le $singleLineHeight) { throw 'Allow line breaks did not expand the editor.' }
        $data = Pick 'Edit' 'Value data'
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
        if ((Pick 'Edit' 'Value data').value -notmatch 'pasted one(?:\r\n|\r|\n)pasted two') { throw 'Opted-in multiline paste lost text.' }
        Fill 'Value data' ''
        $data = Pick 'Edit' 'Value data'
        Ui @('send-keys', 'line one', '--verbatim', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'line two', '--verbatim', '--via', 'send-input') | Out-Null
        $multilineText = [string](Pick 'Edit' 'Value data').value
        if ($multilineText -notmatch 'line one(?:\r\n|\r|\n)line two') { throw 'The string editor did not accept Enter after opting in.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'string-multiline.png')) | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try {
            if ($key.GetValueKind('OptInStringFixture') -ne 'String' -or $key.GetValue('OptInStringFixture') -cne $multilineText) {
                throw 'The multiline string was not saved with its exact text and REG_SZ type.'
            }
        } finally { $key.Dispose() }
        $row = Select-Row 'OptInStringFixture'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'AllowValueLineBreaks' }
        $state = Ui @('get-property', 'AllowValueLineBreaks', '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?On$|^1$' -or
            [string](Pick 'Edit' 'Value data').value -cne $multilineText) { throw 'Existing multiline data did not open visibly in multiline mode.' }
        $enabled = Ui @('get-property', 'AllowValueLineBreaks', '--property', 'IsEnabled', '--json') | ConvertFrom-Json
        if ([string]$enabled.properties.IsEnabled -ine 'False') { throw 'Multiline mode can be disabled while line breaks remain.' }
        Fill 'Value data' 'single line replacement'
        Ui @('invoke', 'AllowValueLineBreaks', '--action', 'toggle-off') | Out-Null
        $data = Pick 'Edit' 'Value data'
        if ($data.value -cne 'single line replacement' -or $data.height -gt $singleLineHeight) { throw 'The editor did not safely return to single-line mode.' }
        Ui @('invoke', (Pick 'Button' 'Cancel').selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if ($key.GetValue('OptInStringFixture') -cne $multilineText) { throw 'Cancel changed the stored multiline string.' } } finally { $key.Dispose() }
        Choose-NewValue 'String value'
        $state = Ui @('get-property', 'AllowValueLineBreaks', '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?Off$|^0$') { throw 'Multiline opt-in leaked to another editor instance.' }
        Fill 'Value name' 'EnterDataFixture'
        Fill 'Value data' 'single line'
        $data = Pick 'Edit' 'Value data'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'EnterDataFixture,*' }).Count -eq 1 }
        Choose-NewValue 'Multi-string value'
        if ((Read-Ui).automationId -contains 'AllowValueLineBreaks') { throw 'REG_MULTI_SZ unexpectedly requires a multiline opt-in.' }
        Fill 'Value name' 'MultilineFixture'
        $data = Pick 'Edit' 'Value data'
        Ui @('send-keys', 'line one', '--verbatim', '--via', 'send-input', '--target', $data.selector) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'line two', '--verbatim', '--via', 'send-input') | Out-Null
        if ((Pick 'Edit' 'Value data').value -notmatch 'line one(?:\r\n|\r|\n)line two') { throw 'Enter did not insert a newline in the multi-string editor.' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if (($key.GetValue('MultilineFixture') -join '|') -cne 'line one|line two') { throw 'Multiline input did not retain both strings.' } } finally { $key.Dispose() }
    }

    Test-Ui 'Opening expanded HKCR in a new tab stays responsive' {
        $root = 'HKEY_CLASSES_ROOT'
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey('ClassesRoot', 'Registry64')
        try { $names = $hive.GetSubKeyNames() } finally { $hive.Dispose() }
        [Array]::Sort($names, [StringComparer]::OrdinalIgnoreCase)
        $first = "$root\$($names[0])"
        $metrics = [ordered]@{ keys = $names.Length }
        function Measure-TreeAction([string] $Name, [scriptblock] $Action) {
            $stop = [Threading.CancellationTokenSource]::new()
            $probe = [UxTestWindow]::LongestWindowDelay([IntPtr]$mainWindow, $stop.Token)
            try { & $Action }
            finally {
                $stop.Cancel()
                $metrics[$Name] = $probe.GetAwaiter().GetResult()
                $stop.Dispose()
                $metrics | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'hkcr-responsiveness.json')
            }
            if ($metrics[$Name] -ge 1000) { throw "$Name stalled the UI thread for $($metrics[$Name]) ms." }
        }
        Go-ToKey $root
        Assert-TreeSelection $root
        Measure-TreeAction 'expandMs' {
            Ui @('invoke', (Pick 'TreeItem' $root).selector, '--action', 'expand') | Out-Null
            Wait-Ui { param($items) $items.name -contains $first }
        }
        $position = (Pick 'TreeItem' $first).y
        Measure-TreeAction 'openTabMs' {
            $menu = Context-Menu (Pick 'TreeItem' $root).selector
            $open = Pick 'MenuItem' 'Open in new tab' -Window $menu.Window
            Ui @('invoke', $open.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $root }).Count -eq 2 -and $items.name -contains $first }
            Assert-TreeSelection $root
        }
        Measure-TreeAction 'returnMs' {
            Open-Menu 'Close tab'
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq $root }).Count -eq 1 -and $items.name -contains $first }
            Assert-TreeSelection $root
        }
        Measure-TreeAction 'registryUsageMs' {
            $menu = Context-Menu (Pick 'TreeItem' $root).selector
            $usage = Pick 'MenuItem' 'Registry Usage' -Window $menu.Window
            Ui @('invoke', $usage.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'RegistryUsageSubkeys' }
        }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.name -contains $first }
        Assert-TreeSelection $root
        if ([Math]::Abs((Pick 'TreeItem' $first).y - $position) -gt 2) { throw 'Opening another tab moved the source tree.' }
        Ui @('invoke', (Pick 'TreeItem' $root).selector, '--action', 'collapse') | Out-Null
        $wide = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\WideTree")
        try {
            foreach ($n in 1..10000) { $wide.CreateSubKey(('Key{0:D5}' -f $n)).Dispose() }
        } finally { $wide.Dispose() }
        $path = "$address\WideTree"
        Go-ToKey $path
        Assert-TreeSelection $path
        Measure-TreeAction 'wideExpandMs' {
            Ui @('invoke', (Pick 'TreeItem' $path).selector, '--action', 'expand') | Out-Null
            Wait-Ui { param($items) $items.name -contains "$path\Key00001" }
        }
        Measure-TreeAction 'wideOpenTabMs' {
            Ui @('send-keys', 'ctrl+t', '--via', 'send-input', '--target', (Pick 'TreeItem' $path).selector) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'WideTree' }).Count -eq 2 -and $items.name -contains "$path\Key00001" }
            Assert-TreeSelection $path
        }
        $rows = @(Read-Ui -All | Where-Object { $_.type -eq 'TreeItem' -and $_.name.StartsWith("$path\Key") -and $_.children })
        $metrics['wideRowPeersWithContent'] = $rows.Count
        if ($rows.Count -eq 0 -or $rows.Count -eq 10000) { throw "Expected a virtualized tree, found $($rows.Count) rows with content." }
        Measure-TreeAction 'wideLastKeyMs' {
            Go-ToKey "$path\Key10000"
            Assert-TreeSelection "$path\Key10000"
        }
        Open-Menu 'Close tab'
        Assert-TreeSelection $path
        Ui @('invoke', (Pick 'TreeItem' $path).selector, '--action', 'collapse') | Out-Null
        Go-ToKey $address
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteSubKeyTree('WideTree') } finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Registry Usage row context menu opens the target subkey' {
        Browser
        $scope = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\UsageMenu")
        try {
            foreach ($fixture in @(@{ Name = 'Usage Alpha'; Bytes = 2 }, @{ Name = 'Usage Beta'; Bytes = 5 })) {
                $key = $scope.CreateSubKey($fixture.Name)
                try { $key.SetValue('Data', [byte[]]::new($fixture.Bytes), [Microsoft.Win32.RegistryValueKind]::Binary) }
                finally { $key.Dispose() }
            }
        } finally { $scope.Dispose() }

        Go-ToKey "$address\UsageMenu"
        Open-Menu 'Registry Usage' 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'RegistryUsageSummary' -and $items.name -contains '3 keys · 2 values · 7 bytes of value data' }

        Select-Row 'Usage Alpha' | Out-Null
        $row = Pick 'ListItem' 'Usage Beta, 5 bytes, 1 key, 1 value'
        $menu = Context-Menu $row.selector
        Ui @('invoke', (Pick 'MenuItem' 'Open subkey' -Window $menu.Window).selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains '1 key · 1 value · 5 bytes of value data' }

        Ui @('invoke', 'RegistryUsageUp') | Out-Null
        Wait-Ui { param($items) $items.name -contains '3 keys · 2 values · 7 bytes of value data' }
        $row = Pick 'ListItem' 'Usage Alpha, 2 bytes, 1 key, 1 value'
        $menu = Context-Menu $row.selector -Keyboard
        Ui @('invoke', (Pick 'MenuItem' 'Open in browser' -Window $menu.Window).selector) -Window $menu.Window | Out-Null
        Assert-TreeSelection "$address\UsageMenu\Usage Alpha"

        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.automationId -contains 'RegistryUsageSubkeys' }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
        Go-ToKey $address
    }

    Test-Ui 'Baseline sibling edits preserve tree anchor viewport' {
        function Save-Name([string] $Field, [string] $Name) {
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
            Fill $Field $Name
            Primary
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        }
        $branch = "$address\AnchorBaseline"
        $path = "$subKey\AnchorBaseline"
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
        try {
            foreach ($n in 1..48) { $key.CreateSubKey(('A-Padding\Row{0:D2}' -f $n)).Dispose() }
            $key.CreateSubKey('M-Keep\Nested').Dispose()
            $key.CreateSubKey('Z-Last').Dispose()
        } finally { $key.Dispose() }
        $originalPreferences = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $originalAutoSync = if ($null -eq $originalPreferences.AutoSyncTree) { $true } else { [bool]$originalPreferences.AutoSyncTree }
        $originalShowKeys = [bool]$originalPreferences.ShowKeysInList
        try {
            Browser
            if (-not $originalAutoSync) { View-Option 'Automatically sync tree' }
            if (-not $originalShowKeys) { View-Option 'Show keys in list' }
            Go-ToKey "$branch\A-Padding\Row48"
            Assert-TreeSelection "$branch\A-Padding\Row48"
            Go-ToKey "$branch\M-Keep\Nested"
            Assert-TreeSelection "$branch\M-Keep\Nested"
            View-Option 'Automatically sync tree'
            Go-ToKey $branch
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -match '^Breadcrumb\d+$' })[-1].name -eq 'AnchorBaseline' }
            function Get-AnchorEvidence([string] $Name) {
                $items = Read-Ui -All
                $pane = @($items | Where-Object automationId -eq 'RegistryTree')[0]
                if (-not $pane) { throw 'RegistryTree viewport was not exposed.' }
                $visible = @($items | Where-Object {
                    $_.type -eq 'TreeItem' -and $_.name -like "$branch\*" -and
                    $_.x -lt ($pane.x + $pane.width) -and ($_.x + $_.width) -gt $pane.x -and
                    $_.y -lt ($pane.y + $pane.height) -and ($_.y + $_.height) -gt $pane.y
                } | Sort-Object y)
                if (-not $visible.Count) { throw 'No branch rows intersect the RegistryTree viewport.' }
                $anchor = $visible[0]
                $next = if ($visible.Count -gt 1) { $visible[1] } else { throw 'A second visible tree row is required to measure the anchor offset.' }
                $clipTop = [Math]::Max([double]$anchor.y, [double]$pane.y)
                $clipBottom = [Math]::Min([double]($anchor.y + $anchor.height), [double]($pane.y + $pane.height))
                $selected = @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq "$branch\M-Keep\Nested" })[0]
                $selectedProperty = if ($selected) { Ui @('get-property', $selected.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json }
                $keep = @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq "$branch\M-Keep" })[0]
                $evidence = [ordered]@{ name=$Name; viewport=[ordered]@{x=$pane.x;y=$pane.y;width=$pane.width;height=$pane.height}; anchorName=$anchor.name; anchorBounds=[ordered]@{x=$anchor.x;y=$anchor.y;width=$anchor.width;height=$anchor.height}; clippedTop=$clipTop; clippedBottom=$clipBottom; verticalOffset=$clipTop-$pane.y; partiallyClipped=($anchor.height -lt 28); nextVisibleName=$next.name; nextVisibleY=$next.y; nextVisibleHeight=$next.height; nextVisibleOffset=$next.y-$pane.y; retainedSelection=($selectedProperty.properties.IsSelected -eq 'True'); expandedKeep=([bool]$selected); nestedVisible=([bool]$selected) }
                $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory ("anchor-$Name.json")) -Encoding utf8
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory ("anchor-$Name.png"))) | Out-Null
                $evidence
            }
            $before = Get-AnchorEvidence 'before'
            if (-not $before.retainedSelection -or -not $before.expandedKeep) { throw 'The middle key was not selected with its child branch expanded before editing.' }
            Press 'New'
            $menu = Read-ContextMenu 'Key'
            Ui @('invoke', (Pick 'MenuItem' 'Key' -Window $menu.Window).selector, '--action', 'invoke') -Window $menu.Window | Out-Null
            Save-Name 'Key name' '000-Inserted'
            $insertedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$path\000-Inserted")
            if (-not $insertedKey) { throw 'The editor did not create the inserted sibling.' }
            $insertedKey.Dispose()
            Press 'Up'
            Expect-HistoryLocation $branch
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '000-Inserted, Key,*' }).Count -eq 1 }
            $afterAdd = Get-AnchorEvidence 'after-add'
            $added = Select-Row '000-Inserted, Key,'
            Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $added.selector) | Out-Null
            Save-Name 'New name' '001-Renamed'
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '001-Renamed, Key,*' }).Count -eq 1 }
            $afterRename = Get-AnchorEvidence 'after-rename'
            $renamed = Select-Row '001-Renamed, Key,'
            Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $renamed.selector) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
            Primary
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and $items.name -notlike '*001-Renamed, Key,*' } -All
            $afterDelete = Get-AnchorEvidence 'after-delete'
            foreach ($state in @($afterAdd,$afterRename,$afterDelete)) {
                if ($state.anchorName -cne $before.anchorName) { throw "Anchor identity changed after $($state.name): '$($state.anchorName)' vs '$($before.anchorName)'" }
                if ($state.nextVisibleName -cne $before.nextVisibleName -or [Math]::Abs([double]$state.nextVisibleOffset - [double]$before.nextVisibleOffset) -gt 2) { throw "Upper-row offset changed after $($state.name): $($state.nextVisibleName) at $($state.nextVisibleOffset) vs $($before.nextVisibleName) at $($before.nextVisibleOffset)" }
                if (-not $state.retainedSelection -or -not $state.expandedKeep -or -not $state.nestedVisible) { throw "Selection/expansion did not survive $($state.name)." }
            }
        } finally {
            if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).AutoSyncTree -ne $originalAutoSync) { View-Option 'Automatically sync tree' }
            if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).ShowKeysInList -ne $originalShowKeys) { View-Option 'Show keys in list' }
            Go-ToKey $address
            $rootKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            if ($rootKey) { try { $rootKey.DeleteSubKeyTree('AnchorBaseline', $false) } finally { $rootKey.Dispose() } }
        }
    }
    Test-Ui 'Key edits preserve expanded branches selection and tree scroll' {
        function Save-Name([string] $Field, [string] $Name) {
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
            Fill $Field $Name
            Primary
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        }
        $branch = "$address\BranchEdits"
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\BranchEdits")
        try {
            foreach ($n in 1..60) { $key.CreateSubKey(('00-Padding\Row{0:D2}' -f $n)).Dispose() }
            $key.CreateSubKey('A-Target\Inner\Leaf').Dispose()
            $key.CreateSubKey('B-Keep\Nested').Dispose()
        } finally { $key.Dispose() }

        View-Option 'Show keys in list'
        foreach ($path in '00-Padding\Row60', 'B-Keep\Nested', 'A-Target\Inner\Leaf') {
            Go-ToKey "$branch\$path"
            Assert-TreeSelection "$branch\$path"
        }
        View-Option 'Automatically sync tree'
        Go-ToKey $branch
        $tree = Pick 'Group' 'Registry keys'
        $anchor = @((Elements $tree.children) | Where-Object {
            $_.type -eq 'TreeItem' -and $_.name -like "$branch\00-Padding\Row*" -and $_.y -gt $tree.y + $_.height
        } | Sort-Object y)[0]
        if (-not $anchor -or $anchor.name -like '*Row01') { throw 'Expected a scrolled tree with a visible padding row.' }
        function Assert-BranchState {
            $items = Read-Ui
            $row = @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $anchor.name })
            $pane = @($items | Where-Object automationId -eq 'RegistryTree')[0]
            if ($row.Count -ne 1 -or [Math]::Abs(($row[0].y - $pane.y) - ($anchor.y - $tree.y)) -gt 2) {
                throw "Tree scroll moved at $($anchor.name): expected $($anchor.y - $tree.y), found $($row[0].y - $pane.y)."
            }
            if ($items.name -notcontains "$branch\B-Keep\Nested") { throw 'The untouched sibling collapsed.' }
        }

        function Assert-RenameFocus {
            Wait-Ui {
                param($items)
                $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
                $items.automationId -contains 'PrimaryButton' -and $focused.hasFocus -and
                    $focused.element.type -eq 'Edit' -and $focused.element.name -eq 'New name'
            }
        }
        $target = Select-Row 'A-Target, Key,'
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $target.selector) | Out-Null
        Assert-RenameFocus
        Ui @('get-focused', '--json') | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'rename-initial-focus.json') -Encoding utf8
        Ui @('send-keys', 'C-Moved', '--via', 'send-input', '--verbatim') | Out-Null
        if ((Pick 'Edit' 'New name').value -cne 'C-Moved') { throw 'F2 did not focus and select the existing key name.' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Assert-BranchState
        if ((Read-Ui).name -contains "$branch\A-Target") { throw 'The old key remained in the tree.' }

        $target = Select-Row 'B-Keep, Key,'
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $target.selector) | Out-Null
        Assert-RenameFocus
        Fill 'New name' 'C-Moved'
        Primary
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and $items.name -contains 'A key with this name already exists.' }
        Assert-RenameFocus
        if ((Pick 'Edit' 'New name').value -cne 'C-Moved' -or (Read-Ui).automationId -contains 'EditOpenAsAdministrator') {
            throw 'A name collision lost the attempted name or offered permission recovery.'
        }
        Ui @('send-keys', 'B-Retry', '--via', 'send-input', '--verbatim') | Out-Null
        if ((Pick 'Edit' 'New name').value -cne 'B-Retry') { throw 'The retry did not retain and select its attempted name.' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $target = Select-Row 'B-Retry, Key,'
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $target.selector) | Out-Null
        Assert-RenameFocus
        Save-Name 'New name' 'B-Keep'
        Assert-BranchState

        Press 'New'
        $menu = Read-ContextMenu 'Key'
        Ui @('invoke', (Pick 'MenuItem' 'Key' -Window $menu.Window).selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Save-Name 'Key name' 'D-Created'
        Expect-HistoryLocation "$branch\D-Created"
        Wait-Ui { param($items) $items.name -contains "$branch\D-Created" } -All
        Assert-BranchState
        Press 'Up'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'B-Keep, Key,*' }).Count }
        $source = Select-Row 'B-Keep, Key,'
        $menu = Context-Menu $source.selector
        Ui @('invoke', 'EntryCopyMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Press 'Paste'
        Save-Name 'Name of the copy' 'E-Pasted'
        $copy = Select-Row 'E-Pasted, Key,'
        Wait-Ui { param($items) $items.name -contains "$branch\E-Pasted" } -All
        Assert-BranchState
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $copy.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and $items.name -notcontains "$branch\E-Pasted" } -All
        Assert-BranchState

        Go-ToKey "$branch\D-Created"
        Press 'Paste'
        Save-Name 'Name of the copy' 'LazyCopy'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'LazyCopy, Key,*' }).Count }
        if ((Read-Ui -All).name -contains "$branch\D-Created\LazyCopy") { throw 'Pasting expanded an unopened branch.' }
        Assert-BranchState
        Assert-TreeSelection "$branch\C-Moved\Inner\Leaf" -All
        View-Option 'Automatically sync tree'
        Assert-TreeSelection "$branch\D-Created"
        Ui @('invoke', (Pick 'TreeItem' "$branch\D-Created").selector, '--action', 'expand') | Out-Null
        Wait-Ui { param($items) $items.name -contains "$branch\D-Created\LazyCopy" } -All
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\BranchEdits\F-External")
        $key.Dispose()
        Go-ToKey "$branch\F-External"
        Assert-TreeSelection "$branch\F-External"
        $items = Read-Ui
        if ($items.name -notcontains "$branch\B-Keep\Nested" -or $items.name -notcontains "$branch\C-Moved\Inner\Leaf") {
            throw 'Tracking an externally created key collapsed an expanded branch.'
        }
        Go-ToKey $address
        View-Option 'Show keys in list'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteSubKeyTree('BranchEdits') } finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Tree context menu renames and deletes its key and protects roots' {
        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\TreeMenuBefore\Nested")
        try { $key.SetValue('Sentinel', 'preserved') } finally { $key.Dispose() }
        Go-ToKey "$address\TreeMenuBefore"
        Go-ToKey $address
        Select-Row 'Counter' | Out-Null
        $menu = Context-Menu (Pick 'TreeItem' "$address\TreeMenuBefore").selector
        foreach ($id in 'TreeRenameMenu', 'TreeDeleteMenu') {
            if (-not @($menu.Items | Where-Object { $_.automationId -eq $id -and $_.isEnabled }).Count) {
                throw "Missing enabled tree command: $id"
            }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'tree-edit-menu.png')) -Window $menu.Window | Out-Null
        Ui @('invoke', 'TreeRenameMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'New name').value -ne 'TreeMenuBefore') { throw 'Tree menu rename targeted another item.' }
        Fill 'New name' 'TreeMenuAfter'
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Expect-HistoryLocation "$address\TreeMenuAfter"
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$subKey\TreeMenuAfter\Nested")
        try {
            if (-not $key -or $key.GetValue('Sentinel') -ne 'preserved') { throw 'Tree rename lost nested data.' }
        } finally { if ($key) { $key.Dispose() } }
        foreach ($confirm in $false, $true) {
            $menu = Context-Menu (Pick 'TreeItem' "$address\TreeMenuAfter").selector -Keyboard
            Ui @('invoke', 'TreeDeleteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
            $lines = @(Read-Ui | Where-Object type -eq 'Text' | ForEach-Object { ([string]$_.name -split '\r?\n')[0] })
            if ($lines -notcontains "$address\TreeMenuAfter") { throw 'Tree menu delete identified another key.' }
            if ($confirm) { Primary } else { Press 'Cancel' }
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        }
        Expect-HistoryLocation $address
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try {
            if ($key.GetSubKeyNames() -contains 'TreeMenuAfter' -or $key.GetSubKeyNames() -contains 'TreeMenuBefore' -or
                $key.GetValue('Counter') -ne 1) { throw 'Tree menu delete did not remove only its target.' }
        } finally { $key.Dispose() }
        foreach ($root in 'HKEY_CURRENT_USER', 'Computer') {
            $menu = Context-Menu (Pick 'TreeItem' $root).selector
            foreach ($id in 'TreeRenameMenu', 'TreeDeleteMenu') {
                $item = @($menu.Items | Where-Object automationId -eq $id)
                if ($item.Count -ne 1 -or $item[0].isEnabled) { throw "Tree edit enabled for root: $root ($id)" }
            }
            Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
        }
        Go-ToKey $address
    }

    Test-Ui 'Tree Delete and F2 target the node with a different list selection' {
        Browser
        Select-Row 'Counter' | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $address }).Count -eq 1 }
        $node = Pick 'TreeItem' $address
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $node.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'New name').value -ne $fixtureName) { throw 'Tree rename picked the selected list value.' }
        Press 'Cancel'
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $node.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        $lines = @(Read-Ui | Where-Object type -eq 'Text' | ForEach-Object { ([string]$_.name -split '\r?\n')[0] })
        if ($lines -notcontains $address) { throw 'Tree delete confirmation did not identify the invoked key.' }
        Press 'Cancel'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if ($key.GetValue('Counter') -ne 1) { throw 'Canceling a tree edit changed the fixture.' } }
        finally { $key.Dispose() }
    }

    Test-Ui 'Empty list selection ignores Delete and F2 after filtering and refresh' {
        Browser
        function Assert-EmptyShortcuts([switch] $KeepFocus) {
            $list = Pick 'List' 'Registry keys and values'
            if ($KeepFocus) {
                $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
                if ($focused.automationId -ne 'EntriesList' -and $focused.type -ne 'ListItem') { throw "Expected focus in the registry list after deleting a row, got '$($focused.name)' ($($focused.type))." }
            } else {
                $x = [int]($list.x + $list.width / 2)
                $y = [int]($list.y + $list.height / 2)
                if (@((Elements $list.children) | Where-Object { $_.type -eq 'ListItem' -and $_.y -le $y -and $_.y + $_.height -gt $y }).Count) { throw 'Expected blank list space.' }
                [UxTestWindow]::LeftClickAt($x, $y)
            }
            foreach ($shortcut in 'delete', 'f2') {
                $selected = @((Elements $list.children) | Where-Object type -eq 'ListItem' | Where-Object {
                    (Ui @('get-property', $_.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json).properties.IsSelected -eq 'True'
                })
                if ($selected.Count) { throw 'The list should have no selected row.' }
                Ui @('send-keys', $shortcut, '--via', 'send-input') | Out-Null
                if ((Read-Ui).automationId -contains 'PrimaryButton') { throw "$shortcut opened an edit dialog with no list selection." }
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
                try {
                    if (-not $key -or $key.GetValue('Counter') -ne 1 -or $key.GetSubKeyNames() -notcontains 'Child') { throw "$shortcut changed the fixture key." }
                } finally { if ($key) { $key.Dispose() } }
            }
        }
        Select-Row 'Counter' | Out-Null
        Press 'Filter'; Fill 'Filter this key' 'NoMatchingFixtureRows'
        Wait-Ui { param($items) $items.name -contains 'No matching items' }
        Assert-EmptyShortcuts
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'f5', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' }).Count }
        Assert-EmptyShortcuts

        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('DeleteOnceFixture', 'disposable') } finally { $key.Dispose() }
        Ui @('send-keys', 'f5', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'DeleteOnceFixture,*' }).Count }
        $row = Select-Row 'DeleteOnceFixture'
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'DeleteOnceFixture,*' }).Count -eq 0 }
        Assert-EmptyShortcuts -KeepFocus
    }

    Test-Ui 'Blank list space accepts keyboard focus with and without rows' {
        [UxTestWindow]::RequireForeground()
        function Click-ListSpace {
            $list = Pick 'List' 'Registry keys and values'
            $x = [int]($list.x + $list.width / 2)
            $y = [int]($list.y + $list.height - 20)
            if (@((Elements $list.children) | Where-Object { $_.type -eq 'ListItem' -and $_.y -le $y -and $_.y + $_.height -gt $y }).Count) { throw 'The test click overlaps a list row.' }
            [UxTestWindow]::LeftClickAt($x, $y)
        }
        Ui @('focus', (Pick 'Edit' 'Search registry').selector) | Out-Null
        Click-ListSpace
        Ui @('send-keys', 'home down', '--via', 'send-input') | Out-Null
        $list = Pick 'List' 'Registry keys and values'
        $rows = @((Elements $list.children) | Where-Object type -eq 'ListItem')
        $selection = Ui @('get-property', $rows[1].selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        if ($selection.properties.IsSelected -ne 'True') { throw 'Arrow keys did not select a row after clicking blank space.' }

        Ui @('focus', (Pick 'Edit' 'Search registry').selector) | Out-Null
        Click-ListSpace
        Ui @('send-keys', 'down', '--via', 'send-input') | Out-Null
        $selection = Ui @('get-property', $rows[2].selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        if ($selection.properties.IsSelected -ne 'True') { throw 'Blank-space focus did not resume keyboard navigation from the previous row.' }

        Press 'Filter'; Fill 'Filter this key' 'NoMatchingFixtureRows'
        Wait-Ui { param($items) @($items | Where-Object type -eq 'ListItem').Count -eq 0 }
        Ui @('focus', (Pick 'Edit' 'Search registry').selector) | Out-Null
        Click-ListSpace
        Ui @('send-keys', 'x', '--verbatim', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FilterBox' -and $_.value -eq 'NoMatchingFixtureRowsx' }).Count -eq 1 }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'FilterBox' }
    }

    Test-Ui 'Value edits preserve list selection scroll and keyboard focus' {
        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            foreach ($i in 1..200) { $key.SetValue(('Entry {0:D3}' -f $i), 'before') }
        } finally { $key.Dispose() }
        Press 'Refresh'
        Wait-Ui { param($items) $items.name -contains 'Entry 001, REG_SZ, before' }
        try {
            foreach ($method in 'Double-click', 'Enter', 'Menu', 'Binary') {
                $list = Pick 'List' 'Registry keys and values'
                $startRow = @(Elements $list.children | Where-Object type -eq 'ListItem')[0]
                Ui @('send-keys', 'ctrl+end pageup', '--via', 'send-input', '--target', $startRow.selector) | Out-Null
                $list = Pick 'List' 'Registry keys and values'
                $rows = @(Elements $list.children | Where-Object {
                    $_.type -eq 'ListItem' -and $_.name -like 'Entry *' -and
                    $_.y -gt $list.y -and $_.y + $_.height -lt $list.y + $list.height
                } | Sort-Object y)
                if ($rows.Count -lt 3 -or $rows[0].name -like 'Entry 001,*') {
                    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-edit-scroll-setup.png')) | Out-Null
                    throw "Expected a scrolled list of fixture values; found $($rows.Count) rows: $($rows.name -join '; ')."
                }
                $row = $rows[2]
                $name = ($row.name -split ',')[0]
                $viewport = @{ List = 'Registry keys and values'; Name = $rows[0].name; Top = $rows[0].y - $list.y }
                Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
                switch ($method) {
                    'Double-click' { Ui @('click', $row.selector, '--double') | Out-Null }
                    'Enter' { Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row.selector) | Out-Null }
                    default {
                        $menu = Context-Menu $row.selector
                        $command = if ($method -eq 'Binary') { 'EntryBinaryMenu' } else { 'EntryOpenMenu' }
                        Ui @('invoke', $command, '--action', 'invoke') -Window $menu.Window | Out-Null
                    }
                }
                Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
                $data = 'after ' + $method
                if ($method -eq 'Binary') {
                    Ui @('send-keys', 'ctrl+a delete', '--via', 'send-input', '--target', 'ValueHexPreview') | Out-Null
                    $digits = [Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes("$data`0")).ToLowerInvariant().ToCharArray() -join ' '
                    Ui @('send-keys', $digits, '--via', 'send-input') | Out-Null
                } else {
                    Ui @('set-value', 'ValueDataEditor', $data) | Out-Null
                }
                Primary
                Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and $items.name -contains "$name, REG_SZ, after $method" }
                Assert-ResultViewport $viewport
                $updated = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq "$name, REG_SZ, after $method" })[0]
                $selection = Ui @('get-property', $updated.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
                if ([string]$selection.properties.IsSelected -ne 'True') { throw "$method lost the edited row selection." }
                $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
                if (-not $focused.hasFocus -or $focused.element.type -ne 'ListItem' -or $focused.element.name -ne $updated.name) {
                    throw "$method did not return keyboard focus to the edited row."
                }
            }
        } finally {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { foreach ($i in 1..200) { $key.DeleteValue(('Entry {0:D3}' -f $i)) } }
            finally { $key.Dispose() }
            Press 'Refresh'
        }
    }

    Test-Ui 'Refreshing one changed registry value keeps the other rows visually still' {
        Browser
        $stable = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'NeedleOne,*' })[0]
        $label = @(Elements $stable.children | Where-Object { $_.type -eq 'Text' -and $_.name -ceq 'NeedleOne' })[0]
        if (-not $label) { throw 'Could not locate the unchanged name cell.' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $original = $key.GetValue('Counter')
            $key.SetValue('Counter', 23, [Microsoft.Win32.RegistryValueKind]::DWord)
            $stop = [Threading.CancellationTokenSource]::new()
            $sample = [UxTestWindow]::SampleRowText([int]$label.x, [int]($label.y + $label.height / 2), [int][Math]::Min(60, $label.width), $stop.Token)
            try {
                Ui @('send-keys', 'f5', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Counter, REG_DWORD, 0x00000017 (23)' }
            } finally {
                $stop.Cancel()
                $frames = $sample.GetAwaiter().GetResult()
                $stop.Dispose()
            }
            @{ frames = $frames[0]; changedFrames = $frames[1] } | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'registry-row-motion.json')
            if ($frames[0] -lt 2 -or $frames[1]) { throw "Unchanged name text moved or faded in $($frames[1]) of $($frames[0]) samples." }
        } finally {
            $key.SetValue('Counter', $original, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.Dispose()
            Ui @('send-keys', 'f5', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        }
    }

    Test-Ui 'Rapid registry filtering and every clear path return keyboard input to the list' {
        Browser
        $path = "$address\FilterTyping"
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\FilterTyping")
        try {
            $payload = 'payload ' * 80
            foreach ($i in 1..4000) { $key.SetValue("Entry $i", $payload) }
            $key.SetValue('Unique match', 'end of the matching value')
        } finally { $key.Dispose() }
        Go-ToKey $path
        Wait-Ui { param($items) $items.name -contains '0 keys · 4,001 values' -or $items.name -contains ('0 keys · {0:N0} values' -f 4001) }
        $stop = [Threading.CancellationTokenSource]::new()
        $probe = [UxTestWindow]::LongestWindowDelay([IntPtr]$mainWindow, $stop.Token)
        try {
            Ui @('send-keys', 'matching value', '--verbatim', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
            Wait-Ui { param($items) $items.value -contains 'matching value' -and $items.name -contains '1 shown' -and $items.name -contains 'Unique match, REG_SZ, end of the matching value' }
        } finally {
            $stop.Cancel()
            $maxDelay = $probe.GetAwaiter().GetResult()
            $stop.Dispose()
            @{ storedValues = 4001; longestUiReplyMs = $maxDelay } | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'filter-responsiveness.json')
        }
        if ($maxDelay -ge 1000) { throw "Typing into the filter stalled the window for $maxDelay ms." }
        foreach ($method in 'Ctrl+A Delete','Backspace','TextBox clear','Dismiss button','Escape') {
            switch ($method) {
                'Ctrl+A Delete' { Ui @('send-keys', 'ctrl+a delete', '--via', 'send-input') | Out-Null }
                'Backspace' {
                    Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
                    Ui @('send-keys', 'z', '--verbatim', '--via', 'send-input') | Out-Null
                    Ui @('send-keys', 'backspace', '--via', 'send-input') | Out-Null
                }
                'TextBox clear' {
                    $edit = Pick 'Edit' 'Filter this key'
                    [UxTestWindow]::LeftClickAt([int]($edit.x + $edit.width - $edit.height / 2), [int]($edit.y + $edit.height / 2))
                }
                'Dismiss button' { Press 'Clear filter' }
                'Escape' { Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null }
            }
            Wait-Ui { param($items) $items.automationId -notcontains 'FilterBox' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Entry *' }).Count -gt 0 }
            $focused = (Ui @('get-focused', '--json') | ConvertFrom-Json).element
            if ($focused.automationId -ne 'EntriesList' -and $focused.type -ne 'ListItem') { throw "$method left focus on $($focused.automationId) instead of the registry list." }
            if ($method -ne 'Escape') {
                Ui @('send-keys', 'Unique match', '--verbatim', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) $items.value -contains 'Unique match' -and $items.name -contains '1 shown' }
            }
        }
        Go-ToKey $address
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$subKey\FilterTyping")
    }

    Test-Ui 'Visible search and type-to-filter stay separate' {
        $items = Read-Ui
        if ($items.automationId -contains 'FilterBox' -or $items.automationId -notcontains 'QuickSearch') { throw 'Initial search/filter visibility is incorrect.' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'Needle', '--verbatim', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FilterBox' -and $_.value -eq 'Needle' }).Count -eq 1 -and
            $items.name -contains '2 shown' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' }).Count -eq 0 }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'FilterBox' }
        Press 'Filter'; Fill 'Filter this key' 'Needle'
        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Address' }
        $input = Pick 'Edit' 'Address'
        Ui @('set-value', $input.selector, ($address + '\Child')) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'FilterBox' -and $items.name -notcontains 'Address' }
        Press 'Back'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'browser.png')) | Out-Null
    }

    Test-Ui 'History menus shortcuts and mouse buttons share active-tab navigation' {
        Browser
        Go-ToKey ($address + '\Child')
        Expect-HistoryLocation ($address + '\Child')
        Press 'Up'; Expect-HistoryLocation $address

        $menu = Context-Menu 'BackButton'
        if ($menu.Items.Count -ne 2 -or $menu.Items[0].name -ne "$address\Child · 64-bit" -or $menu.Items[1].name -ne "$address · 64-bit") { throw 'Back history is not nearest-first.' }
        Ui @('invoke', $menu.Items[1].selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) -not ($items | Where-Object automationId -eq 'BackButton').isEnabled }
        $menu = Context-Menu 'ForwardButton' -Keyboard
        if ($menu.Items.Count -ne 2 -or $menu.Items[0].name -ne "$address\Child · 64-bit" -or $menu.Items[1].name -ne "$address · 64-bit") { throw 'Forward history lost skipped visits.' }
        Ui @('invoke', $menu.Items[1].selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) -not ($items | Where-Object automationId -eq 'ForwardButton').isEnabled }
        Ui @('send-keys', 'alt+left', '--via', 'send-input', '--target', (Pick 'Edit' 'Search registry').selector) | Out-Null
        Expect-HistoryLocation ($address + '\Child')
        Ui @('send-keys', 'alt+right', '--via', 'send-input', '--target', (Pick 'Edit' 'Search registry').selector) | Out-Null
        Expect-HistoryLocation $address
        Ui @('click', 'QuickSearch') | Out-Null
        [UxTestWindow]::NavigationClick(1); Expect-HistoryLocation ($address + '\Child')
        Ui @('click', 'QuickSearch') | Out-Null
        [UxTestWindow]::NavigationClick(2); Expect-HistoryLocation $address

        Press 'Add favorite'
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Ui @('click', (Pick 'Edit' 'Name').selector) | Out-Null
        [UxTestWindow]::NavigationClick(1)
        Start-Sleep -Milliseconds 150
        Press 'Cancel'
        Expect-HistoryLocation $address
        if ((Pick 'Button' 'Forward').isEnabled) { throw 'Mouse navigation changed history behind a dialog.' }

        Tool-Option 'Right sidebar'
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        Ui @('click', (Pick 'Edit' 'Search tool items').selector) | Out-Null
        [UxTestWindow]::NavigationClick(1)
        Start-Sleep -Milliseconds 150
        Open-Menu 'Close tab'
        Expect-HistoryLocation $address
        if ((Pick 'Button' 'Forward').isEnabled) { throw 'Mouse navigation changed an inactive registry tab.' }
    }

    Test-Ui 'Registry context menus distinguish items from empty space' {
        Browser
        $row = Select-Row 'Counter'
        $menu = Context-Menu $row.selector -Keyboard
        foreach ($name in @('Modify…', 'Modify binary data…', 'Inspect value…', 'Watch value', 'Rename…', 'Delete', 'Copy')) {
            if ($menu.Items.name -notcontains $name) { throw "Missing item command: $name" }
        }
        if ($menu.Items.name -contains 'New' -or $menu.Items.name -contains 'Paste') { throw 'Item menu contains background actions.' }
        Close-ContextMenu $menu
        Ui @('send-keys', 'shift+down', '--via', 'send-input', '--target', $row.selector) | Out-Null
        $menu = Context-Menu $row.selector
        if (($menu.Items | Where-Object name -eq 'Modify…').isEnabled -or -not ($menu.Items | Where-Object name -eq 'Delete').isEnabled) { throw 'Right-click discarded the multiple selection.' }
        Close-ContextMenu $menu

        $bytes = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Bytes,*' })[0]
        $menu = Context-Menu $bytes.selector
        Ui @('invoke', 'EntryRenameMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'New name').value -ne 'Bytes') { throw 'Item menu targeted the previous selection.' }
        Press 'Cancel'

        $menu = Context-Menu 'EntriesList'
        foreach ($name in @('New', 'Paste', 'Refresh', 'Copy current key path')) {
            if ($menu.Items.name -notcontains $name) { throw "Missing background command: $name" }
        }
        if (@($menu.Items | Where-Object name -in @('Modify…', 'Rename…', 'Delete', 'Watch value')).Count) { throw 'Background menu exposes actions for the selected row.' }
        Close-ContextMenu $menu

        $row = Select-Row '(Default)'
        $menu = Context-Menu $row.selector
        if (-not ($menu.Items | Where-Object name -eq 'Watch value').isEnabled -or ($menu.Items | Where-Object name -eq 'Inspect value…').isEnabled) { throw 'Unset default value has incorrect context actions.' }
        Close-ContextMenu $menu
        Ui @('send-keys', 'shift+down', '--via', 'send-input', '--target', $row.selector) | Out-Null
        $menu = Context-Menu $row.selector
        Ui @('invoke', 'EntryDeleteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if (@(Read-Ui | Where-Object name -like '*\Bytes*').Count -eq 0) { throw 'Mixed default-placeholder selection did not target the stored value.' }
        Press 'Cancel'

        View-Option 'Show keys in list'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child,*' }).Count -eq 1 }
        $row = Select-Row 'Child, Key,'
        $menu = Context-Menu $row.selector
        foreach ($name in @('Open', 'Copy key path', 'Add to favorites…', 'Export…', 'Permissions…', 'Rename…', 'Delete')) {
            if ($menu.Items.name -notcontains $name) { throw "Missing key command: $name" }
        }
        if ($menu.Items.name -contains 'Watch value') { throw 'Key menu contains value-only actions.' }
        Close-ContextMenu $menu
        $menu = Context-Menu 'EntriesList'
        $copy = @($menu.Items | Where-Object name -eq 'Copy current key path')[0]
        Ui @('invoke', $copy.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Assert-Clipboard $address 'path-list-background'
        Press 'Copy current key path'
        Assert-Clipboard $address 'path-list-overflow'
        Press 'Selection'
        $menu = Read-ContextMenu
        if ($menu.Items.name -notcontains 'Open' -or $menu.Items.name -notcontains 'Add to favorites…' -or $menu.Items.name -contains 'Watch value') { throw 'Selection submenu does not match the selected key context menu.' }
        Ui @('invoke', 'EntryFavoriteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'Name').value -ne 'Child') { throw 'Selection favorite targeted the current key instead of the selected key.' }
        Press 'Cancel'
        View-Option 'Show keys in list'
        Press 'Filter'; Fill 'Filter this key' 'no-context-menu-matches'
        $menu = Context-Menu 'EntriesList'
        if ($menu.Items.name -notcontains 'New' -or $menu.Items.name -contains 'Rename…') { throw 'Empty-state overlay blocked the background menu.' }
        Close-ContextMenu $menu
        Press 'Clear filter'
    }

    Test-Ui 'Optional parent row navigates without becoming a registry item' {
        Browser
        View-Option 'Show keys in list'
        View-Option 'Show parent key ..'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith('..,') }).Count -eq 1 }
        $rows = @(Read-Ui | Where-Object type -eq 'ListItem')
        if (-not $rows[0].name.StartsWith('..,')) { throw 'Parent row is not first.' }
        $parent = $rows[0]
        $menu = Context-Menu $parent.selector
        if ($menu.Items.name -notcontains 'Open parent key' -or $menu.Items.name -contains 'Watch value') { throw 'Parent row has incorrect navigation actions.' }
        foreach ($name in @('Rename…', 'Delete', 'Copy', 'Export…', 'Permissions…')) {
            if ($menu.Items.name -contains $name) { throw "Parent row exposes $name." }
        }
        Close-ContextMenu $menu
        foreach ($key in @('f2', 'delete')) {
            Ui @('send-keys', $key, '--via', 'send-input', '--target', $parent.selector) | Out-Null
            if ((Read-Ui).automationId -contains 'PrimaryButton') { throw 'Parent row opened a mutation dialog.' }
        }
        Ui @('send-keys', 'shift+down', '--via', 'send-input', '--target', $parent.selector) | Out-Null
        $menu = Context-Menu $parent.selector
        Ui @('invoke', 'EntryDeleteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if (@(Read-Ui | Where-Object name -like '*\Child*').Count -eq 0) { throw 'Mixed parent selection did not target the real child key.' }
        Press 'Cancel'
        View-Option 'Show keys in list'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith('..,') }).Count -eq 0 }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (-not $saved.ShowParentKeyInList) { throw 'Hiding keys discarded the parent-row preference.' }
        View-Option 'Show keys in list'
        $child = Select-Row 'Child, Key,'
        Ui @('click', $child.selector, '--double') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq ".., Parent key, $address" }).Count -eq 1 }
        $parent = Pick 'ListItem' ".., Parent key, $address"
        Ui @('invoke', $parent.selector, '--action', 'select') | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $parent.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' }).Count -eq 1 }
        View-Option 'Show parent key ..'
        View-Option 'Show keys in list'
    }

    Test-Ui 'Middle-click opens clicked tree and list targets in background tabs' {
        Browser
        $tab = Middle-Open (Pick 'TreeItem' 'Computer') 'Computer'
        Expect-HistoryLocation $address
        Close-MiddleTab $tab; Browser
        View-Option 'Show keys in list'
        View-Option 'Show parent key ..'
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'shift+down', '--via', 'send-input', '--target', $row.selector) | Out-Null
        $child = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child,*' })[0]
        $tab = Middle-Open $child 'Child'
        Expect-HistoryLocation $address
        Press 'Selection'
        $menu = Read-ContextMenu
        if ($menu.Items.name -contains 'Modify…' -or $menu.Items.name -contains 'Open' -or $menu.Items.name -notcontains 'Delete') { throw 'Middle-click changed the multiple selection.' }
        Close-ContextMenu $menu
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Expect-HistoryLocation ($address + '\Child')
        Open-Menu 'Close tab'; Browser

        Select-Row 'Child, Key,' | Out-Null
        $value = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' })[0]
        $tab = Middle-Open $value $fixtureName
        Expect-HistoryLocation $address
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' }).Count -eq 1 }
        Press 'Selection'
        $menu = Read-ContextMenu
        Ui @('invoke', 'EntryRenameMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'New name').value -ne 'Counter') { throw 'Middle-click did not select the clicked value in its new tab.' }
        Press 'Cancel'
        Open-Menu 'Close tab'; Browser

        $parent = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith('..,') })[0]
        $tab = Middle-Open $parent 'Software'
        Expect-HistoryLocation $address
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Expect-HistoryLocation 'HKEY_CURRENT_USER\Software'
        Open-Menu 'Close tab'; Browser
        View-Option 'Show parent key ..'
        View-Option 'Show keys in list'
    }

    Test-Ui 'Middle-click closes active and background tabs through the native close action' {
        Browser
        foreach ($active in @($false, $true)) {
            Open-Menu 'New tab'
            Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Address' }
            $input = Pick 'Edit' 'Address'
            Ui @('set-value', $input.selector, ($address + '\Child')) | Out-Null
            Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
            Expect-HistoryLocation ($address + '\Child')
            if (-not $active) { Browser }
            Close-MiddleTab (Pick 'TabItem' 'Child')
            Expect-HistoryLocation $address
        }
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Close-MiddleTab (Pick 'TabItem' 'Settings')
        Expect-HistoryLocation $address
    }

    Test-Ui 'Browser list and tree share the same Favorites store' {
        Browser
        $showKeys = (Get-Content $settingsFile -Raw | ConvertFrom-Json).ShowKeysInList
        if (-not $showKeys) { View-Option 'Show keys in list' }
        $childAddress = "$address\Child"
        $row = Select-Row 'Child, Key,'
        $menu = Context-Menu $row.selector
        Ui @('invoke', 'EntryFavoriteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Fill 'Name' 'Context-menu fixture bookmark'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $saved = (Get-Content $settingsFile -Raw | ConvertFrom-Json).Favorites | Where-Object Name -eq 'Context-menu fixture bookmark'
        if ($saved.Address -ne $childAddress -or $saved.Is32Bit -or -not $saved.Id) { throw 'The list did not bookmark the invoked key and view.' }
        Open-Menu 'Favorites' -Group 'Tools' -MainTab
        Show-RootFavorites
        Fill 'Search tool items' 'Context-menu fixture bookmark'
        $target = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Context-menu fixture bookmark*' })
        if ($target.Count -ne 1 -or (Pick 'Button' 'Open').isEnabled) { throw 'Expected one unselected bookmark for the background gesture.' }
        $background = Middle-Open $target[0] 'Child'
        if ((Read-Ui).automationId -notcontains 'ShowInPanelButton' -or (Pick 'Button' 'Open').isEnabled) { throw 'Middle-click changed the active organizer or its selection.' }
        Close-MiddleTab $background
        Browser
        Go-ToKey $childAddress
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        Go-ToKey $address
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 }
        $root = Pick 'TreeItem' $address
        Ui @('invoke', $root.selector, '--action', 'expand') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $childAddress }).Count -eq 1 }
        $menu = Context-Menu (Pick 'TreeItem' $childAddress).selector -Keyboard
        if (($menu.Items | Where-Object automationId -eq 'TreeFavoriteMenu').name -ne 'Remove from favorites') { throw 'The tree did not recognize the bookmark saved from the list.' }
        Ui @('invoke', 'TreeFavoriteMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { -not @((Get-Content $settingsFile -Raw | ConvertFrom-Json).Favorites | Where-Object Id -eq $saved.Id).Count }
        Go-ToKey $childAddress
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 }
        Ui @('send-keys', 'ctrl+d', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Fill 'Name' 'Keyboard toggle fixture'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        Ui @('send-keys', 'ctrl+d', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 -and $items.automationId -notcontains 'PrimaryButton' }
        Press 'Up'
        Expect-HistoryLocation $address
        if (-not $showKeys) { View-Option 'Show keys in list' }
    }

    Test-Ui 'Toolbar context actions and application Tools stay in sync' {
        Browser
        Set-TestWindowForeground
        if ((Read-Ui).automationId -contains 'ToolSelector') { Press 'Close tool panel' }
        if ((Read-Ui).automationId -contains 'BottomTabs') { Press 'Hide bottom panel' }
        $toolbarBefore = Pick 'Button' 'Tools'
        $treeBefore = @(Read-Ui | Where-Object automationId -eq 'RegistryTree')[0]
        $menu = Context-Menu 'ToolsButton' -Keyboard
        if ($menu.Items.name -notcontains 'Customize' -or $menu.Items.name -contains 'Hide from toolbar') { throw 'Tools must offer Customize without hiding its fallback menu.' }
        Ui @('invoke', @($menu.Items | Where-Object name -eq 'Customize')[0].selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolbarFavoritesOption' }
        foreach ($feature in 'Favorites', 'Sidebar', 'BottomPanel') {
            Ui @('invoke', "Toolbar${feature}Option", '--action', 'toggle-on') | Out-Null
        }
        Browser
        $toolbarGeometry = @(Read-Ui)
        $toolsGeometry = @($toolbarGeometry | Where-Object automationId -eq 'ToolsButton')[0]
        $treeGeometry = @($toolbarGeometry | Where-Object automationId -eq 'RegistryTree')[0]
        if ($toolsGeometry.height -ne $toolbarBefore.height -or $toolsGeometry.y -ne $toolbarBefore.y -or $treeGeometry.y -ne $treeBefore.y) {
            throw 'Enabling shortcuts changed the navigation toolbar height or position.'
        }
        foreach ($id in 'ToolbarFavorites', 'ToolbarSidebar', 'ToolbarBottomPanel') {
            $shortcut = @($toolbarGeometry | Where-Object automationId -eq $id)[0]
            if (-not $shortcut -or [Math]::Abs($shortcut.height - $toolsGeometry.height) -gt 1 -or [Math]::Abs($shortcut.y - $toolsGeometry.y) -gt 1) {
                throw "Toolbar shortcut $id does not match the Tools button height and alignment."
            }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-icons-aligned.png')) | Out-Null
        Ui @('invoke', 'ToolbarFavorites') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        if ((Read-Ui).name -contains 'Pin tool panel') { Press 'Pin tool panel' }
        Ui @('invoke', 'ToolbarBottomPanel') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.automationId -contains 'ToolSelector' }
        Press 'Tools'
        $toolbarMenu = Read-ToolbarTools
        $toolbarNames = @($toolbarMenu.Items | Where-Object automationId -notlike 'ToolsMenuPin*' | ForEach-Object name)
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Press 'Application menu'
        $applicationMenu = Read-ContextMenu 'Tools'
        $tools = @($applicationMenu.Items | Where-Object name -eq 'Tools')[0]
        Ui @('invoke', $tools.selector, '--action', 'expand') -Window $applicationMenu.Window | Out-Null
        $menu = Read-ContextMenu 'Right sidebar'
        if (($menu.Items.name -join '|') -cne ($toolbarNames -join '|')) { throw 'The Application and toolbar Tools menus have different entries or ordering.' }
        foreach ($name in 'Right sidebar', 'Bottom panel') {
            $item = @($menu.Items | Where-Object name -eq $name)[0]
            $state = Ui @('get-property', $item.selector, '--property', 'ToggleState', '--json') -Window $menu.Window | ConvertFrom-Json
            if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?On$|^1$') { throw "The Application menu lost the $name checkmark." }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'application-tools.png')) | Out-Null
        $bottom = @($menu.Items | Where-Object name -eq 'Bottom panel')[0]
        Ui @('invoke', $bottom.selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' -and $items.automationId -contains 'ToolSelector' }
        Open-Menu 'Bottom panel' -Group 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        $menu = Context-Menu 'ToolbarFavorites'
        if ($menu.Items.name -notcontains 'Customize' -or $menu.Items.name -notcontains 'Hide from toolbar') { throw 'Feature buttons need Customize and Hide from toolbar.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-context-menu.png')) -Window $menu.Window | Out-Null
        Ui @('invoke', @($menu.Items | Where-Object name -eq 'Hide from toolbar')[0].selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolbarFavorites' -and $items.automationId -contains 'FavoriteTree' }
        if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).ToolbarButtons -ne 192) { throw 'Hide from toolbar changed another shortcut or failed to save.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-neutral-light.png')) | Out-Null
        Open-Menu 'Settings'
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Browser
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-neutral-dark.png')) | Out-Null
        $menu = Context-Menu 'ToolbarSidebar' -Keyboard
        Ui @('invoke', @($menu.Items | Where-Object name -eq 'Hide from toolbar')[0].selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolbarSidebar' -and $items.automationId -contains 'ToolSelector' }
        if ((Get-Content $settingsFile -Raw | ConvertFrom-Json).ToolbarButtons -ne 128) { throw 'Hiding a selected panel button also changed another shortcut.' }
        $back = Pick 'Button' 'Back'
        Ui @('click', '--at', "$([int]($back.x + 4)),$([int]($back.y + $back.height + 5))", '--right') | Out-Null
        $menu = Read-ContextMenu 'Customize'
        if ($menu.Items.name -contains 'Hide from toolbar') { throw 'Blank toolbar space must only offer Customize.' }
        Ui @('invoke', @($menu.Items | Where-Object name -eq 'Customize')[0].selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolbarBottomPanelOption' } -All
        Ui @('invoke', 'ToolbarBottomPanelOption', '--action', 'toggle-off') | Out-Null
        Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Open-Menu 'Favorites' -Group 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        Press 'Close tool panel'
        Browser
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolbarShortcuts' }
        if ((Read-Ui).automationId -contains 'BottomTabs') { Press 'Hide bottom panel' }
        Tool-Option 'Favorites'
        if ((Read-Ui).name -contains 'Unpin tool panel') { Press 'Unpin tool panel' }
        Press 'Close tool panel'
    }

    Test-Ui 'Toolbar Tools pins save from a narrow window with mouse and keyboard' {
        Browser
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 900, 900, 0x16)) { throw 'Could not narrow the toolbar fixture.' }
        Wait-Ui { param($items) $items.automationId -contains 'ToolsButton' -and $items.automationId -notcontains 'ToolbarShortcuts' }
        Set-TestWindowForeground
        Press 'Tools'
        $menu = Read-ToolbarTools
        $favorites = @($menu.Items | Where-Object automationId -eq 'ToolsMenuFavorites')[0]
        $pin = @($menu.Items | Where-Object automationId -eq 'ToolsMenuPinFavorites')[0]
        if ($pin.name -ne 'Pin Favorites to toolbar' -or $pin.x -le $favorites.x + $favorites.width / 2 -or
            $pin.x + $pin.width -gt $favorites.x + $favorites.width) {
            throw 'Favorites needs a separate pin button to the right of its action.'
        }
        $pinPoint = "$([int]($pin.x + $pin.width / 2)),$([int]($pin.y + $pin.height / 2))"
        Ui @('click', '--at', $pinPoint) | Out-Null
        $menu = Read-ToolbarTools
        $pin = @($menu.Items | Where-Object automationId -eq 'ToolsMenuPinFavorites')[0]
        if ($pin.name -ne 'Unpin Favorites from toolbar' -or (Get-Content $settingsFile -Raw | ConvertFrom-Json).ToolbarButtons -ne 1) {
            throw 'Pinning Favorites did not update the open flyout and save the shortcut.'
        }
        Wait-Ui { param($items) $items.automationId -contains 'ToolbarFavorites' -and $items.automationId -notcontains 'ToolSelector' } -All
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'enter', '--target', 'ToolsButton', '--via', 'send-input') | Out-Null
        Read-ToolbarTools | Out-Null
        Ui @('send-keys', 'right', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        $menu = Read-ToolbarTools
        if ($menu.Items.name -notcontains 'Pin Favorites to toolbar' -or (Get-Content $settingsFile -Raw | ConvertFrom-Json).ToolbarButtons -ne 0) {
            throw 'Unpinning Favorites did not update the open flyout and save the shortcut.'
        }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolbarShortcuts' }
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1280, 900, 0x16)) { throw 'Could not restore the toolbar fixture.' }
    }

    Test-Ui 'Toolbar feature shortcuts and panel toggles are optional and independent' {
        Browser
        Set-TestWindowForeground
        if ((Read-Ui).automationId -contains 'ToolSelector') { Press 'Close tool panel' }
        if ((Read-Ui).automationId -contains 'BottomTabs') { Press 'Hide bottom panel' }
        if ((Read-Ui).automationId -contains 'ToolbarShortcuts') { throw 'Optional toolbar buttons should start hidden.' }
        Press 'Tools'
        $menu = Read-ToolbarTools
        foreach ($name in @('Favorites', 'Notes', 'Watches', 'Change journal', 'Saved searches', 'Workspaces', 'Right sidebar', 'Bottom panel', 'Customize toolbar…')) {
            if ($menu.Items.name -notcontains $name) { throw "Missing Tools option: $name" }
        }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Tool-Option 'Right sidebar'
        if ((Read-Ui).name -contains 'Pin tool panel') { Press 'Pin tool panel' }
        Tool-Option 'Bottom panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -contains 'BottomTabs' }
        Press 'Tools'
        $menu = Read-ToolbarTools
        foreach ($name in 'Right sidebar', 'Bottom panel') {
            $item = @($menu.Items | Where-Object name -eq $name)[0]
            $state = Ui @('get-property', $item.selector, '--property', 'ToggleState', '--json') -Window $menu.Window | ConvertFrom-Json
            if ([string]$state.properties.ToggleState -notmatch '^(?:ToggleState_)?On$|^1$') { throw "$name does not reflect its open state." }
        }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Tool-Option 'Bottom panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -notcontains 'BottomTabs' }
        Tool-Option 'Right sidebar'
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' -and $items.automationId -notcontains 'BottomTabs' }

        Tool-Option 'Customize toolbar…'
        Wait-Ui { param($items) $items.automationId -contains 'ToolbarFavoritesOption' }
        $features = 'Favorites', 'Notes', 'Watches', 'Journal', 'SavedSearches', 'Workspaces', 'Sidebar', 'BottomPanel'
        foreach ($feature in $features) { Ui @('invoke', "Toolbar${feature}Option", '--action', 'toggle-on') | Out-Null }
        $toolbarSettings = Join-Path $dataFolder 'settings.json'
        if ((Get-Content $toolbarSettings -Raw | ConvertFrom-Json).ToolbarButtons -ne 255) { throw 'Toolbar choices were not persisted.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-settings.png')) | Out-Null
        Browser
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1600, 950, 0x16)) { throw 'Could not widen the toolbar fixture.' }
        Wait-Ui { param($items) @($features | Where-Object { $items.automationId -notcontains "Toolbar$_" }).Count -eq 0 }
        Ui @('invoke', 'ToolbarFavorites') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        Ui @('invoke', 'ToolbarNotes') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' }
        Ui @('invoke', 'ToolbarFavorites') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        Ui @('invoke', 'ToolbarJournal') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.automationId -contains 'FavoriteTree' }
        Ui @('invoke', 'ToolbarWatches') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Watches' -and $items.automationId -contains 'BottomTabs' }
        Ui @('invoke', 'ToolbarBottomPanel') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' -and $items.automationId -contains 'FavoriteTree' }
        Ui @('invoke', 'ToolbarBottomPanel') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        Ui @('invoke', 'ToolbarSidebar') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' -and $items.automationId -contains 'BottomTabs' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-buttons.png')) | Out-Null
        foreach ($feature in 'SavedSearches', 'Workspaces') {
            Ui @('invoke', "Toolbar$feature") | Out-Null
            $pageTitle = if ($feature -eq 'SavedSearches') { 'Saved searches' } else { 'Workspaces' }
            Wait-Ui { param($items) $items.name -contains $pageTitle -and $items.automationId -notcontains 'QuickSearch' }
            Browser
        }
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 760, 900, 0x16)) { throw 'Could not narrow the toolbar fixture.' }
        Wait-Ui { param($items) $items.automationId -contains 'ToolsButton' -and $items.automationId -contains 'EditAddressButton' -and $items.automationId -contains 'QuickSearch' }
        $narrowToolbar = @(Read-Ui)
        $narrowTools = @($narrowToolbar | Where-Object automationId -eq 'ToolsButton')[0]
        $shortcutsMore = @($narrowToolbar | Where-Object { $_.automationId -eq 'MoreButton' -and [Math]::Abs($_.y - $narrowTools.y) -le 1 })
        if ($shortcutsMore.Count -ne 1) { throw 'The narrow shortcut strip lost its overflow button.' }
        Set-TestWindowForeground
        Ui @('click', '--at', "$([int]($shortcutsMore[0].x + $shortcutsMore[0].width / 2)),$([int]($shortcutsMore[0].y + $shortcutsMore[0].height / 2))") | Out-Null
        $shortcutMenu = Read-ContextMenu 'Favorites'
        foreach ($name in 'Favorites', 'Notes', 'Watches', 'Change journal', 'Saved searches', 'Workspaces', 'Right sidebar', 'Bottom panel') {
            if ($shortcutMenu.Items.name -notcontains $name) { throw "Missing overflow shortcut: $name" }
        }
        $favoriteShortcut = @($shortcutMenu.Items | Where-Object name -eq 'Favorites')[0]
        Ui @('invoke', $favoriteShortcut.selector) -Window $shortcutMenu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        Press 'Close tool panel'
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' }
        Ui @('send-keys', 'enter', '--target', 'ToolsButton', '--via', 'send-input') | Out-Null
        $menu = Read-ToolbarTools
        if ($menu.Items.name -notcontains 'Bottom panel') { throw 'Narrow layout lost panel commands.' }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'toolbar-narrow.png')) | Out-Null
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1280, 900, 0x16)) { throw 'Could not restore the toolbar fixture.' }
        Open-Menu 'Settings'
        foreach ($feature in $features) { Ui @('invoke', "Toolbar${feature}Option", '--action', 'toggle-off') | Out-Null }
        Browser
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolbarShortcuts' }
        if ((Read-Ui).automationId -contains 'BottomTabs') { Press 'Hide bottom panel' }
        Tool-Option 'Favorites'
        if ((Read-Ui).name -contains 'Unpin tool panel') { Press 'Unpin tool panel' }
        Press 'Close tool panel'
    }

    Test-Ui 'Favorites sidebar keeps one toolbar and reveals search on demand' {
        Browser; Set-TestWindowForeground; Tool-Option 'Favorites'
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        $items = @(Read-Ui)
        foreach ($id in 'Search', 'SearchLabel', 'TreeHeading', 'TreeCount') {
            if ($items.automationId -contains $id) { throw "Redundant sidebar content is visible: $id" }
        }
        if (@($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Favorites' }).Count) { throw 'The sidebar still shows its redundant root.' }
        $selector = Pick 'ComboBox' 'Tool'
        foreach ($name in 'Add current key', 'New folder', 'Search favorites', 'Folder actions') {
            $button = Pick 'Button' $name
            if ([Math]::Abs($button.y + $button.height / 2 - $selector.y - $selector.height / 2) -gt 1) { throw "Sidebar action is below the header: $name" }
        }
        $tree = @($items | Where-Object automationId -eq 'FavoriteTree')[0]
        $resize = @($items | Where-Object automationId -eq 'ToolResize')[0]
        if ($resize.y + $resize.height - $tree.y - $tree.height -gt 16) { throw 'Favorites still reserves space at the bottom.' }
        Ui @('invoke', 'FavoritesSearchButton', '--action', 'toggle') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'Search' }
        $focused = (Ui @('get-focused', '--json')) -join "`n"
        if ($focused -notmatch '"automationId"\s*:\s*"Search"') { throw 'The search button did not focus its input.' }
        Fill 'Search tool items' 'no matching favorite'
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'Search') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' -and $items.automationId -notcontains 'Search' }
        Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'Search' }
        if ((Pick 'Edit' 'Search tool items').value) { throw 'Closing search retained an invisible filter.' }
        Ui @('invoke', 'FavoritesSearchButton', '--action', 'toggle') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'Search' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-sidebar-compact.png')) | Out-Null
        for ($step = 0; $step -lt 3; $step++) {
            Ui @('send-keys', 'right', '--via', 'send-input', '--target', 'ToolResize') | Out-Null
        }
        $selector = Pick 'ComboBox' 'Tool'
        $more = @(Read-Ui | Where-Object { $_.automationId -eq 'MoreButton' -and [Math]::Abs($_.y + $_.height / 2 - $selector.y - $selector.height / 2) -le 1 })
        if ($more.Count -ne 1 -or $selector.width -lt 100) { throw 'The narrow sidebar clipped its selector or lost overflow.' }
        Ui @('invoke', $more[0].selector) | Out-Null
        $menu = Read-ContextMenu 'Search favorites'
        if ($menu.Items.name -notcontains 'New folder' -or $menu.Items.name -notcontains 'Folder actions') { throw 'The narrow sidebar lost its commands.' }
        Close-ContextMenu $menu
        Ui @('send-keys', 'ctrl+f', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'Search' }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'Search') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-sidebar-narrow.png')) | Out-Null
        for ($step = 0; $step -lt 3; $step++) {
            Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'ToolResize') | Out-Null
        }
        Press 'Close tool panel'
    }

    Test-Ui 'Favorites panel transfers its search and selection to a shared tab' {
        Press 'Add favorite'; Fill 'Name' 'UX fixture favorite'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and @($items | Where-Object { $_.automationId -eq 'ToolsButton' -and $_.isEnabled }).Count -eq 1 }
        Tool-Option 'Right sidebar'
        Wait-Ui { param($items) $items.automationId -contains 'FavoriteTree' }
        if (@(Read-Ui | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Favorites' }).Count) { throw 'The sidebar repeated its root above saved favorites.' }
        Fill 'Search tool items' 'UX fixture'
        $favorite = Select-Row 'UX fixture favorite'
        $tab = Middle-Open $favorite $fixtureName
        if ((Read-Ui).automationId -notcontains 'ToolSelector' -or (Pick 'Edit' 'Search tool items').value -ne 'UX fixture') { throw 'Middle-click dismissed or reset the Favorites panel.' }
        Close-MiddleTab $tab; Browser
        if ((Read-Ui).automationId -notcontains 'ToolSelector') { Tool-Option 'Right sidebar' }
        $favorite = Select-Row 'UX fixture favorite'
        $menu = Context-Menu $favorite.selector -Keyboard
        if ($menu.Items.name -notcontains 'Rename…' -or $menu.Items.name -contains 'New folder') { throw 'Favorite row menu has incorrect scope.' }
        Close-ContextMenu $menu
        if ((Read-Ui).automationId -notcontains 'FavoriteTree') { throw 'Compact favorites must use one hierarchy.' }
        Select-Row 'UX fixture favorite' | Out-Null
        Press 'Open tool in tab'
        Wait-Ui { param($items) $items.name -contains 'Favorites' -and $items.automationId -contains 'ShowInPanelButton' }
        if ((Pick 'Edit' 'Search tool items').value -ne 'UX fixture') { throw 'Favorite query was lost.' }
        $tab = Middle-Open (Select-Row 'UX fixture favorite') $fixtureName
        if ((Read-Ui).automationId -notcontains 'ShowInPanelButton') { throw 'Middle-click switched away from the Favorites tab.' }
        Close-MiddleTab $tab
        Ui @('invoke', (Pick 'TabItem' 'Favorites').selector, '--action', 'select') | Out-Null
        Select-Row 'UX fixture favorite' | Out-Null
        Ui @('send-keys', 'f2', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Fill 'Name' 'UX fixture renamed'; Primary
        Browser; Tool-Option 'Right sidebar'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -in @('ListItem','TreeItem') -and $_.name -like '*UX fixture renamed*' }).Count -eq 1 }
        $tabsBeforePin = @(Read-Ui | Where-Object automationId -eq 'Tabs')[0]
        Press 'Pin tool panel'
        Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'ToolResize') | Out-Null
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (-not $saved.WindowSessions[-1].Layout.PanePinned -or $saved.WindowSessions[-1].Layout.PaneWidth -ne 380) { throw 'Pin or panel resize was not persisted.' }
        $tabsAfterPin = @(Read-Ui | Where-Object automationId -eq 'Tabs')[0]
        if ($tabsBeforePin.width -ne $tabsAfterPin.width) { throw 'Pinning squeezed the tab strip.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-panel.png')) | Out-Null
        Select-Row 'UX fixture renamed' | Out-Null
        Press 'Open tool in tab'
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' -and $items.automationId -contains 'ShowInPanelButton' }
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Favorites' }).Count -ne 1) { throw 'Expanding created a duplicate Favorites tab.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-tab.png')) | Out-Null
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -contains 'EntriesList' }
        if ((Pick 'Edit' 'Search tool items').value -ne 'UX fixture') { throw 'Returning to the panel lost the query.' }
        $focused = (Ui @('get-focused', '--json')) -join "`n"
        if ($focused -notmatch 'UX fixture renamed') { throw "Returning to the panel did not focus the selected favorite: $focused" }
        $favorite = Select-Row 'UX fixture renamed'
        Ui @('click', $favorite.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' }
        Press 'Unpin tool panel'
        $favorite = Select-Row 'UX fixture renamed'
        Ui @('click', $favorite.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' }
        Ui @('send-keys', 'f9', '--via', 'send-input', '--target', 'ToolsButton') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' }
        $search = Pick 'Edit' 'Search tool items'
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', $search.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -notcontains 'Search' }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' }
        $focused = (Ui @('get-focused', '--json')) -join "`n"
        if ($focused -notmatch 'ToolsButton') { throw 'Closing the panel did not return keyboard focus to Tools.' }
    }

    Test-Ui 'Favorites nested folder creation and selection do not reenter expansion' {
        Browser; Set-TestWindowForeground; Tool-Option 'Favorites'
        Press 'Pin tool panel'
        foreach ($name in 'Crash parent folder', 'Crash child folder') {
            Press 'New folder'; Fill 'Name' $name; Primary
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and $items.name -contains $name }
            Select-Folder $name
        }
        foreach ($name in 'Crash parent folder', 'Crash child folder', 'Crash parent folder', 'Crash child folder') {
            Set-TestWindowForeground
            $folder = Pick 'TreeItem' $name
            Ui @('click', $folder.selector) | Out-Null
            Wait-Ui {
                param($items)
                $folder = @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq $name })[0]
                $folder -and (Ui @('get-property', $folder.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json).properties.IsSelected -eq 'True'
            }
        }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $parent = @($saved.FavoriteFolders | Where-Object Name -eq 'Crash parent folder')[0]
        $child = @($saved.FavoriteFolders | Where-Object Name -eq 'Crash child folder')[0]
        if (-not $parent -or $child.ParentId -ne $parent.Id) { throw 'Nested folders were not preserved.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-nested-folder-selection.png')) | Out-Null
        Press 'Unpin tool panel'
        Press 'Close tool panel'
    }

    Test-Ui 'Favorites folders preserve navigation across panel and tab and failed saves' {
        Browser; Tool-Option 'Right sidebar'; Fill 'Search tool items' ''
        Press 'New folder'; Fill 'Name' 'UI favorites folder'; Primary
        Select-Folder 'UI favorites folder'
        Press 'New folder'; Fill 'Name' 'Nested UI folder'; Primary
        Select-Folder 'Nested UI folder'
        Press 'Add current key'; Fill 'Name' 'Nested UI favorite'; Primary
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $parent = @($saved.FavoriteFolders | Where-Object Name -eq 'UI favorites folder')[0]
        $nested = @($saved.FavoriteFolders | Where-Object Name -eq 'Nested UI folder')[0]
        $favorite = @($saved.Favorites | Where-Object Name -eq 'Nested UI favorite')[0]
        if (-not $parent -or $nested.ParentId -ne $parent.Id -or $favorite.FolderId -ne $nested.Id -or $favorite.Address -ne $address) { throw 'Nested favorites were not saved in their intended folder.' }
        Press 'Open tool in tab'
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' -and @($items | Where-Object { $_.type -in @('ListItem','TreeItem') -and $_.name -like '*Nested UI favorite*' }).Count -eq 1 }
        if ((Read-Ui).name -notcontains 'Nested UI folder' -or (Read-Ui).automationId -contains 'FolderUp') { throw 'Expanding Favorites lost its folder tree or retained browser navigation.' }
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and @($items | Where-Object { $_.type -in @('ListItem','TreeItem') -and $_.name -like '*Nested UI favorite*' }).Count -eq 1 }
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Press 'New folder'; Fill 'Name' 'Unsaved UI folder'; Primary
            Wait-Ui { param($items) $items.name -contains 'Could not complete the action' }
            if (@(Read-Ui | Where-Object { $_.type -eq 'TreeItem' -and $_.name -like '*Unsaved UI folder*' }).Count) { throw 'Failed folder save retained an unsaved item.' }
        } finally { $locked.Dispose() }
        Fill 'Search tool items' 'Nested UI favorite'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -in @('ListItem','TreeItem') -and $_.name -like '*Nested UI favorite*' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-folders.png')) | Out-Null
        Fill 'Search tool items' ''
        function Folder-Action([string] $Name) {
            Press 'Folder actions'
            $menu = Read-ContextMenu
            $command = @($menu.Items | Where-Object name -eq $Name)[0]
            if (-not $command.isEnabled) { throw "Folder action is unavailable: $Name" }
            Ui @('invoke', $command.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        }
        Select-Folder 'Nested UI folder'
        Folder-Action 'Rename…'; Fill 'Name' 'Renamed UI folder'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Renamed UI folder' }).Count -eq 1 }
        Press 'Open tool in tab'
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        Select-Folder 'Renamed UI folder'
        Folder-Action 'Move to folder…'
        Ui @('send-keys', 'home', '--via', 'send-input', '--target', (Pick 'ComboBox' 'Folder').selector) | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and -not ((Get-Content $settingsFile -Raw | ConvertFrom-Json).FavoriteFolders | Where-Object Id -eq $nested.Id).ParentId }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (($saved.FavoriteFolders | Where-Object Id -eq $nested.Id).ParentId -or ($saved.Favorites | Where-Object Name -eq 'Nested UI favorite').FolderId -ne $nested.Id) { throw 'Moving the folder did not preserve its bookmarks.' }
        Select-Folder 'Renamed UI folder'
        Folder-Action 'Move up'
        Wait-Ui { param($items) @((Get-Content $settingsFile -Raw | ConvertFrom-Json).FavoriteFolders | Where-Object { -not $_.ParentId })[0].Id -eq $nested.Id }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (@($saved.FavoriteFolders | Where-Object { -not $_.ParentId })[0].Id -ne $nested.Id) { throw 'Folder reordering did not persist.' }
        Folder-Action 'Remove selected'
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Press 'Cancel'
        if (-not @((Get-Content $settingsFile -Raw | ConvertFrom-Json).FavoriteFolders | Where-Object Id -eq $nested.Id).Count) { throw 'Cancel removed a Favorites subtree.' }
        Folder-Action 'Remove selected'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and -not @((Get-Content $settingsFile -Raw | ConvertFrom-Json).FavoriteFolders | Where-Object Id -eq $nested.Id).Count }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (@($saved.FavoriteFolders | Where-Object Id -eq $nested.Id).Count -or @($saved.Favorites | Where-Object FolderId -eq $nested.Id).Count -or -not @($saved.FavoriteFolders | Where-Object Id -eq $parent.Id).Count) { throw 'Folder removal failed to preserve unrelated items.' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if ($null -eq $key -or $key.GetValue('Counter') -ne 1) { throw 'Removing Favorites changed registry data.' } } finally { if ($key) { $key.Dispose() } }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-folders-organized.png')) | Out-Null
        Browser
    }

    Test-Ui 'Quick search transfers its query to advanced search and retains results' {
        Fill 'Search registry' 'needle'
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Find: needle' }).Count) { throw 'Typing started a scan.' }
        Press 'Advanced search'
        if ((Pick 'Edit' 'Find what').value -ne 'needle') { throw 'Advanced search lost the quick query.' }
        $form = @(Read-Ui)
        $activity = @($form | Where-Object automationId -eq 'SearchActivity')[0]
        $modified = @($form | Where-Object automationId -eq 'SearchKeyModified')[0]
        if (-not $activity -or -not $modified -or $form.name -notcontains 'Filters' -or
            [Math]::Abs($activity.y - $modified.y) -gt 2 -or ($activity.x + $activity.width) -gt $modified.x) {
            throw 'The activity and modification filters are not grouped side by side.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'advanced-search-layout.png')) | Out-Null
        Press 'Cancel'
        $input = Pick 'Edit' 'Search registry'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $input.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.name -contains 'Find: needle' }
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 }
        Open-Menu 'Close tab'; Browser
    }

    Test-Ui 'Typed match lists select fields in Advanced search, the search box, and the omnibox' {
        function Assert-MatchFieldResults([string] $Query, [string[]] $Expected) {
            Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.name -contains "Find: $Query" }
            Press 'Open panel tab in main tab'
            Wait-Ui {
                param($items)
                $hits = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'MatchField*' })
                foreach ($name in $Expected) { if (-not ($hits.name -like "$name,*")) { return $false } }
                $hits.Count -eq $Expected.Count -and
                    @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Run search' -and $_.isEnabled }).Count -eq 1
            }
        }

        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.CreateSubKey('MatchFieldNeedleKey').Dispose()
            $key.SetValue('MatchFieldNeedleName', 'unrelated')
            $key.SetValue('MatchFieldOnlyData', 'MatchFieldNeedle')

            $mixed = 'MatchFieldNeedle match:keys,names'
            Fill 'Search registry' $mixed
            Press 'Advanced search'
            if ((Pick 'Edit' 'Find what').value -ne 'MatchFieldNeedle') { throw 'The match list remained in the query text.' }
            $dataState = Ui @('get-property', 'SearchValueData', '--property', 'ToggleState', '--json') | ConvertFrom-Json
            if ([string]$dataState.properties.ToggleState -notmatch '^(?:ToggleState_)?Off$|^0$') { throw 'The typed list did not uncheck value data.' }
            Primary
            Assert-MatchFieldResults $mixed @('MatchFieldNeedleKey', 'MatchFieldNeedleName')
            Press 'Copy query' -CommandBarId 'ItemCommands'
            Assert-Clipboard $mixed 'match-field-query'
            Open-Menu 'Close tab'; Browser

            Fill 'Search registry' 'MatchFieldNeedle match:names'
            Press 'Start search'
            Assert-MatchFieldResults 'MatchFieldNeedle match:names' @('MatchFieldNeedleName')
            Open-Menu 'Close tab'; Browser

            Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
            Ui @('set-value', 'OmniboxInput', '?MatchFieldNeedle match:data') | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Search this branch' }
            Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
            Assert-MatchFieldResults 'MatchFieldNeedle match:data' @('MatchFieldOnlyData')
            Open-Menu 'Close tab'; Browser
        } finally {
            $key.DeleteSubKeyTree('MatchFieldNeedleKey', $false)
            $key.DeleteValue('MatchFieldNeedleName', $false)
            $key.DeleteValue('MatchFieldOnlyData', $false)
            $key.Dispose()
        }
    }

    Test-Ui 'Saved key notes match from the search box, Advanced search, and the omnibox' {
        function Open-NoteMatches {
            Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.name -contains "Find: $query" }
            Press 'Open panel tab in main tab'
            Wait-Ui {
                param($items)
                @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "$fixtureName,*Key note*$noteText*" }).Count -eq 1 -and
                    @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Run search' -and $_.isEnabled }).Count -eq 1
            }
        }

        Browser
        $noteText = 'Driver timeout note fixture'
        $query = 'Driver timeout match:notes'
        Press 'Notes for this key'
        $previousNote = (Pick 'Edit' 'Local note').value
        Fill 'Local note' $noteText
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' }
        Press 'Close tool panel'

        Fill 'Search registry' $query
        Press 'Start search'
        Open-NoteMatches
        Open-Menu 'Close tab'; Browser

        Press 'Advanced search'
        if ((Pick 'Edit' 'Find what').value -ne 'Driver timeout') { throw 'The typed note selector remained in the search text.' }
        $notesState = Ui @('get-property', 'SearchNotes', '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$notesState.properties.ToggleState -notmatch '^(?:ToggleState_)?On$|^1$') { throw 'The typed query did not check Notes.' }
        Primary
        Open-NoteMatches
        Press 'Copy query' -CommandBarId 'ItemCommands'
        Assert-Clipboard $query 'key-note-query'
        Open-Menu 'Close tab'; Browser

        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
        Ui @('set-value', 'OmniboxInput', "?$query") | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Search this branch' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Open-NoteMatches
        Open-Menu 'Close tab'; Browser

        Press 'Notes for this key'
        Fill 'Local note' $previousNote
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Press 'Close tool panel'
    }

    Test-Ui 'View options and narrow layout preserve browsing controls' {
        View-Option 'Show keys in list'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Child*' }).Count -eq 1 }
        View-Option 'Show keys in list'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Child*' }).Count -eq 0 }
        Press 'Filter'; Fill 'Filter this key' 'Needle'
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 760, 900, 0x16)) { throw 'Could not resize the fixture window.' }
        $items = Read-Ui
        foreach ($id in @('QuickSearch','FilterBox','ToolsButton','GlobalMenuButton','RegistryTree')) { if ($items.automationId -notcontains $id) { throw "Missing narrow control: $id" } }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'narrow.png')) | Out-Null
        Tool-Option 'Right sidebar'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'narrow-panel.png')) | Out-Null
        Press 'Close tool panel'
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not restore window width.' }
        Press 'Clear filter'
    }

    Test-Ui 'App edit is journaled and selectively reverted' {
        Browser
        Go-ToKey $address
        Select-Row 'Counter' | Out-Null
        Press 'Selection'
        $menu = Read-ContextMenu
        Ui @('invoke', 'EntryOpenMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Fill 'Value data' '2'; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Open-Menu 'Change journal' -Group 'Tools' -MainTab
        Wait-Ui { param($items)
            @($items | Where-Object { $_.type -eq 'Group' -and $_.name -eq 'Today' }).Count -eq 1 -and
                @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Edit value*' }).Count -ge 1
        }
        $target = "$address · Counter"
        Fill 'Search tool items' $target
        Select-Row $target | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Count' -and $_.name -like '*1 selected*' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-selected.png')) | Out-Null
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.automationId -contains 'InspectDetails' } -All
        $toolItems = Pick 'List' 'Tool items'
        $counterRow = @(Elements $toolItems.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' })
        if ($counterRow.Count -ne 1) { throw 'The compact Journal panel did not expose its selected Counter row.' }
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $counterRow[0].selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' -and $items.automationId -contains 'AfterText' } -All
        $compactDetails = Read-Details
        if ($compactDetails -notlike '*0x00000001*' -or $compactDetails -notlike '*0x00000002*') {
            throw 'Enter in the compact Journal panel did not inspect the selected edit.'
        }
        Set-Preview $false
        Wait-Ui { param($items) $items.automationId -notcontains 'BeforeText' } -All
        $toolItems = Pick 'List' 'Tool items'
        $counterRow = @(Elements $toolItems.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' })
        if ($counterRow.Count -ne 1) { throw 'Collapsing Journal details lost the selected Counter row.' }
        Set-TestWindowForeground
        Ui @('click', $counterRow[0].selector, '--double') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' -and $items.automationId -contains 'AfterText' } -All
        $compactDetails = Read-Details
        if ($compactDetails -notlike '*0x00000001*' -or $compactDetails -notlike '*0x00000002*') {
            throw 'Double-click in the compact Journal panel did not reopen the selected edit.'
        }
        Set-Preview $true
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' } -All
        Press 'Filter and scope' -CommandBarId 'Commands'
        if ((Pick 'Edit' 'Search tool items').value -ne $target) { throw 'Returning journal to the panel lost its query.' }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        $details = Read-Details
        if ($details -notlike '*0x00000001*' -or $details -notlike '*0x00000002*') { throw 'Journal selection did not survive the panel handoff.' }
        $automationRoot = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $timestampElement = $automationRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ToolDetailsTimestamp'))
        if (-not $timestampElement) { throw 'Journal inspector did not expose its timestamp readout through UI Automation.' }
        $timestampElement.SetFocus()
        $timestamp = Pick 'Text' 'Selected item local timestamp'
        $timestampRange = ([System.Windows.Automation.TextPattern]$timestampElement.GetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern)).DocumentRange
        $timestampValue = $timestampRange.GetText(-1).TrimEnd("`r", "`n")
        $displayedTimestamp = [datetime]::MinValue
        if (-not [datetime]::TryParse($timestampValue, [ref]$displayedTimestamp)) { throw "Journal inspector did not expose a readable local timestamp: '$timestampValue'." }
        if ($timestampElement.Current.HelpText -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}[+-]\d{2}:\d{2}$') { throw 'Journal inspector did not retain the exact local timestamp in accessible help.' }
        Set-Clipboard -Value 'journal timestamp sentinel'
        $timestampRange.Select()
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
        Assert-Clipboard $timestampValue 'journal-local-timestamp' 'journal timestamp sentinel'
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' -and $items.automationId -contains 'BeforeText' }
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Change journal' }).Count -ne 1) { throw 'Expanding created a duplicate journal tab.' }
        Set-Clipboard -Value 'journal after byte sentinel'
        Copy-DetailBytes 'After'
        Assert-Clipboard '02000000' 'journal-after-exact-bytes' 'journal after byte sentinel'
        Set-Clipboard -Value 'journal before byte sentinel'
        Copy-DetailBytes 'Before'
        Assert-Clipboard '01000000' 'journal-before-exact-bytes' 'journal before byte sentinel'
        Ui @('invoke', 'CompareRawBytes', '--action', 'expand') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'ByteDifference' } -All
        $automationRoot = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $rawComparisonElement = $automationRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CompareRawBytes'))
        if (-not $rawComparisonElement) { throw 'Raw-byte comparison expander is missing from UI Automation.' }
        $rawComparisonPattern = [System.Windows.Automation.ExpandCollapsePattern]$rawComparisonElement.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern
        )
        if ($rawComparisonPattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            throw 'Raw-byte comparison did not enter its expanded UIA state.'
        }
        $byteDifferenceElement = $automationRoot.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ByteDifference'))
        if (-not $byteDifferenceElement) { throw 'Raw-byte comparison did not expose its readout through UI Automation.' }
        $byteDifferenceRange = ([System.Windows.Automation.TextPattern]$byteDifferenceElement.GetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern)).DocumentRange
        $byteDifferenceValue = $byteDifferenceRange.GetText(-1).TrimEnd("`r", "`n")
        $byteDifferenceElement.SetFocus()
        $visibilityTimer = [Diagnostics.Stopwatch]::StartNew()
        while ($byteDifferenceElement.Current.IsOffscreen -and $visibilityTimer.Elapsed.TotalSeconds -lt 5) {
            Start-Sleep -Milliseconds 50
        }
        $byteDifferenceLines = $byteDifferenceValue.ReplaceLineEndings("`n")
        $byteDifferenceEvidence = @{
            text = $byteDifferenceLines; readOnly = $byteDifferenceElement.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text
            visible = -not $byteDifferenceElement.Current.IsOffscreen; focused = $byteDifferenceElement.Current.HasKeyboardFocus
        }
        $byteDifferenceEvidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'byte-difference.json')
        if (-not $byteDifferenceEvidence.visible -or -not $byteDifferenceEvidence.readOnly -or $byteDifferenceLines -notmatch '0x0\)' -or
            $byteDifferenceLines -notmatch '(?m)^Before: 01 00 00 00$' -or
            $byteDifferenceLines -notmatch '(?m)^After: 02 00 00 00$') {
            throw 'Raw-byte comparison did not show the exact first difference and both DWORD byte sequences.'
        }
        Set-Clipboard -Value 'journal raw-byte comparison sentinel'
        $byteDifferenceRange.Select()
        Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
        $copyExpected = $byteDifferenceValue.ReplaceLineEndings("`n")
        $clipboardTimer = [Diagnostics.Stopwatch]::StartNew()
        $copyActual = ''
        do {
            $copyActual = [string](Get-Clipboard -Raw)
            if ([string]::Equals($copyActual.ReplaceLineEndings("`n"), $copyExpected, [StringComparison]::Ordinal)) { break }
            Start-Sleep -Milliseconds 100
        } while ($clipboardTimer.Elapsed.TotalSeconds -lt 10)
        if (-not [string]::Equals($copyActual.ReplaceLineEndings("`n"), $copyExpected, [StringComparison]::Ordinal)) {
            throw 'Copying the read-only raw-byte comparison changed its contents beyond standard newline encoding.'
        }
        Ui @('invoke', 'CompareRawBytes', '--action', 'collapse') | Out-Null
        $collapseTimer = [Diagnostics.Stopwatch]::StartNew()
        do {
            if ($rawComparisonPattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { break }
            Start-Sleep -Milliseconds 50
        } while ($collapseTimer.Elapsed.TotalSeconds -lt 10)
        if ($rawComparisonPattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            throw 'Raw-byte comparison did not collapse through its native UIA state.'
        }
        $selectedDetails = Read-Details
        Ui @('invoke', 'InspectAfterValue', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Value inspector' -and @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and $_.name -eq 'Close' }).Count -eq 1 }
        Primary
        Wait-Ui { param($items) $items.name -notcontains 'Value inspector' -and $items.automationId -contains 'BeforeText' }
        if ((Read-Details) -cne $selectedDetails) { throw 'Closing the full inspector lost the exact selected journal comparison.' }
        Press 'Revert edit…'; Primary
        Wait-Ui { param($items) $items.name -contains 'Edit reverted' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try { if ($key.GetValue('Counter') -ne 1) { throw 'Revert did not restore the previous DWORD.' } } finally { $key.Dispose() }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal.png')) | Out-Null
        $journalRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Edit value,*' })[0]
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $journalRow.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains '2 items selected' }
        if ((Pick 'Button' 'Revert edit…').isEnabled -or (Read-Ui).automationId -contains 'BeforeText') { throw 'Journal multi-selection shows a single-edit inspector or incorrect actions.' }
        Press 'Copy script'
        $menu = Read-ContextMenu
        if ($menu.Items.name -notcontains 'Copy PowerShell' -or $menu.Items.name -notcontains 'Copy reg.exe (.cmd)') { throw 'The script menu lost an existing format.' }
        Ui @('invoke', ($menu.Items | Where-Object name -eq 'Copy reg.exe (.cmd)').selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Script copied' }
        $script = Get-Clipboard -Raw
        if ($script -notlike '*0x00000002*0x00000001*' -or ([regex]::Matches($script, '/v "Counter"')).Count -ne 2) { throw 'Journal script copy lost selected changes or their chronological order.' }
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'JournalScope') | Out-Null
        $historyLabels = @("User-profile history · $address · 64-bit", "Administrator-only history · $address · 64-bit")
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -in $historyLabels }).Count -eq 1 }
        Open-Menu 'Close tab'; Browser
        foreach ($value in @(3, 4)) {
            Select-Row 'Counter' | Out-Null
            Press 'Selection'
            $menu = Read-ContextMenu
            Ui @('invoke', 'EntryOpenMenu', '--action', 'invoke') -Window $menu.Window | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
            Fill 'Value data' "$value"; Primary
            Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        }
        Open-Menu 'Change journal' -Group 'Tools' -MainTab
        Fill 'Search tool items' $target
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Edit value,*' }).Count -ge 3 }
        $original = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Edit value,*' })[0]
        Ui @('invoke', $original.selector, '--action', 'select') | Out-Null
        Press 'Copy script'
        $menu = Read-ContextMenu
        Ui @('invoke', ($menu.Items | Where-Object name -eq 'Copy PowerShell').selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Script copied' }
        $scaleKey = $null
        try {
            if ($SandboxRun -ne [guid]::Empty) {
                $scaleKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Accessibility')
                $hadScale = $scaleKey.GetValueNames() -contains 'TextScaleFactor'
                $oldScale = $scaleKey.GetValue('TextScaleFactor')
                $scaleKey.SetValue('TextScaleFactor', 150, [Microsoft.Win32.RegistryValueKind]::DWord)
                [UxTestAccessibility]::BroadcastTextScaleChange()
                Start-Sleep -Milliseconds 500
            }
            if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 820, 900, 0x16)) { throw 'Could not narrow Journal.' }
            Wait-Ui { param($items) $items.automationId -contains 'InspectDetails' -and $items.automationId -contains 'Count' }
            $items = Read-Ui
            $rows = @($items | Where-Object type -eq 'ListItem')
            $items | Where-Object { $_.automationId -in 'Results','Count','ApplicationNotifications' -or $_.type -eq 'ListItem' } |
                Select-Object type,automationId,name,x,y,width,height | ConvertTo-Json |
                Set-Content (Join-Path $ArtifactDirectory 'compact-journal.json')
            if ($rows.Count -lt 3) { throw "The collapsed 820x900 Journal shows only $($rows.Count) rows at 150% text scale." }
            if ($items.automationId -contains 'BeforeText' -or -not ($items | Where-Object automationId -eq 'InspectDetails')) { throw 'The narrow Journal did not keep details collapsed with an explicit disclosure.' }
            foreach ($id in @('JournalRevert','InspectDetails')) {
                if (@($items | Where-Object automationId -eq $id).Count -ne 1) { throw "The narrow Journal lost reachable action '$id'." }
            }
            if ($items.automationId -contains 'ApplicationNotice') {
                throw 'Operation feedback still inserts an application status row above the Journal.'
            }
            $journalList = @($items | Where-Object automationId -eq 'Results')[0]
            if ($journalList.height -lt 200) { throw 'The narrow Journal lost its usable list area.' }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-narrow-150.png')) | Out-Null
            $selectedRow = @($rows | Where-Object name -ceq $original.name)
            if ($selectedRow.Count -ne 1) { throw 'The selected edit is missing from the narrow Journal list.' }
            Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $selectedRow[0].selector) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'BeforeText' }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-narrow-details-150.png')) | Out-Null
        } finally {
            if ($scaleKey) {
                if ($hadScale) { $scaleKey.SetValue('TextScaleFactor', $oldScale, [Microsoft.Win32.RegistryValueKind]::DWord) }
                else { $scaleKey.DeleteValue('TextScaleFactor', $false) }
                [UxTestAccessibility]::BroadcastTextScaleChange()
                $scaleKey.Dispose()
            }
            if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not restore Journal width.' }
        }
    }

    Test-Ui 'Journal archive filters retain original and revert links' {
        $journalFolder = Join-Path $dataFolder 'Journal'
        $null = [IO.Directory]::CreateDirectory($journalFolder)
        $originalId = [guid]::NewGuid()
        $revertId = [guid]::NewGuid()
        $started = [DateTimeOffset]::UtcNow.AddMinutes(-1)
        $location = @{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = $subKey; View = 256 }
        $state = { param([int] $number)
            @{ Scope = @{ Location = $location; ValueNames = @('Counter') }; Security = @(); Snapshot = @{
                Version = 1; Root = $location; Started = $started.ToString('o'); Completed = $started.ToString('o')
                Keys = @(@{ Path = ''; Values = @(@{ Name = 'Counter'; Type = 4; Data = [Convert]::ToBase64String([BitConverter]::GetBytes($number)) }) })
                Computer = $env:COMPUTERNAME } }
        }
        $original = @{ Id = $originalId; Time = $started.ToString('o'); Kind = 6; Operation = 'Archive fixture edit'
            Before = @((& $state 1)); After = @((& $state 2)) }
        $revert = @{ Id = $revertId; Time = $started.AddSeconds(1).ToString('o'); Kind = 9; Operation = 'Archive fixture revert'
            Reverts = $originalId; Before = @((& $state 2)); After = @((& $state 1)) }
        $originalFile = Join-Path $journalFolder ($originalId.ToString('N') + '.json')
        [IO.File]::WriteAllText($originalFile, ($original | ConvertTo-Json -Depth 10))
        [IO.File]::WriteAllText((Join-Path $journalFolder ($revertId.ToString('N') + '.json')), ($revert | ConvertTo-Json -Depth 10))
        $originalBytes = [IO.File]::ReadAllBytes($originalFile)
        $originalTime = [IO.File]::GetLastWriteTimeUtc($originalFile)
        Open-Menu 'Change journal' -Group 'Tools' -MainTab
        Press 'Refresh'
        Fill 'Search tool items' 'Archive fixture'
        Select-Row 'Archive fixture edit' | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'JournalArchive' -and $_.isEnabled }).Count -eq 1 }
        Press 'Archive edit'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Archive fixture edit,*' }).Count -eq 0 }

        function Set-ArchiveView([string] $Key) {
            Ui @('invoke', 'JournalFilters', '--action', 'invoke') | Out-Null
            $timer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
                $targets = @($script:mainWindow) + @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | ForEach-Object hwnd)
                foreach ($window in $targets) {
                    $filter = @(Read-Ui -Window $window | Where-Object automationId -eq 'JournalArchiveFilter')
                    if ($filter.Count -eq 1) {
                        Ui @('send-keys', $Key, '--via', 'send-input', '--target', $filter[0].selector) -Window $window | Out-Null
                        Ui @('send-keys', 'esc', '--via', 'send-input') -Window $script:mainWindow | Out-Null
                        return
                    }
                }
                Start-Sleep -Milliseconds 100
            } while ($timer.Elapsed.TotalSeconds -lt 15)
            throw 'Archive view selector did not open.'
        }

        Set-ArchiveView 'end'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Archive fixture edit,*' }).Count -eq 1 }
        Select-Row 'Archive fixture edit' | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Revert edit' }
        $link = @(Read-Ui | Where-Object name -eq 'Revert edit')[0]
        Ui @('invoke', $link.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Original edit · Archived' }
        $link = @(Read-Ui | Where-Object name -eq 'Original edit · Archived')[0]
        Ui @('invoke', $link.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'JournalArchive' -and $_.name -eq 'Unarchive edit' -and $_.isEnabled }).Count -eq 1 }
        Press 'Unarchive edit'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'JournalArchive' -and $_.name -eq 'Archive edit' }).Count -eq 1 }
        Set-ArchiveView 'home'
        Fill 'Search tool items' 'Archive fixture'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Archive fixture*' }).Count -eq 2 }
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($originalFile)) -cne [Convert]::ToBase64String($originalBytes) -or
            [IO.File]::GetLastWriteTimeUtc($originalFile) -ne $originalTime) { throw 'Archive or navigation rewrote the original journal JSON.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-archive-links.png')) | Out-Null
        Open-Menu 'Close tab'; Browser
    }

    Test-Ui 'Journal inspects damaged history and reverts legacy and typed renames' {
        $folder = Join-Path $dataFolder 'Journal'
        $null = [IO.Directory]::CreateDirectory($folder)
        $time = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('o')
        $sections = [Security.AccessControl.AccessControlSections]'Access, Owner, Group'
        $cases = foreach ($format in 'Legacy', 'Typed') {
            $old = "Journal$format-Old"
            $new = "Journal$format-New"
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\$new")
            try { $descriptor = $key.GetAccessControl($sections).GetSecurityDescriptorSddlForm($sections) }
            finally { $key.Dispose() }
            $state = { param($name, $exists)
                $location = @{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = "$subKey\$name"; View = 256 }
                $keys = @()
                $security = @()
                if ($exists) {
                    $keys = @(@{ Path = ''; Values = @() })
                    $security = @(@{ Path = ''; Descriptor = $descriptor })
                }
                @{ Scope = @{ Location = $location }; Security = $security; Snapshot = @{
                    Version = 1; Root = $location; Started = $time; Completed = $time; Keys = $keys; Computer = $env:COMPUTERNAME } }
            }
            $entry = @{ Id = [guid]::NewGuid(); Time = $time; Operation = 'Rename key'
                Before = @((& $state $old $true), (& $state $new $false))
                After = @((& $state $old $false), (& $state $new $true)) }
            if ($format -eq 'Typed') { $entry.Kind = 2; $entry.Operation = 'Changed rename label' }
            [IO.File]::WriteAllText((Join-Path $folder ($entry.Id.ToString('N') + '.json')), ($entry | ConvertTo-Json -Depth 10))
            @{ Old = $old; New = $new; Operation = $entry.Operation }
        }
        $damaged = Join-Path $folder ([guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::WriteAllText($damaged, '{')
        try {
            Open-Menu 'Change journal' -Group 'Tools' -MainTab
            Press 'Refresh'
            Wait-Ui { param($items) $items.name -contains 'Journal history incomplete' }
            Fill 'Search tool items' $cases[0].Old
            Select-Row ($address + '\' + $cases[0].Old) | Out-Null
            if ((Pick 'Button' 'Revert edit…').isEnabled) { throw 'Damaged history enabled Revert.' }
        } finally { [IO.File]::Delete($damaged) }
        Press 'Refresh'
        foreach ($case in $cases) {
            Fill 'Search tool items' $case.Old
            Select-Row ($address + '\' + $case.Old) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'JournalRevert' -and $_.isEnabled }).Count -eq 1 }
            Press 'Revert edit…'; Primary
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Revert: *' }).Count -eq 1 }
            $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
            try {
                if ($parent.GetSubKeyNames() -notcontains $case.Old -or $parent.GetSubKeyNames() -contains $case.New) {
                    throw "Failed to restore the original key for $($case.Operation)."
                }
            } finally { $parent.Dispose() }
            $row = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like ($case.Operation + ',*') })[0]
            Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
            if ((Pick 'Button' 'Revert edit…').isEnabled) { throw 'Already reverted rename stayed enabled.' }
        }
        Open-Menu 'Close tab'; Browser
    }

    Test-Ui 'Watch retains external before and after values' {
        Browser; Press 'Watch this branch' -CommandBarId 'Commands' -CommandBarChildId 'PermissionsButton'; Start-WatchPreview $address
        Wait-Ui { param($items) @($items | Where-Object name -like '*Watching*').Count -gt 0 }
        Open-Menu 'Close tab'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('Counter', 3, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
        Browser; Press 'Watch this branch' -CommandBarId 'Commands' -CommandBarChildId 'PermissionsButton'; Start-WatchPreview $address
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' }).Count -eq 1 }
        Fill 'Search tool items' 'Counter'
        Select-Row 'Counter' | Out-Null
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.automationId -contains 'InspectDetails' } -All
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -match 'Watching · 1 observation ·' }).Count -eq 1 } -All
        $toolItems = Pick 'List' 'Tool items'
        $counterRow = @(Elements $toolItems.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' })
        if ($counterRow.Count -ne 1) { throw 'The compact Watch panel did not expose its selected Counter observation.' }
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $counterRow[0].selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' -and $items.automationId -contains 'AfterText' } -All
        $compactDetails = Read-Details
        if ($compactDetails -notlike '*0x00000001*' -or $compactDetails -notlike '*0x00000003*') {
            throw 'Enter in the compact Watch panel did not inspect the selected observation.'
        }
        Set-Preview $false
        Wait-Ui { param($items) $items.automationId -notcontains 'BeforeText' } -All
        $toolItems = Pick 'List' 'Tool items'
        $counterRow = @(Elements $toolItems.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' })
        if ($counterRow.Count -ne 1) { throw 'Collapsing Watch details lost the selected Counter observation.' }
        Set-TestWindowForeground
        Ui @('click', $counterRow[0].selector, '--double') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' -and $items.automationId -contains 'AfterText' } -All
        $compactDetails = Read-Details
        if ($compactDetails -notlike '*0x00000001*' -or $compactDetails -notlike '*0x00000003*') {
            throw 'Double-click in the compact Watch panel did not reopen the selected observation.'
        }
        Set-Preview $true
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' } -All
        Press 'Filter and scope' -CommandBarId 'Commands'
        if ((Pick 'Edit' 'Search tool items').value -ne 'Counter') { throw 'Returning watch to the panel lost its query.' }
        Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'watch-panel.png')) | Out-Null
        Press 'Stop watching'
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' -and $items.automationId -contains 'BeforeText' }
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Watches' }).Count -ne 1) { throw 'Expanding created a duplicate Watches tab.' }
        $commandBars = @(Read-Ui | Where-Object { $_.type -eq 'AppBar' -and $_.automationId -eq 'Commands' })
        if ($commandBars.Count -ne 1) { throw 'The Watch command bar is unavailable for its Open key overflow check.' }
        $moreButtons = @(Elements $commandBars[0].children | Where-Object { $_.automationId -eq 'MoreButton' })
        if ($moreButtons.Count -ne 1) { throw 'The Watch command bar does not expose one overflow button.' }
        Ui @('invoke', $moreButtons[0].selector, '--action', 'invoke') | Out-Null
        $openKeyMenu = Read-ContextMenu -Expected 'Open key' -Types @('Button', 'MenuItem')
        $openKeyCommand = @($openKeyMenu.Items | Where-Object name -eq 'Open key')
        if ($openKeyCommand.Count -ne 1 -or $openKeyCommand[0].isOffscreen -or
            $openKeyCommand[0].width -le 0 -or $openKeyCommand[0].height -le 0) {
            throw 'The Watch overflow did not expose one visible Open key command.'
        }
        $watchTabCount = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        $x = [int]($openKeyCommand[0].x + $openKeyCommand[0].width / 2)
        $y = [int]($openKeyCommand[0].y + $openKeyCommand[0].height / 2)
        if ($openKeyMenu.Window -eq $mainWindow) {
            [UxTestWindow]::MiddleClickAt($x, $y)
        } else {
            [UxTestWindow]::MiddleClickAtOwnedPopup($x, $y, [IntPtr]$openKeyMenu.Window)
        }
        Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $watchTabCount + 1 } -All
        $openedKeyTab = @(Read-Ui -All | Where-Object type -eq 'TabItem' | Sort-Object x | Select-Object -Last 1)[0]
        if (-not $openedKeyTab -or $openedKeyTab.name -ne $fixtureName -or
            @(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Watches' }).Count -ne 1) {
            throw 'Middle-clicking Watch Open key did not preserve the source Watch tab and open its key.'
        }
        $watchTab = Pick 'TabItem' 'Watches'
        $watchSelection = Ui @('get-property', $watchTab.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        if ($watchSelection.properties.IsSelected -ne 'True') { throw 'Middle-click activated the destination instead of leaving Watches selected.' }
        $closeKeyTab = @(Elements $openedKeyTab.children | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq 'CloseButton' })
        if ($closeKeyTab.Count -ne 1) { throw 'The background Watch destination has no unique Close button.' }
        Ui @('invoke', $closeKeyTab[0].selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $watchTabCount } -All
        if ((Read-Ui).automationId -notcontains 'BeforeText') { throw 'Closing the middle-clicked key tab dismissed the source Watch details.' }
        $items = Read-Ui
        $details = Read-Details
        if ($details -notlike '*0x00000001*' -or $details -notlike '*0x00000003*') { throw 'Watch details do not contain both values.' }
        Press 'Copy script'
        $menu = Read-ContextMenu
        Ui @('invoke', ($menu.Items | Where-Object name -eq 'Copy PowerShell').selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Script copied' }
        Press 'Stop watching'
        Wait-Ui { param($items) @($items | Where-Object name -like '*Stopped*').Count -gt 0 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'watch.png')) | Out-Null
    }

    Test-Ui 'Watch compares and copies a one MiB value with a change beyond the preview' {
        $payload = [byte[]]::new(1MB)
        $offset = $payload.Length - 20
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\LargeWatch")
        try {
            $key.SetValue('BinaryTail', $payload, [Microsoft.Win32.RegistryValueKind]::Binary)
            Browser; Go-ToKey "$address\LargeWatch"; Press 'Watch this branch'; Start-WatchPreview "$address\LargeWatch"
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -like 'Watching · *' }).Count -eq 1 } -All
            Fill 'Search tool items' 'BinaryTail'
            $payload[$offset] = 255
            $key.SetValue('BinaryTail', $payload, [Microsoft.Win32.RegistryValueKind]::Binary)
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*BinaryTail*' }).Count -eq 1 }
            $row = Select-Row 'BinaryTail'
            $preview = Read-Details
            if ($row.name.Length -gt 1000 -or $preview.Length -gt 2000 -or $preview -notlike '*REG_BINARY*') {
                throw 'The Watch row or selected comparison lost its bounded binary preview.'
            }
            Ui @('invoke', 'CompareRawBytes', '--action', 'expand') | Out-Null
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
            $field = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ByteDifference'))
            $difference = ([System.Windows.Automation.TextPattern]$field.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)).DocumentRange.GetText(-1).TrimEnd("`r", "`n")
            if ($difference -notlike ('*(0x{0:X})*' -f $offset) -or $difference -notlike '*After: FF 00*' -or $difference.Length -gt 220) {
                throw 'The comparison did not locate the changed tail byte in a bounded readout.'
            }
            Set-Clipboard -Value 'large Watch sentinel'
            Copy-DetailBytes 'After'
            Assert-Clipboard ([Convert]::ToHexString($payload)) 'large-watch-bytes' 'large Watch sentinel'
            @{ bytes = $payload.Length; offset = $offset; comparison = $difference; previewCharacters = $preview.Length } |
                ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'large-watch.json')
            Press 'Stop watching'
        } finally { $key.Dispose() }
    }

    Test-Ui 'Watch viewing pause, restart and failure routing preserve session ownership' {
        $watchName = 'WatchDeliveryFixture'
        $watchPath = "$subKey\$watchName"
        $watchAddress = "$address\$watchName"
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($watchPath)
        try { $key.SetValue('Anchor', 1, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
        try {
            Browser; Go-ToKey $watchAddress; Press 'Watch this branch'; Start-WatchPreview $watchAddress
            Wait-Ui { param($items) @($items | Where-Object name -like '*Watching*').Count -gt 0 }
            Fill 'Search tool items' ''
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($watchPath, $true)
            try { $key.SetValue('Anchor', 2, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Anchor*' }).Count -eq 1 }
            Select-Row 'Anchor' | Out-Null
            $details = Read-Details
            Press 'Pause viewing' -CommandBarId 'Commands' -CommandBarChildId 'NewWatchButton'
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($watchPath, $true)
            try {
                foreach ($index in 0..79) { $key.SetValue(('Bulk{0:D3}' -f $index), $index, [Microsoft.Win32.RegistryValueKind]::DWord) }
                $key.SetValue('Other', 3, [Microsoft.Win32.RegistryValueKind]::DWord)
            } finally { $key.Dispose() }
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'WatchViewingStatus' -and $_.name -match 'Viewing paused.*[1-9][0-9]* new observations' }).Count -eq 1 }
            if (@(Read-Ui | Where-Object type -eq 'ListItem').Count -ne 1 -or (Read-Details) -ne $details) { throw 'Paused viewing changed retained rows or the selected evidence.' }
            Press 'Show in panel'
            Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
            Press 'Filter and scope' -CommandBarId 'Commands'
            Wait-Ui { param($items) @($items | Where-Object name -like 'Viewing paused*').Count }
            Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
            Set-Preview $true
            Wait-Ui { param($items) $items.automationId -contains 'BeforeText' } -All
            Press 'Resume viewing' -CommandBarId 'Commands' -CommandBarChildId 'NewWatchButton'
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Bulk*' }).Count -gt 0 }
            if ((Read-Details) -ne $details) { throw 'Resuming viewing lost selected before/after evidence.' }
            Press 'Open panel tab in main tab'
            Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' }
            Ui @('invoke', 'WatchFollowLatest', '--action', 'invoke') | Out-Null
            Ui @('scroll', (Pick 'List' 'Tool items').selector, '--to', 'top') | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Anchor*' }).Count -eq 1 }
            Select-Row 'Anchor' | Out-Null
            $viewport = Get-ResultViewport 'Tool items' 'Anchor'
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($watchPath, $true)
            try { $key.SetValue('Tail', 4, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'WatchViewingStatus' -and $_.name -like 'Reading earlier observations*' }).Count -eq 1 -and @($items | Where-Object name -like '*83 observations*').Count -gt 0 }
            Assert-ResultViewport $viewport
            if ((Read-Details) -ne $details) { throw 'A new observation replaced offscreen selected evidence.' }
            Press 'Stop watching'
            Wait-Ui { param($items) @($items | Where-Object name -like '*Stopped*').Count -gt 0 }
            if ((Read-Ui).name -contains 'Watch stopped unexpectedly') { throw 'Deliberate Stop raised an unexpected-stop notice.' }
            Press 'Watch again'
            Wait-Ui { param($items) @($items | Where-Object name -like '*Watching*').Count -gt 0 }
            if (@(Read-Ui | Where-Object type -eq 'ListItem').Count) { throw 'Restart reused the stopped session history instead of capturing a fresh baseline.' }
            $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $parent.DeleteSubKeyTree($watchName, $false) } finally { $parent.Dispose() }
            Wait-Ui { param($items) $items.name -contains 'Watch stopped unexpectedly' -and $items.name -contains 'Open watch' }
            Ui @('invoke', (Pick 'TabItem' $watchName).selector, '--action', 'select') | Out-Null
            Go-ToKey $address
            Press 'Open watch'
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ComboBox' -and $_.name -eq 'Choose capture' }).Count -eq 1 -and @($items | Where-Object name -like "*${watchName}*Stopped*").Count -gt 0 }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'watch-pause-restart-failure.png')) | Out-Null
            Press 'Remove stopped watch'
            Open-Menu 'Close tab'; Browser
        } finally {
            $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $parent.DeleteSubKeyTree($watchName, $false) } finally { $parent.Dispose() }
        }
    }

    Test-Ui 'Bottom panel shares search sets tools and live list spacing' {
        Browser
        Fill 'Search registry' 'needle'; Press 'Start search'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.name -contains 'Find: needle' }
        $first = Pick 'TabItem' 'Find: needle'
        $list = Pick 'List' 'Investigation results'
        $strip = @(Read-Ui | Where-Object automationId -eq 'BottomTabs')[0]
        if ($list.y - ($strip.y + $strip.height) -gt 32) { throw 'An extra toolbar row is consuming bottom panel space.' }
        $hide = Pick 'Button' 'Hide bottom panel'
        if ([Math]::Abs(($strip.x + $strip.width) - ($hide.x + $hide.width)) -gt 24) { throw 'Panel controls are not anchored to the right edge.' }
        $filter = @(Read-Ui | Where-Object automationId -eq 'SearchResultFilter')[0]
        if (-not $filter -or [Math]::Abs(($filter.y + $filter.height / 2) - ($hide.y + $hide.height / 2)) -gt 6) { throw 'Result filter is not on the panel toolbar.' }
        Fill 'Filter search results' 'NeedleTwo'
        Wait-Ui { param($items) @(Elements (Pick 'List' 'Investigation results').children | Where-Object type -eq 'ListItem').Count -eq 1 }
        $filteredList = Pick 'List' 'Investigation results'
        if (@(Elements $filteredList.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleTwo*' }).Count -ne 1) { throw 'Toolbar filter did not narrow existing results.' }
        Fill 'Filter search results' 'No matching displayed row'
        Wait-Ui { param($items) $items.name -contains 'No results match the filter' }
        Fill 'Filter search results' ''
        Wait-Ui { param($items) @(Elements (Pick 'List' 'Investigation results').children | Where-Object type -eq 'ListItem').Count -eq 2 }
        $list = Pick 'List' 'Investigation results'
        $matches = @(Elements $list.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleOne*' })
        if ($matches.Count -ne 1) { throw 'The bottom search did not publish its match.' }
        Ui @('invoke', $matches[0].selector, '--action', 'select') | Out-Null
        $open = Pick 'Button' 'Open selected result'
        if (-not $open.isEnabled) { throw 'The primary result action is unavailable.' }
        $header = @(Read-Ui | Where-Object automationId -eq 'ToolColumn1')[0]
        Ui @('send-keys', 'right', '--via', 'send-input', '--target', 'ToolColumnResize0') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object automationId -eq 'ToolColumn1')[0].x -gt $header.x + 15 }
        $columns = @(Read-Ui | Where-Object automationId -eq 'ToolColumn1')[0]
        $row = @(Elements (Pick 'List' 'Investigation results').children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleOne*' })[0]
        $pathCell = @(Elements $row.children | Where-Object { $_.type -eq 'Text' -and $_.name -eq $address })[0]
        if (-not $pathCell -or [Math]::Abs($pathCell.x - $columns.x) -gt 3) { throw 'Resizing misaligned a realized result row.' }
        $menu = Context-Menu $row.selector -Keyboard
        foreach ($action in 'Open result','Open in new tab','Show details','Inspect value','Copy path','Copy results','Select all') {
            if ($menu.Items.name -notcontains $action) { throw "Missing search context action: $action" }
        }
        Close-ContextMenu $menu
        $secondRow = @(Elements (Pick 'List' 'Investigation results').children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleTwo*' })[0]
        $menu = Context-Menu $secondRow.selector
        $copy = @($menu.Items | Where-Object name -eq 'Copy results')[0]
        Ui @('invoke', $copy.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        if ((Get-Clipboard -Raw) -notlike '*NeedleTwo*' -or (Get-Clipboard -Raw) -like '*NeedleOne*') { throw 'The context menu copied a previously selected result.' }
        Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
        Ui @('hover', 'BottomPanelResize') | Out-Null
        if (-not [UxTestWindow]::HasVerticalResizeCursor()) { throw 'The bottom splitter did not show the vertical resize cursor.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'bottom-panel-toolbar.png')) | Out-Null
        Fill 'Filter search results' 'NeedleOne'
        Fill 'Search registry' 'NeedleTwo'; Press 'Start search'
        Wait-Ui { param($items) $items.name -contains 'Find: NeedleTwo' -and $items.name -contains 'Find: needle' }
        Open-Menu 'Change journal' -Group 'Tools'
        Open-Menu 'Watches' -Group 'Tools'
        Open-Menu 'Change journal' -Group 'Tools'
        $bottom = @(Read-Ui | Where-Object automationId -eq 'BottomTabs')[0]
        $tabs = @(Elements $bottom.children | Where-Object type -eq 'TabItem')
        if ($tabs.Count -ne 4 -or @($tabs | Where-Object name -eq 'Change journal').Count -ne 1) { throw 'Bottom tabs did not retain searches and singleton tools.' }
        Ui @('invoke', $first.selector, '--action', 'select') | Out-Null
        $retained = @(Elements (Pick 'List' 'Investigation results').children | Where-Object type -eq 'ListItem')
        if ($retained.Count -ne 1 -or $retained[0].name -notlike '*NeedleOne*') { throw 'Changing panel tabs lost the result filter.' }
        Press 'Hide bottom panel'
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' }
        Tool-Option 'Right sidebar'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -notcontains 'BottomTabs' }
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Local note' }
        Press 'Close tool panel'
        Open-Menu 'Bottom panel' -Group 'Tools'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        Tool-Option 'Right sidebar'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -contains 'BottomTabs' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'bottom-and-side-panels.png')) | Out-Null
        Press 'Close tool panel'
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        Fill 'Filter search results' ''
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and $items.automationId -contains 'EntriesList' }
        Open-Menu 'Settings'
        Ui @('scroll-into-view', 'ListSpacingSlider') | Out-Null
        Ui @('set-value', 'ListSpacingSlider', '10') | Out-Null
        Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ListRowPadding -eq 10 }
        $list = Pick 'List' 'Investigation results'
        $comfortable = @(Elements $list.children | Where-Object type -eq 'ListItem')[0].height
        Ui @('set-value', 'ListSpacingSlider', '0') | Out-Null
        Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ListRowPadding -eq 0 }
        $list = Pick 'List' 'Investigation results'
        $compact = @(Elements $list.children | Where-Object type -eq 'ListItem')[0].height
        if ($comfortable -le $compact) { throw "Live list spacing did not update: comfortable $comfortable, compact $compact." }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'bottom-panel-compact.png')) | Out-Null
        $tab = Pick 'TabItem' 'Settings'
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        Ui @('set-value', 'ListSpacingSlider', '3') | Out-Null
        Browser
        Go-ToKey 'HKEY_LOCAL_MACHINE'
        Fill 'Search registry' ([guid]::NewGuid().ToString('N')); Press 'Start search'
        Wait-Ui { param($items) $items.automationId -contains 'SearchProgress' }
        $progress = @(Read-Ui | Where-Object automationId -eq 'SearchProgress')[0]
        if ($progress.height -gt 3) { throw 'Search progress consumed extra vertical space.' }
        $busyList = Pick 'List' 'Investigation results'
        Press 'Cancel search' -CommandBarId 'ItemCommands'
        Wait-Ui { param($items) $items.automationId -notcontains 'SearchProgress' }
        $idleList = Pick 'List' 'Investigation results'
        if ([Math]::Abs($busyList.y - $idleList.y) -gt 1 -or [Math]::Abs($busyList.height - $idleList.height) -gt 1) { throw 'Search progress moved the list.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'bottom-panel-search-canceled.png')) | Out-Null
        Go-ToKey $address
        $bottom = @(Read-Ui | Where-Object automationId -eq 'BottomTabs')[0]
        foreach ($name in @(Elements $bottom.children | Where-Object type -eq 'TabItem' | ForEach-Object name)) {
            Ui @('invoke', (Pick 'TabItem' $name).selector, '--action', 'select') | Out-Null
            Ui @('send-keys', 'ctrl+w', '--via', 'send-input', '--target', 'BottomPanelResize') | Out-Null
            Wait-Ui { param($items) $items.name -notcontains $name }
        }
        Wait-Ui { param($items) $items.automationId -notcontains 'BottomTabs' -and $items.automationId -contains 'EntriesList' }
    }

    Test-Ui 'Bottom tool lists offer context menus and resizable columns' {
        $folder = Join-Path $dataFolder 'Journal'
        $null = [IO.Directory]::CreateDirectory($folder)
        $entry = @{ Id = [guid]::NewGuid(); Time = [DateTimeOffset]::Now.ToString('o'); Operation = 'Panel activity'
            Before = @(); After = @(); Activity = 'Panel activity'; ActivityLocation = @{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = $subKey } }
        [IO.File]::WriteAllText((Join-Path $folder ($entry.Id.ToString('N') + '.json')), ($entry | ConvertTo-Json -Depth 6))
        Open-Menu 'Change journal' -Group 'Tools'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Panel activity*' }).Count -eq 1 }
        $row = Select-Row 'Panel activity'
        $menu = Context-Menu $row.selector -Keyboard
        foreach ($action in 'Show details','Open key','Copy path','Copy PowerShell','Select all') {
            if ($menu.Items.name -notcontains $action) { throw "Missing journal context action: $action" }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'journal-context-menu.png')) -Window $mainWindow | Out-Null
        Close-ContextMenu $menu
        $before = @(Read-Ui | Where-Object automationId -eq 'ToolColumn2')[0]
        Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'ToolColumnResize1') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object automationId -eq 'ToolColumn2')[0].x -lt $before.x - 15 }
        Browser; Press 'Watch this branch' -CommandBarId 'Commands' -CommandBarChildId 'PermissionsButton'; Start-WatchPreview $address
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Context' -and $_.name -like 'Watching*' }).Count -eq 1 }
        Press 'Show in panel'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.SetValue('Counter', 42, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
        Wait-Ui { param($items) @(Elements @($items | Where-Object { $_.type -eq 'List' -and $_.name -eq 'Tool items' })[0].children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' }).Count -eq 1 }
        $row = @(Elements (Pick 'List' 'Tool items').children | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' })[0]
        Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
        $menu = Context-Menu $row.selector
        foreach ($action in 'Show details','Open key','Copy path','Copy PowerShell','Select all') {
            if ($menu.Items.name -notcontains $action) { throw "Missing watch context action: $action" }
        }
        $copy = @($menu.Items | Where-Object name -eq 'Copy PowerShell')[0]
        Ui @('invoke', $copy.selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        if ((Get-Clipboard -Raw) -notlike '*Counter*') { throw 'Watch context menu did not copy the selected observation script.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'watch-panel.png')) | Out-Null
    }

    Test-Ui 'Bottom search virtualizes large result sets and recycles resized columns' {
        $name = 'VirtualizationFixture'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\$name")
        try { foreach ($number in 0..1999) { $key.SetValue(('VirtualResultNeedle{0:D4}' -f $number), "Payload $number") } } finally { $key.Dispose() }
        Browser; Go-ToKey "$address\$name"
        Fill 'Search registry' 'VirtualResultNeedle'; Press 'Start search'
        Wait-Ui { param($items) $items.name -contains 'Find: VirtualResultNeedle (2,000)' -and $items.automationId -notcontains 'SearchProgress' }
        $rootElement = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $listElement = $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'Results'))
        $peers = $listElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
        $virtual = 0; $realized = 0
        foreach ($peer in $peers) {
            $pattern = $null
            if ($peer.TryGetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern, [ref]$pattern)) { $virtual++ }
            if ($peer.Current.BoundingRectangle.Width -gt 0 -and $peer.Current.BoundingRectangle.Height -gt 0) { $realized++ }
        }
        @{ results = 2000; accessibleItems = $peers.Count; itemsSupportingVirtualization = $virtual; itemsWithBounds = $realized } | ConvertTo-Json |
            Set-Content (Join-Path $ArtifactDirectory 'search-virtualization.json')
        if ($realized -gt 100 -or $realized -lt 1) { throw "Unexpected realized result count: $realized of 2000." }
        Ui @('send-keys', 'right', '--via', 'send-input', '--target', 'ToolColumnResize0') | Out-Null
        $scroll = $listElement.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $scroll.SetScrollPercent(-1, 100)
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'VirtualResultNeedle1999,*' }).Count -eq 1 }
        $last = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'VirtualResultNeedle1999,*' })[0]
        $pathCell = @(Elements $last.children | Where-Object { $_.type -eq 'Text' -and $_.name -eq "$address\$name" })[0]
        $header = @(Read-Ui | Where-Object automationId -eq 'ToolColumn1')[0]
        if (-not $pathCell -or [Math]::Abs($pathCell.x - $header.x) -gt 3) { throw 'A recycled row lost the resized column width.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'virtualized-search-last-row.png')) | Out-Null
    }

    Test-Ui 'Investigation status survives switching and docking then clears on close' {
        Browser
        Fill 'Search registry' 'notification lifetime'
        Start-SearchInMainTab
        Wait-Ui { param($items) $items.name -contains 'Find: notification lifetime' -and @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Run search' -and $_.isEnabled }).Count -eq 1 }
        $settingsLock = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            Press 'Save search'; Fill 'Search name' 'Blocked notification fixture'; Primary
            Wait-Ui { param($items) $items.name -contains 'Could not save search' }
        } finally { $settingsLock.Dispose() }
        Browser
        if ((Read-Ui).name -notcontains 'Could not save search') { throw 'Switching tabs dismissed the investigation status.' }
        Ui @('invoke', (Pick 'TabItem' 'Find: notification lifetime').selector, '--action', 'select') | Out-Null
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        if ((Read-Ui).name -notcontains 'Could not save search') { throw 'Docking dismissed the investigation status.' }
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.name -contains 'Find: notification lifetime' -and $items.automationId -contains 'ShowInPanelButton' }
        if ((Read-Ui).name -notcontains 'Could not save search') { throw 'Undocking dismissed the investigation status.' }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.name -notcontains 'Could not save search' }
        Browser
    }

    Test-Ui 'Advanced search access keys reach fields and keep focus on validation' {
        function Assert-SearchFocus([string] $Id) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
                $actual = $focused.Current.AutomationId
                while ($focused) {
                    if ($focused.Current.AutomationId -eq $Id) { return }
                    $focused = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)
                }
                Start-Sleep -Milliseconds 50
            } while ($timer.Elapsed.TotalSeconds -lt 3)
            throw "Expected focus in '$Id' but found '$actual'."
        }

        function Focus-SearchField([string] $Key, [string] $Id) {
            Ui @('send-keys', "alt+$Key", '--via', 'send-input') | Out-Null
            Assert-SearchFocus $Id
        }

        Browser
        Set-TestWindowForeground
        Press 'Advanced search'
        Wait-Ui { param($items) $items.automationId -contains 'QueryInput' }
        Assert-SearchFocus 'QueryInput'
        Ui @('send-keys', 'tab', '--via', 'send-input') | Out-Null
        Assert-SearchFocus 'SearchScope'
        Ui @('send-keys', 'shift+tab', '--via', 'send-input') | Out-Null
        Assert-SearchFocus 'QueryInput'

        Ui @('send-keys', 'vk=0x12', '--via', 'send-input') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'advanced-search-keytips.png')) | Out-Null
        Ui @('send-keys', 'vk=0x41', '--via', 'send-input') | Out-Null
        Assert-SearchFocus 'SearchActivity'
        Ui @('send-keys', 'space home down down enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'SearchInspectionHint' }
        Focus-SearchField 'i' 'SearchActivityWithin'

        Focus-SearchField 'm' 'SearchKeyModified'
        Ui @('send-keys', 'space end enter', '--via', 'send-input') | Out-Null
        Wait-Ui {
            param($items)
            $items.name -contains 'Key last modified from' -and $items.name -contains 'Key last modified through'
        }
        Focus-SearchField 'r' 'SearchModifiedFrom'
        Focus-SearchField 't' 'SearchModifiedThrough'
        Focus-SearchField 's' 'SearchScope'
        Focus-SearchField 'w' 'QueryInput'
        Fill 'Find what' 'needle'

        Ui @('send-keys', 'alt+c', '--via', 'send-input') | Out-Null
        $case = Ui @('get-property', 'SearchMatchCase', '--property', 'ToggleState', '--json') | ConvertFrom-Json
        if ([string]$case.properties.ToggleState -notmatch '^(?:ToggleState_)?On$|^1$') { throw 'Alt+C did not check Match case.' }

        Focus-SearchField 'v' 'SearchValueTypes'
        Fill 'Value types (blank for all)' 'not-a-type'
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -eq 'DialogError' -and $_.name -like 'Unknown value type*' }).Count -eq 1
        }
        Assert-SearchFocus 'SearchValueTypes'
        Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'QueryInput' }
    }

    Test-Ui 'Find-all keeps matches while opening a result and saves its query' {
        Browser
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not resize the fixture window.' }
        Set-TestWindowForeground
        Press 'Advanced search'
        Wait-Ui {
            param($items)
            if ($items.automationId -notcontains 'DialogHeading') { return $false }
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            $focused.hasFocus -and $focused.element.automationId -eq 'QueryInput'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'find-dialog.png')) | Out-Null
        Fill 'Find what' 'needle'; Fill 'Value types (blank for all)' 'not-a-type'; Primary
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' -and $items.automationId -contains 'DialogError' }
        Fill 'Value types (blank for all)' 'REG_SZ'
        $types = Pick 'Edit' 'Value types (blank for all)'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $types.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        Press 'Open panel tab in main tab'
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' -and @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Find: needle' }).Count -eq 1 -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 }
        $items = Read-Ui
        $tools = @($items | Where-Object automationId -eq 'ShowInPanelButton')[0]
        $menu = @($items | Where-Object automationId -eq 'GlobalMenuButton')[0]
        if (-not $tools -or -not $menu -or $items.automationId -contains 'ToolContextButton') { throw 'Tool header lost global actions or retained the context toolbar.' }
        if ([Math]::Abs($menu.y - $tools.y) -gt 2 -or $menu.x -lt $tools.x + $tools.width) { throw 'Application menu is not beside the tool header action.' }
        if ($items.automationId -contains 'BeforeText' -or $items.automationId -contains 'AfterText') { throw 'Empty details are visible before selecting a result.' }
        if (-not (Pick 'Button' 'Run search').isEnabled -or (Pick 'Button' 'Open selected result').isEnabled) { throw 'Search commands do not reflect completion and selection.' }
        $result = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*NeedleTwo*' })[0]
        $tab = Middle-Open $result $fixtureName
        if ((Read-Ui).automationId -contains 'EntriesList' -or (Pick 'Button' 'Open selected result').isEnabled) { throw 'Middle-click switched tabs or selected a search result.' }
        Close-MiddleTab $tab
        Ui @('invoke', (Pick 'TabItem' 'Find: needle').selector, '--action', 'select') | Out-Null
        Select-Row 'NeedleOne' | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'BeforeText' -and $items.automationId -contains 'AfterText' }
        Press 'Open selected result'
        $tab = Pick 'TabItem' 'Find: needle'
        Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
        if (@(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -ne 2) { throw 'Results disappeared after navigating.' }
        Press 'Save search'; Fill 'Search name' 'UI fixture search'; Primary
        Wait-Ui { param($items) $items.name -contains 'Search saved' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'search.png')) | Out-Null
    }

    Test-Ui 'Search results reuse owned destinations and keep explicit foreground and background opens' {
        $navigationName = 'SearchNavigationFixture'
        $navigationPath = "$subKey\$navigationName"
        $query = 'NavigationNeedle_' + [guid]::NewGuid().ToString('N')
        $firstKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$navigationPath\NavFirst")
        try { $firstKey.SetValue('', $query) } finally { $firstKey.Dispose() }
        $secondKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$navigationPath\NavSecond")
        try { $secondKey.SetValue('Named result', $query) } finally { $secondKey.Dispose() }
        try {
            Browser; Go-ToKey "$address\$navigationName"
            Fill 'Search registry' $query; Start-SearchInMainTab
            Wait-Ui { param($items) @($items | Where-Object type -eq 'ListItem').Count -eq 2 }
            $sourceName = 'Find: ' + $query
            $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
            $result = Select-Row 'NavFirst'
            Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $result.selector) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq $count } -All
            $default = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '(Default),*' })[0]
            $selected = Ui @('get-property', $default.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
            if ($selected.properties.IsSelected -ne 'True') { throw 'Ordinary opening lost the unnamed value selection.' }
            Ui @('invoke', (Pick 'TabItem' $sourceName).selector, '--action', 'select') | Out-Null
            $result = Select-Row 'NavSecond'
            Ui @('click', $result.selector, '--double') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'NavSecond' }).Count -eq 1 }
            if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $count) { throw 'Double-click created another ordinary destination.' }
            $named = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Named result,*' })[0]
            $selected = Ui @('get-property', $named.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
            if ($selected.properties.IsSelected -ne 'True') { throw 'Reusing the destination lost its named value selection.' }
            Go-ToKey $address
            Ui @('invoke', (Pick 'TabItem' $sourceName).selector, '--action', 'select') | Out-Null
            Select-Row 'NavFirst' | Out-Null; Press 'Open selected result'
            Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq $count + 1 } -All
            Ui @('invoke', (Pick 'TabItem' $sourceName).selector, '--action', 'select') | Out-Null
            Select-Row 'NavSecond' | Out-Null; Press 'Open in new tab'
            Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq $count + 2 } -All
            Open-Menu 'Close tab'
            Ui @('invoke', (Pick 'TabItem' $sourceName).selector, '--action', 'select') | Out-Null
            $middle = Middle-Open (Select-Row 'NavSecond') 'NavSecond'
            if ((Read-Ui).automationId -contains 'EntriesList') { throw 'Middle-click activated its background result.' }
            Close-MiddleTab $middle
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'search-destination-owned.png')) | Out-Null
            Open-Menu 'Close tab'
            Ui @('invoke', (Pick 'TabItem' 'NavFirst').selector, '--action', 'select') | Out-Null
            Open-Menu 'Close tab'
            Browser
            Go-ToKey $address
        } finally {
            $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $parent.DeleteSubKeyTree($navigationName, $false) } finally { $parent.Dispose() }
        }
    }

    Test-Ui 'Visible Computer search cancels its real scan and closed rerun cannot replace the browser' {
        Browser; Go-ToKey 'Computer'
        $query = 'NoMatch_' + [guid]::NewGuid().ToString('N')
        Fill 'Search registry' $query; Start-SearchInMainTab
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel search' -and $_.isEnabled }).Count -eq 1 }
        Press 'Cancel search'
        Wait-Ui { param($items) $items.name -contains 'Search canceled' -and @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Run search' -and $_.isEnabled }).Count -eq 1 }
        if (@(Read-Ui | Where-Object type -eq 'ListItem').Count) { throw 'Canceled no-match scan published late rows.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'search-real-canceled.png')) | Out-Null
        Press 'Run search'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel search' -and $_.isEnabled }).Count -eq 1 }
        Open-Menu 'Close tab'
        Ui @('invoke', (Pick 'TabItem' 'Computer').selector, '--action', 'select') | Out-Null
        Go-ToKey $address
        if ((Read-Ui -All).name -contains ('Find: ' + $query)) { throw 'Closing the live results tab left its scan tab installed.' }
    }

    Test-Ui 'Search with no matches shows an empty state without blank details' {
        Browser
        Fill 'Search registry' ($fixtureName + '.no-match')
        Start-SearchInMainTab
        Wait-Ui { param($items) $items.name -contains 'No matches found' }
        $items = Read-Ui
        if ($items.automationId -contains 'BeforeText' -or $items.automationId -contains 'AfterText' -or $items.automationId -contains 'Progress') { throw 'Finished empty search still shows progress or blank details.' }
        if (-not (Pick 'Button' 'Run search').isEnabled -or (Pick 'Button' 'Open selected result').isEnabled) { throw 'Empty search leaves unavailable commands enabled.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'search-empty.png')) | Out-Null
    }

    Test-Ui 'Saved searches and workspaces open repeatedly without a search filter' {
        foreach ($repeat in 1..2) {
            Browser
            Open-Menu 'Saved searches' -Group 'Tools'
            Wait-Ui { param($items) $items.name -contains 'No saved searches' }
            if ($appProcess.HasExited) { throw 'Opening Saved searches terminated the editor.' }
            if ((Read-Ui).automationId -contains 'SearchResultFilter') { throw 'Saved searches contains the results-only filter.' }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory "saved-searches-empty-$repeat.png")) | Out-Null
            Browser
            Open-Menu 'Workspaces' -Group 'Tools'
            Wait-Ui { param($items) $items.name -contains 'No saved workspaces' }
            if ($appProcess.HasExited) { throw 'Opening Workspaces terminated the editor.' }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory "workspaces-empty-$repeat.png")) | Out-Null
        }
        Browser
        Fill 'Search registry' 'needle'; Start-SearchInMainTab
        Wait-Ui { param($items) @(Elements (Pick 'List' 'Investigation results').children | Where-Object type -eq 'ListItem').Count -eq 2 }
        Press 'Save search'; Fill 'Search name' 'Crash regression query'; Primary
        Wait-Ui { param($items) @((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Searches | Where-Object Name -eq 'Crash regression query').Count -eq 1 }
        Open-Menu 'Saved searches' -Group 'Tools'
        Wait-Ui { param($items) $items.name -contains 'Crash regression query' }
        Select-Row 'Crash regression query' | Out-Null
        Open-Menu 'Saved searches' -Group 'Tools'
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Saved searches' }).Count -ne 1) { throw 'Reopening Saved searches duplicated its tab.' }
        if (-not (Pick 'Button' 'Run search').isEnabled -or -not (Pick 'Button' 'Remove selected').isEnabled) { throw 'Saved search actions lost the selected query.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'saved-searches-populated.png')) | Out-Null
        Press 'Run search'
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' -and @(Elements (Pick 'List' 'Investigation results').children | Where-Object type -eq 'ListItem').Count -eq 2 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'saved-search-executed.png')) | Out-Null
        Press 'Hide bottom panel'
        Open-Menu 'Saved searches' -Group 'Tools'
        Select-Row 'Crash regression query' | Out-Null
        Press 'Remove selected'
        Wait-Ui { param($items) $items.name -contains 'No saved searches' }
        if (@((Get-Content $settingsFile -Raw | ConvertFrom-Json).Searches | Where-Object Name -eq 'Crash regression query').Count) { throw 'Removing the saved query did not persist.' }
    }

    Test-Ui 'Saved searches keep selection actions separate and run the saved query' {
        Browser
        Fill 'Search registry' 'needle'
        Start-SearchInMainTab
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 }
        Press 'Save search'; Fill 'Search name' 'Selection fixture search'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', (Pick 'Edit' 'Search name').selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Search saved' }
        Open-Menu 'Saved searches' -Group 'Tools'
        Wait-Ui { param($items) $items.name -contains 'Selection fixture search' }
        if ((Pick 'Button' 'Run search').isEnabled -or (Pick 'Button' 'Remove selected').isEnabled) { throw 'Saved searches enabled selection commands without a selection.' }
        Select-Row 'Selection fixture search' | Out-Null
        Browser
        Open-Menu 'Saved searches' -Group 'Tools'
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Saved searches' }).Count -ne 1) { throw 'Reopening Saved searches duplicated its collection tab.' }
        if (-not (Pick 'Button' 'Run search').isEnabled -or (Read-Details) -notlike '*needle*') { throw 'Selecting a saved search did not expose its query and run action.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'saved-searches.png')) | Out-Null
        Tool-Option 'Right sidebar'
        Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'ToolSelector') | Out-Null
        if ((Read-Ui).name -contains 'Pin tool panel') { Press 'Pin tool panel' }
        Wait-Ui { param($items) $items.automationId -contains 'AfterText' }
        $items = Read-Ui
        if (($items | Where-Object automationId -eq 'AfterText').y -le ($items | Where-Object automationId -eq 'Count').y) { throw 'Pinned panel did not reflow the adjacent tool inspector.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'saved-searches-pinned.png')) | Out-Null
        Press 'Unpin tool panel'; Press 'Close tool panel'
        Press 'Run search'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 }
        Open-Menu 'Close tab'
        Wait-Ui { param($items) $items.name -contains 'Selection fixture search' }
        Press 'Remove selected'
        Wait-Ui { param($items) $items.name -notcontains 'Selection fixture search' }
        if (@((Get-Content $settingsFile -Raw | ConvertFrom-Json).Searches | Where-Object Name -eq 'Selection fixture search').Count) {
            throw 'Removing the selected saved search did not persist.'
        }
        Open-Menu 'Close tab'
    }

    Test-Ui 'Registry file Problems lists and navigates all errors without a syntax InfoBar' {
        Browser
        [UxTestWindow]::SetWindowPos($script:mainWindow, 0, 100, 100, 1400, 900, 0x14) | Out-Null
        $file = Join-Path $ArtifactDirectory 'problems.reg'
        [IO.File]::WriteAllLines($file, @('Windows Registry Editor Version 5.00', '',
            '[HKEY_CURRENT_USER\Software\Contoso\Agent]', '"First"=unknown', '"Second"=dword:invalid'), [Text.Encoding]::Unicode)
        Open-Menu 'Import registry file…'
        File-Picker 'Open' $file
        $originalWindow = $script:mainWindow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
            $preview = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.title -like '*problems.reg*' })
            if ($preview.Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 15)
        if ($preview.Count -ne 1) { throw 'The registry file Problems preview did not open.' }
        $previewWindow = [long]$preview[0].hwnd
        try {
            $script:mainWindow = $previewWindow
            [UxTestWindow]::TargetWindow = $previewWindow
            Wait-Ui { param($items) $items.automationId -contains 'RegFileProblemsList' -and $items.name -contains '2 errors' }
            $ownerBounds = @((Read-Ui -Window $originalWindow) | Where-Object type -eq 'Window')[0]
            $previewBounds = @((Read-Ui) | Where-Object type -eq 'Window')[0]
            if ([Math]::Abs(($previewBounds.x + $previewBounds.width / 2) - ($ownerBounds.x + $ownerBounds.width / 2)) -gt 4 -or
                [Math]::Abs(($previewBounds.y + $previewBounds.height / 2) - ($ownerBounds.y + $ownerBounds.height / 2)) -gt 4) {
                throw 'The registry file preview did not open centered over its owner.'
            }
            Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'False', '-t', '3000') | Out-Null
            $items = @(Read-Ui)
            $values = @($items | Where-Object automationId -eq 'RegFileValues')[0]
            foreach ($column in 'name', 'operation', 'type', 'data', 'line') {
                $header = @($items | Where-Object automationId -eq ($column + 'Header'))[0]
                if (-not $header -or $header.x -lt $values.x -or $header.x + $header.width -gt $values.x + $values.width + 1) {
                    throw "The $column column does not fit the default split preview."
                }
            }
            [UxTestWindow]::SetWindowPos($previewWindow, 0, 0, 0, 720, 520, 0x16) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'nameHeader' }
            [UxTestWindow]::SetWindowPos($previewWindow, 0, 0, 0, 1200, 780, 0x16) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'lineHeader' }
            if ((Read-Ui).automationId -contains 'RegFileNotice') { throw 'Syntax errors still occupy the operation InfoBar.' }
            Ui @('hover', 'RegFileTitleBar') | Out-Null
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-problems.png')) | Out-Null
            Ui @('screenshot', 'RegFileSource', '--output', (Join-Path $ArtifactDirectory 'registry-file-problem-markers.png')) | Out-Null
            Ui @('invoke', 'RegFileProblems', '--action', 'collapse') | Out-Null
            Ui @('invoke', 'RegFileLayoutPreview', '--action', 'select') | Out-Null
            Wait-Ui { param($items) $items.automationId -notcontains 'RegFileSource' }
            Ui @('send-keys', 'alt+d', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'RegFileSource' -and $items.automationId -contains 'RegFileProblemsList' }
            $problem = @((Read-Ui) | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Line 5:*' })[0]
            if (-not $problem) { throw 'The second parser error is missing from the Problems list.' }
            Ui @('invoke', $problem.selector, '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Ln 5, Col 1' }
            $source = @((Read-Ui) | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })[0]
            Ui @('invoke', 'RegFileProblems', '--action', 'collapse') | Out-Null
            Ui @('send-keys', 'ctrl+end', '--via', 'send-input', '--target', $source.selector) | Out-Null
            Ui @('send-keys', '; typing remains possible', '--verbatim', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) $items.name -contains '2 errors' -and $items.name -contains 'Ln 6, Col 26' }
            if ((Read-Ui).automationId -contains 'RegFileProblemsList') { throw 'Typing reopened the collapsed Problems panel.' }
            Ui @('send-keys', 'ctrl+z', '--via', 'send-input', '--target', $source.selector) | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Ln 6, Col 1' }
        } finally {
            [UxTestWindow]::CloseWindow($previewWindow)
            $script:mainWindow = $originalWindow
            [UxTestWindow]::TargetWindow = $originalWindow
        }
    }

    Test-Ui '32-bit address and tab identify the import target with the status bar hidden' {
        function Set-PreviewLayout([string] $Mode) {
            Ui @('invoke', ('RegFileLayout' + $Mode), '--action', 'select') | Out-Null
            Wait-Ui { param($items)
                $hasEditor = $items.automationId -contains 'RegFileSource'
                $hasPreview = $items.automationId -contains 'RegFileKeys'
                $hasEditor -eq ($Mode -ne 'Preview') -and $hasPreview -eq ($Mode -ne 'Editor')
            }
        }
        function Read-SourceCharacterMetrics {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindow).FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'WinUIEditor.EditorBaseControl'))
            $range = $element.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange
            $range.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, $range, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
            [void]$range.MoveEndpointByUnit([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End, [System.Windows.Automation.Text.TextUnit]::Character, 1)
            $rectangles = @($range.GetBoundingRectangles())
            if ($rectangles.Count -ne 1 -or $rectangles[0].Height -le 0) { throw 'The source character has no measurable font height.' }
            @{
                Height = $rectangles[0].Height
                Inset = $rectangles[0].X - $element.Current.BoundingRectangle.X
            }
        }
        function Read-SourceCharacterHeight { (Read-SourceCharacterMetrics).Height }
        function Set-EditorFontFamily([string] $Name) {
            Ui @('scroll-into-view', 'EditorFontSelector') | Out-Null
            $family = Pick 'ComboBox' 'Editor font family'
            Set-TestWindowForeground
            [UxTestWindow]::LeftClickAt([int]($family.x + 20), [int]($family.y + $family.height / 2))
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
            Ui @('send-keys', $Name, '--verbatim', '--via', 'send-input') | Out-Null
            Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        }
        function Step-EditorFontSize([string] $Button) {
            $size = @(Read-Ui -All | Where-Object automationId -eq 'EditorFontSizeBox')[0]
            $spin = @(Elements $size.children | Where-Object automationId -eq $Button)[0]
            foreach ($step in 1..3) { Ui @('invoke', $spin.selector, '--action', 'invoke') | Out-Null }
        }
        function Assert-RegistrySyntax([string] $Theme, [int[]] $Colors) {
            $path = Join-Path $ArtifactDirectory ('registry-file-syntax-' + $Theme + '.png')
            Ui @('screenshot', 'RegFileSource', '--output', $path) | Out-Null
            Add-Type -AssemblyName System.Drawing
            $bitmap = [Drawing.Bitmap]::new($path)
            $missing = [Collections.Generic.HashSet[int]]::new($Colors)
            try {
                :scan for ($y = 0; $y -lt $bitmap.Height; $y++) {
                    for ($x = 0; $x -lt $bitmap.Width; $x++) {
                        [void]$missing.Remove($bitmap.GetPixel($x, $y).ToArgb() -band 0xFFFFFF)
                        if ($missing.Count -eq 0) { break scan }
                    }
                }
                if ($missing.Count) { throw "Missing $Theme registry syntax colors: $($missing -join ', ')." }
            } finally { $bitmap.Dispose() }
        }
        function Switch-RegistryView([string] $label) {
            Press 'View'
            $popup = Read-ContextMenu 'Registry view'
            $sub = @($popup.Items | Where-Object name -eq 'Registry view')[0]
            Ui @('invoke', $sub.selector, '--action', 'expand') -Window $popup.Window | Out-Null
            $popup = Read-ContextMenu $label
            $item = @($popup.Items | Where-Object name -eq $label)[0]
            Ui @('invoke', $item.selector) -Window $popup.Window | Out-Null
        }
        Browser
        $hadStatus = (Get-Content $settingsFile -Raw | ConvertFrom-Json).ShowStatusBar ?? $true
        if ($hadStatus) {
            Open-Menu 'Settings'
            Ui @('invoke', 'StatusBarSwitch', '--action', 'toggle-off') | Out-Null
            Browser
        }
        Switch-RegistryView '32-bit registry'
        Wait-Ui { param($items) $items.name -contains ($fixtureName + ' · 32-bit') -and
            $items.name -contains '32-bit registry view' -and $items.automationId -notcontains 'StatusBar' }
        $classKey = 'Software\Classes\CLSID\' + [guid]::NewGuid().ToString('B')
        $importFile = Join-Path $ArtifactDirectory 'view-target.reg'
        $lines = @('Windows Registry Editor Version 5.00', '; Syntax highlighting fixture',
            ';Title=Registry preview sample', ';Description=Preview changes before importing.', ';Author=UI fixture',
            ';Requires=ExplorerRestart', '', "[HKEY_CURRENT_USER\$classKey]", '"RegeditViewFixture"="32"',
            '"Enabled"=dword:00000001', '"Payload"=hex:01,ab,ff',
            "[HKEY_CURRENT_USER\$classKey\Removed]", '@="discard"', "[-HKEY_CURRENT_USER\$classKey\Removed]",
            "[HKEY_CURRENT_USER\$classKey\Reset]", '"Old"="discard"', "[-HKEY_CURRENT_USER\$classKey\Reset]",
            "[HKEY_CURRENT_USER\$classKey\Reset]", '"After"="retained"', '')
        $editedSource = (($lines -join [Environment]::NewLine).Replace('"32"', '"unsaved"').
            Replace(';Title=Registry preview sample', ';Title=Edited registry preview').
            Replace(';Requires=ExplorerRestart', ';Requires=Logoff'))
        [IO.File]::WriteAllLines($importFile, $lines, [Text.Encoding]::Unicode)
        $user32 = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry32')
        $user64 = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
        try {
            Open-Menu 'Import registry file…'
            File-Picker 'Open' $importFile
            $originalWindow = $script:mainWindow
            try {
                $timer = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
                    $preview = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.title -like '*view-target.reg*' })
                    if ($preview.Count -eq 1) { break }
                    Start-Sleep -Milliseconds 100
                } while ($timer.Elapsed.TotalSeconds -lt 15)
                if ($preview.Count -ne 1) { throw 'The separate registry file preview did not appear.' }
                $script:mainWindow = [long]$preview[0].hwnd
                [UxTestWindow]::TargetWindow = $script:mainWindow
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                if (-not [UxTestWindow]::SetWindowPos($script:mainWindow, 0, 0, 0, 1200, 780, 0x16)) { throw 'Could not position the preview.' }
                $items = @(Read-Ui)
                if ($items.automationId -notcontains 'RegFileTitleBar') { throw 'The custom preview title bar is missing.' }
                Ui @('hover', 'RegFileTitleBar') | Out-Null
                Ui @('screenshot', 'RegFileMenu', '--output', (Join-Path $ArtifactDirectory 'registry-file-menu-button.png')) | Out-Null
                Ui @('screenshot', 'RegFileLayoutModes', '--output', (Join-Path $ArtifactDirectory 'registry-file-layout-switcher.png')) | Out-Null
                Ui @('wait-for', 'RegFileLayoutBoth', '-p', 'IsSelected', '--value', 'True', '-t', '3000') | Out-Null
                $navigation = @($items | Where-Object automationId -eq 'RegFileKeys')[0]
                $source = @($items | Where-Object automationId -eq 'RegFileSource')[0]
                $values = @($items | Where-Object automationId -eq 'RegFileValues')[0]
                $target = @($items | Where-Object automationId -eq 'RegFileTarget')[0]
                if (-not $navigation -or -not $source -or -not $values -or $source.x + $source.width -gt $navigation.x -or $navigation.y + $navigation.height -gt $values.y) {
                    throw 'Preview panes do not keep source left and the key tree above values on the right.'
                }
                if (-not $target -or $target.height / ([UxTestWindow]::GetDpiForWindow($script:mainWindow) / 96.0) -gt 32) { throw 'The target selector does not fit the compact status bar.' }
                if ($user32.OpenSubKey($classKey) -or $user64.OpenSubKey($classKey)) { throw 'Opening a preview modified the registry.' }
                if ($items.automationId -notcontains 'RegFileMetadata' -or $items.name -notcontains 'Registry preview sample') {
                    throw 'Header metadata is missing from the preview.'
                }
                $metadata = @($items | Where-Object automationId -eq 'RegFileMetadata')[0]
                $keysHeading = @($items | Where-Object automationId -eq 'RegFileKeysHeading')[0]
                $valuesHeading = @($items | Where-Object automationId -eq 'RegFileValuesHeading')[0]
                if (-not $keysHeading -or -not $valuesHeading -or $metadata.y + $metadata.height -gt $keysHeading.y -or
                    [Math]::Abs($keysHeading.x - $valuesHeading.x) -gt 1) {
                    throw 'File metadata is not above the matching Keys and Values preview headings.'
                }
                Ui @('wait-for', 'RegFileUndo', '-p', 'IsEnabled', '--value', 'False', '-t', '3000') | Out-Null
                Ui @('wait-for', 'RegFileRedo', '-p', 'IsEnabled', '--value', 'False', '-t', '3000') | Out-Null
                $initialEditor = @($items | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })[0]
                Ui @('send-keys', 'ctrl+end', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                Wait-Ui { param($items) $items.name -notcontains 'Ln 1, Col 1' }
                Ui @('send-keys', 'alt+f', '--via', 'send-input') | Out-Null
                [void](Read-ContextMenu 'Reload')
                Ui @('send-keys', 'r', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Ln 1, Col 1' }
                Ui @('wait-for', 'RegFileUndo', '-p', 'IsEnabled', '--value', 'False', '-t', '3000') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                $smallInset = (Read-SourceCharacterMetrics).Inset
                $largeSourceFile = Join-Path $ArtifactDirectory 'large-line-margin.reg'
                $largeSource = [IO.File]::ReadAllText($importFile) + [string]::Concat(@(1..12000 | ForEach-Object { "; margin line" + [Environment]::NewLine }))
                [IO.File]::WriteAllText($largeSourceFile, $largeSource, [Text.Encoding]::Unicode)
                Ui @('send-keys', 'ctrl+o', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                File-Picker 'Open' $largeSourceFile
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                Ui @('send-keys', 'ctrl+home', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                $largeInset = (Read-SourceCharacterMetrics).Inset
                if ($largeInset -le $smallInset + 4) { throw 'Loading a five-digit line count did not widen the line-number margin.' }
                Set-Clipboard -Value ([IO.File]::ReadAllText($importFile))
                Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
                $clearedInset = (Read-SourceCharacterMetrics).Inset
                if ($clearedInset -ge $largeInset - 4) { throw 'Deleting lines did not shrink the line-number margin.' }
                Ui @('send-keys', 'ctrl+z', '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                Ui @('send-keys', 'ctrl+home', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                $restoredInset = (Read-SourceCharacterMetrics).Inset
                if ($restoredInset -le $smallInset + 4) { throw 'Restoring lines did not widen the line-number margin.' }
                @{ small = $smallInset; loaded = $largeInset; deleted = $clearedInset; restored = $restoredInset } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'registry-file-line-margin.json')
                Ui @('send-keys', 'ctrl+o', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                File-Picker 'Open' $importFile
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                Ui @('send-keys', 'ctrl+shift+s', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'False', '-t', '5000') | Out-Null
                [UxTestWindow]::CloseWindow($originalWindow)
                Wait-Ui { param($items) $items.name -contains 'Finish the current operation before closing.' } -All
                File-Picker 'Cancel'
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                $notice = @(Read-Ui | Where-Object { $_.automationId -like 'Notification_*' -and $_.name -like "*Can't close yet*" })[0]
                $dismissNotice = @(Elements $notice.children | Where-Object { $_.automationId -like 'DismissNotification_*' })[0]
                Ui @('invoke', $dismissNotice.selector, '--action', 'invoke') | Out-Null
                Wait-Ui { param($items) $items.name -notcontains 'Finish the current operation before closing.' }
                Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', $initialEditor.selector) | Out-Null
                $fileMenu = Read-ContextMenu 'Reload'
                if (($fileMenu.Items.automationId -join ',') -cne 'RegFileOpen,RegFileReload,RegFileSave,RegFileSaveAs,RegFileSettings,RegFileClose') {
                    throw 'The File menu commands are missing or out of order.'
                }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-menu.png')) -Window $fileMenu.Window | Out-Null
                Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
                Ui @('invoke', 'RegFileMetadata', '--action', 'collapse') | Out-Null
                Wait-Ui { param($items) $items.name -notcontains 'Registry preview sample' }
                $collapsedTree = @(Read-Ui | Where-Object automationId -eq 'RegFileKeys')[0]
                if ($collapsedTree.height -le $navigation.height) { throw 'Collapsing metadata did not release tree space.' }
                Ui @('invoke', 'RegFileMetadata', '--action', 'expand') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Registry preview sample' }
                Ui @('hover', 'RegFileTitleBar') | Out-Null
                $metadataEditor = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })[0]
                Ui @('focus', $metadataEditor.selector) | Out-Null
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-metadata.png')) | Out-Null
                if (-not [UxTestWindow]::SetWindowPos($script:mainWindow, 0, 0, 0, 780, 520, 0x16)) { throw 'Could not test the minimum preview height.' }
                Wait-Ui { param($items)
                    $values = @($items | Where-Object automationId -eq 'RegFileValues')[0]
                    $status = @($items | Where-Object automationId -eq 'RegFileStatus')[0]
                    $values -and $status -and $values.height -ge 76 -and $values.y + $values.height -le $status.y
                }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-metadata-minimum.png')) | Out-Null
                [void][UxTestWindow]::SetWindowPos($script:mainWindow, 0, 0, 0, 1200, 780, 0x16)
                Ui @('invoke', 'RegFileMetadata', '--action', 'collapse') | Out-Null
                $leaf = Pick 'TreeItem' ($classKey.Split([char]92))[-1]
                Ui @('invoke', $leaf.selector, '--action', 'expand') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Removed (Delete)' -and $items.name -contains 'Reset (Delete, then recreate)' }

                for ($navigation = 0; $navigation -lt 3; $navigation++) {
                    $valueRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' })
                    if ($valueRow.Count -ne 1) { throw 'The file preview value row is missing.' }
                    Ui @('invoke', $valueRow[0].selector, '--action', 'select') | Out-Null
                    Ui @('wait-for', $valueRow[0].selector, '-p', 'IsSelected', '--value', 'True', '-t', '3000') | Out-Null
                    $parentKey = Pick 'TreeItem' 'Software'
                    Ui @('click', $parentKey.selector, '--double') | Out-Null
                    Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' }).Count -eq 0 }
                    $parentKey = Pick 'TreeItem' 'Software'
                    Ui @('invoke', $parentKey.selector, '--action', 'expand') | Out-Null
                    $leaf = Pick 'TreeItem' ($classKey.Split([char]92))[-1]
                    Ui @('invoke', $leaf.selector, '--action', 'select') | Out-Null
                    Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' }).Count -eq 1 }
                }
                Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', $leaf.selector) | Out-Null
                [void](Read-ContextMenu 'Reload')
                Ui @('send-keys', 'escape', '--via', 'send-input') | Out-Null
                $typingEditor = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })[0]
                $typingRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' })[0]
                Ui @('click', $typingRow.selector) | Out-Null
                $valueLine = '"RegeditViewFixture"="32"'
                $valueLineNumber = [Array]::IndexOf($lines, $valueLine) + 1
                Wait-Ui { param($items) $items.name -contains ('Ln ' + $valueLineNumber + ', Col 1') }
                Ui @('send-keys', 'end', '--target', $typingEditor.selector, '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'left', '--via', 'send-input') | Out-Null
                $typed = ''
                foreach ($character in @('a', 'b', 'c')) {
                    Ui @('send-keys', $character, '--via', 'send-input') | Out-Null
                    $typed += $character
                    Start-Sleep -Milliseconds 600
                    $expectedCaret = 'Ln ' + $valueLineNumber + ', Col ' + ($valueLine.Length + $typed.Length)
                    $state = @(Read-Ui)
                    if ($state.name -notcontains $expectedCaret) {
                        throw "Preview refresh moved the typing caret; expected $expectedCaret."
                    }
                    if (@($state | Where-Object { $_.type -eq 'ListItem' -and $_.name -like ('*REG_SZ, 32' + $typed + ', line*') }).Count -ne 1) {
                        throw 'Typing did not update the selected value preview.'
                    }
                }
                Ui @('send-keys', 'd', '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'ctrl+shift+left', '--via', 'send-input') | Out-Null
                Start-Sleep -Milliseconds 600
                Set-Clipboard -Value 'caret selection sentinel'
                Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
                Assert-Clipboard '32abcd' 'registry-file-selection-after-refresh.txt' 'caret selection sentinel'
                $initialSource = @(Read-Ui | Where-Object automationId -eq 'RegFileSource')[0]
                Ui @('send-keys', 'left', '--target', 'RegFileSourceResize', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RegFileSource' -and $_.width -lt $initialSource.width - 10 }).Count -eq 1 }
                $splitSource = @(Read-Ui | Where-Object automationId -eq 'RegFileSource')[0]
                Set-PreviewLayout 'Editor'
                $editorOnly = @(Read-Ui | Where-Object automationId -eq 'RegFileSource')[0]
                if ($editorOnly.width -le $splitSource.width + 100) { throw 'Editor-only layout did not reclaim preview space.' }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-editor-only.png')) | Out-Null
                Set-PreviewLayout 'Preview'
                $previewOnly = @(Read-Ui | Where-Object automationId -eq 'RegFileKeys')[0]
                if ($previewOnly.width -le $splitSource.width + 100) { throw 'Preview-only layout did not reclaim editor space.' }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-preview-only.png')) | Out-Null
                Ui @('send-keys', 'left', '--target', 'RegFileLayoutPreview', '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileLayoutBoth', '-p', 'IsSelected', '--value', 'True', '-t', '3000') | Out-Null
                Wait-Ui { param($items) $items.automationId -contains 'RegFileSource' -and $items.automationId -contains 'RegFileKeys' }
                $restoredSource = @(Read-Ui | Where-Object automationId -eq 'RegFileSource')[0]
                if ([Math]::Abs($restoredSource.width - $splitSource.width) -gt 2) { throw 'Switching layouts lost the resized split width.' }
                Set-Clipboard -Value 'layout selection sentinel'
                Ui @('send-keys', 'ctrl+c', '--target', $typingEditor.selector, '--via', 'send-input') | Out-Null
                Assert-Clipboard '32abcd' 'registry-file-selection-after-layout.txt' 'layout selection sentinel'
                Set-PreviewLayout 'Editor'
                Ui @('send-keys', 'x', '--target', $typingEditor.selector, '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                Set-PreviewLayout 'Preview'
                Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*REG_SZ, x, line*' }).Count -eq 1 }
                Ui @('send-keys', 'alt+z', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*REG_SZ, 32abcd, line*' }).Count -eq 1 }
                Set-PreviewLayout 'Both'
                Ui @('focus', $typingEditor.selector) | Out-Null
                foreach ($mode in @(@('alt+1','Editor'), @('alt+3','Preview'), @('alt+2','Both'))) {
                    Ui @('send-keys', $mode[0], '--via', 'send-input') | Out-Null
                    Ui @('wait-for', ('RegFileLayout' + $mode[1]), '-p', 'IsSelected', '--value', 'True', '-t', '3000') | Out-Null
                }

                $fontRow = @(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' })[0]
                Ui @('click', $fontRow.selector) | Out-Null
                Ui @('send-keys', 'end', '--target', $typingEditor.selector, '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'left', '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'ctrl+shift+left', '--via', 'send-input') | Out-Null
                Set-Clipboard -Value 'before font selection sentinel'
                Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
                Assert-Clipboard '32abcd' 'registry-file-selection-before-font.txt' 'before font selection sentinel'
                $originalEditorPreferences = Get-Content $settingsFile -Raw | ConvertFrom-Json
                $originalEditorFamily = $originalEditorPreferences.EditorFont.Family ?? 'Cascadia Code'
                $originalEditorSize = $originalEditorPreferences.EditorFont.Size ?? 11
                $originalCharacterHeight = Read-SourceCharacterHeight
                $previewWindow = $script:mainWindow
                Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', $typingEditor.selector) | Out-Null
                [void](Read-ContextMenu 'Settings…')
                Ui @('send-keys', 't', '--via', 'send-input') | Out-Null
                try {
                    $script:mainWindow = $originalWindow
                    [UxTestWindow]::TargetWindow = $originalWindow
                    Wait-Ui { param($items) $items.automationId -contains 'EditorLayoutSelector' -and $items.automationId -contains 'EditorFontSelector' }
                    Wait-Ui {
                        param($items)
                        $focusJson = Ui @('get-focused', '--json')
                        $focusJson | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'registry-file-settings-focus.json')
                        $focused = $focusJson | ConvertFrom-Json
                        $focused.hasFocus -and $focused.element.automationId -eq 'EditorLayoutSelector'
                    }
                    Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'EditorLayoutSelector') | Out-Null
                    Set-EditorFontFamily 'Courier New'
                    Step-EditorFontSize 'UpSpinButton'
                    Wait-Ui { param($items)
                        $prefs = Get-Content $settingsFile -Raw | ConvertFrom-Json
                        $prefs.RegistryFileLayout -eq 0 -and $prefs.EditorFont.Family -ceq 'Courier New' -and $prefs.EditorFont.Size -eq $originalEditorSize + 3
                    }
                    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-editor-settings.png')) | Out-Null
                } finally {
                    $script:mainWindow = $previewWindow
                    [UxTestWindow]::TargetWindow = $previewWindow
                    Set-TestWindowForeground
                }
                $current = @(Read-Ui)
                if ($current.automationId -notcontains 'RegFileSource' -or $current.automationId -notcontains 'RegFileKeys') { throw 'Changing the default view changed an already open preview.' }
                $newCharacterHeight = Read-SourceCharacterHeight
                if ($newCharacterHeight -le $originalCharacterHeight + 1) { throw 'The editor did not apply the new point size live.' }
                Ui @('screenshot', 'RegFileSource', '--output', (Join-Path $ArtifactDirectory 'registry-file-custom-font.png')) | Out-Null
                Ui @('wait-for', 'RegFileUndo', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Set-Clipboard -Value 'font selection sentinel'
                Ui @('send-keys', 'ctrl+c', '--target', $typingEditor.selector, '--via', 'send-input') | Out-Null
                Assert-Clipboard '32abcd' 'registry-file-selection-after-font.txt' 'font selection sentinel'

                foreach ($defaultView in 0, 2) {
                    if ($defaultView -eq 2) {
                        Ui @('send-keys', 'ctrl+,', '--via', 'send-input', '--target', $typingEditor.selector) | Out-Null
                        try {
                            $script:mainWindow = $originalWindow
                            [UxTestWindow]::TargetWindow = $originalWindow
                            Wait-Ui { param($items) $items.automationId -contains 'EditorLayoutSelector' }
                            Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'EditorLayoutSelector') | Out-Null
                            Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).RegistryFileLayout -eq 2 }
                        } finally {
                            $script:mainWindow = $previewWindow
                            [UxTestWindow]::TargetWindow = $previewWindow
                            Set-TestWindowForeground
                        }
                    }
                    try {
                        $script:mainWindow = $originalWindow
                        [UxTestWindow]::TargetWindow = $originalWindow
                        $registryTab = Pick 'TabItem' ($fixtureName + ' · 32-bit')
                        Ui @('invoke', $registryTab.selector, '--action', 'select') | Out-Null
                        Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
                        Open-Menu 'Import registry file…'
                        File-Picker 'Open' $importFile
                        $timer = [Diagnostics.Stopwatch]::StartNew()
                        do {
                            $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
                            $newPreview = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.title -like '*view-target.reg*' -and $_.hwnd -ne $previewWindow })
                            if ($newPreview.Count -eq 1) { break }
                            Start-Sleep -Milliseconds 100
                        } while ($timer.Elapsed.TotalSeconds -lt 15)
                        if ($newPreview.Count -ne 1) { throw 'The second preview did not open.' }
                        $script:mainWindow = [long]$newPreview[0].hwnd
                        [UxTestWindow]::TargetWindow = $script:mainWindow
                        Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '10000') | Out-Null
                        Wait-Ui { param($items)
                            $hasEditor = $items.automationId -contains 'RegFileSource'
                            $hasPreview = $items.automationId -contains 'RegFileKeys'
                            $hasEditor -eq ($defaultView -eq 0) -and $hasPreview -eq ($defaultView -eq 2)
                        }
                        $layoutName = if ($defaultView -eq 0) { 'editor' } else { 'preview' }
                        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory ('registry-file-default-' + $layoutName + '.png'))) | Out-Null
                        $closeTarget = 'RegFileMenu'
                        if ($defaultView -eq 0) {
                            if ([Math]::Abs((Read-SourceCharacterHeight) - $newCharacterHeight) -gt 1) { throw 'A new preview did not inherit the editor font.' }
                            $closeTarget = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })[0].selector
                        }
                        Ui @('send-keys', 'alt+f', '--via', 'send-input', '--target', $closeTarget) | Out-Null
                        [void](Read-ContextMenu 'Close')
                        Ui @('send-keys', 'c', '--via', 'send-input') | Out-Null
                        Start-Sleep -Milliseconds 400
                        $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
                        if ($windows.hwnd -contains $script:mainWindow) { throw 'File Close did not close the clean preview.' }
                    } finally {
                        $script:mainWindow = $previewWindow
                        [UxTestWindow]::TargetWindow = $previewWindow
                        Set-TestWindowForeground
                    }
                }
                Ui @('send-keys', 'ctrl+,', '--via', 'send-input', '--target', $typingEditor.selector) | Out-Null
                try {
                    $script:mainWindow = $originalWindow
                    [UxTestWindow]::TargetWindow = $originalWindow
                    Wait-Ui { param($items) $items.automationId -contains 'EditorLayoutSelector' }
                    Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'EditorLayoutSelector') | Out-Null
                    Ui @('send-keys', 'down', '--via', 'send-input', '--target', 'EditorLayoutSelector') | Out-Null
                    Set-EditorFontFamily $originalEditorFamily
                    Step-EditorFontSize 'DownSpinButton'
                    Wait-Ui { param($items)
                        $prefs = Get-Content $settingsFile -Raw | ConvertFrom-Json
                        $prefs.RegistryFileLayout -eq 1 -and $prefs.EditorFont.Family -ceq $originalEditorFamily -and $prefs.EditorFont.Size -eq $originalEditorSize
                    }
                } finally {
                    $script:mainWindow = $previewWindow
                    [UxTestWindow]::TargetWindow = $previewWindow
                    Set-TestWindowForeground
                }
                if ([Math]::Abs((Read-SourceCharacterHeight) - $originalCharacterHeight) -gt 1) { throw 'Restoring editor preferences did not restore the font size.' }
                [ordered]@{ originalHeight=$originalCharacterHeight; customHeight=$newCharacterHeight; restoredHeight=(Read-SourceCharacterHeight) } |
                    ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'registry-file-font-heights.json')
                Ui @('focus', $typingEditor.selector) | Out-Null

                Ui @('send-keys', 'ctrl+end', '--via', 'send-input') | Out-Null
                Start-Sleep -Milliseconds 600
                $originalSource = [IO.File]::ReadAllText($importFile)
                $endCaret = 'Ln ' + ($originalSource -split '\r\n|\n|\r').Count + ', Col 1'
                if ((Read-Ui).name -notcontains $endCaret) { throw 'Synchronizing another key moved the source caret.' }
                Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
                Set-Clipboard -Value 'caret source sentinel'
                Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
                Assert-Clipboard ($originalSource.Replace($valueLine, '"RegeditViewFixture"="32abcd"')) 'registry-file-typed-source.txt' 'caret source sentinel'
                Set-Clipboard -Value $editedSource
                $editors = @(Read-Ui | Where-Object { $_.type -eq 'Edit' -and $_.className -eq 'WinUIEditor.EditorBaseControl' })
                if ($editors.Count -ne 1) { throw 'The WinUIEdit editing surface is missing.' }
                Ui @('send-keys', 'ctrl+a', '--target', $editors[0].selector, '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                Ui @('wait-for', 'RegFileUndo', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Ui @('invoke', 'RegFileUndo') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                Ui @('wait-for', 'RegFileRedo', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Ui @('invoke', 'RegFileMetadata', '--action', 'expand') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Registry preview sample' }
                Ui @('invoke', 'RegFileRedo') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Edited registry preview' -and $items.name -contains 'After import: Sign out' }
                Ui @('invoke', 'RegFileMetadata', '--action', 'collapse') | Out-Null
                Set-Clipboard -Value ($editedSource + '"Incomplete')
                Ui @('send-keys', 'ctrl+a', '--target', $editors[0].selector, '--via', 'send-input') | Out-Null
                Ui @('send-keys', 'ctrl+v', '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'False', '-t', '5000') | Out-Null
                Set-PreviewLayout 'Preview'
                Ui @('invoke', 'RegFileErrorCount') | Out-Null
                Ui @('wait-for', 'RegFileLayoutBoth', '-p', 'IsSelected', '--value', 'True', '-t', '3000') | Out-Null
                Wait-Ui { param($items) $items.automationId -contains 'RegFileSource' }
                Assert-RegistrySyntax 'light-incomplete' @(0x008000, 0xA31515, 0x0000FF, 0x098658)
                Ui @('send-keys', 'ctrl+z', '--target', $editors[0].selector, '--via', 'send-input') | Out-Null
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '5000') | Out-Null
                $sourceRoot = Pick 'TreeItem' ($classKey.Split([char]92))[-1]
                Ui @('click', $sourceRoot.selector) | Out-Null
                Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' }).Count -eq 1 }
                Ui @('hover', 'RegFileTitleBar') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Removed (Delete)' -and $items.name -contains 'Reset (Delete, then recreate)' }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-preview.png')) | Out-Null
                Assert-RegistrySyntax 'light' @(0x008000, 0xA31515, 0x0000FF, 0x098658)
                if (-not [UxTestWindow]::SetWindowPos($script:mainWindow, 0, 0, 0, 780, 720, 0x16)) { throw 'Could not narrow the preview.' }
                Wait-Ui { param($items) $items.name -contains 'Read-only preview' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*RegeditViewFixture*' }).Count -eq 1 }
                Ui @('hover', 'RegFileTitleBar') | Out-Null
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-preview-narrow.png')) | Out-Null
                [void][UxTestWindow]::SetWindowPos($script:mainWindow, 0, 0, 0, 1200, 780, 0x16)
                $previewWindow = $script:mainWindow
                try {
                    $script:mainWindow = $originalWindow
                    [UxTestWindow]::TargetWindow = $originalWindow
                    Open-Menu 'Settings'
                    Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
                    Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
                    Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Dark' }
                    $script:mainWindow = $previewWindow
                    [UxTestWindow]::TargetWindow = $previewWindow
                    Set-TestWindowForeground
                    Ui @('hover', 'RegFileTitleBar') | Out-Null
                    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-preview-dark.png')) | Out-Null
                    Ui @('screenshot', 'RegFileMenu', '--output', (Join-Path $ArtifactDirectory 'registry-file-menu-button-dark.png')) | Out-Null
                    Assert-RegistrySyntax 'dark' @(0x6A9955, 0xCE9178, 0x569CD6, 0xB5CEA8)
                    $script:mainWindow = $originalWindow
                    [UxTestWindow]::TargetWindow = $originalWindow
                    Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
                    Ui @('send-keys', 'down', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
                    Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Light' }
                    $registryTab = Pick 'TabItem' ($fixtureName + ' · 32-bit')
                    Ui @('invoke', $registryTab.selector, '--action', 'select') | Out-Null
                    Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
                } finally {
                    $script:mainWindow = $previewWindow
                    [UxTestWindow]::TargetWindow = $previewWindow
                }
                Ui @('send-keys', 'alt+i', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) @($items | Where-Object { $_.name -like '*32-bit registry*' -and $_.name -like '*current editor contents*' }).Count -gt 0 }
                Primary
                Wait-Ui { param($items) $items.name -contains 'Import complete' }
                Wait-Ui { param($items) @($items | Where-Object { $_.name -like '*File recommends: Sign out.*' }).Count -gt 0 }
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'registry-file-follow-up.png')) | Out-Null
                if ([IO.File]::ReadAllText($importFile) -match 'unsaved') { throw 'Import saved the edited source file.' }
                [UxTestWindow]::CloseWindow($originalWindow)
                Wait-Ui { param($items) $items.name -contains 'Unsaved registry file' }
                Ui @('invoke', 'Cancel', '--action', 'invoke', '--type', 'Button') | Out-Null
                Wait-Ui { param($items) $items.automationId -contains 'RegFileImport' -and $items.name -notcontains 'Unsaved registry file' }
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Set-TestWindowForeground
                Ui @('send-keys', 'alt+f', '--via', 'send-input') | Out-Null
                $fileMenu = Read-ContextMenu 'Close'
                $close = @($fileMenu.Items | Where-Object automationId -eq 'RegFileClose')[0]
                Ui @('invoke', $close.selector) -Window $fileMenu.Window | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Unsaved registry file' }
                [UxTestWindow]::AltF4($script:mainWindow)
                Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Window' -and $_.name -eq 'Unsaved registry file' }).Count -eq 1 }
                Ui @('invoke', 'Cancel', '--action', 'invoke', '--type', 'Button') | Out-Null
                Wait-Ui { param($items) $items.automationId -contains 'RegFileImport' -and $items.name -notcontains 'Unsaved registry file' }
                Ui @('wait-for', 'RegFileImport', '-p', 'IsEnabled', '--value', 'True', '-t', '3000') | Out-Null
                Ui @('get-focused', '--json') | Set-Content (Join-Path $ArtifactDirectory 'registry-file-focus-after-cancel.json')
                Ui @('send-keys', 'ctrl+w', '--via', 'send-input') | Out-Null
                Wait-Ui { param($items) $items.name -contains 'Unsaved registry file' }
                Ui @('invoke', 'SecondaryButton', '--action', 'invoke') | Out-Null
            } finally {
                $script:mainWindow = $originalWindow
                [UxTestWindow]::TargetWindow = $originalWindow
            }
            $included = $user32.OpenSubKey($classKey)
            $excluded = $user64.OpenSubKey($classKey)
            try {
                if (-not $included -or $included.GetValue('RegeditViewFixture') -cne 'unsaved' -or $excluded) { throw 'Import did not apply the current buffer to the selected 32-bit view.' }
                if ($included.OpenSubKey('Removed')) { throw 'A deleted key survived import.' }
                $reset = $included.OpenSubKey('Reset')
                try {
                    if (-not $reset -or $reset.GetValue('Old') -or $reset.GetValue('After') -cne 'retained') {
                        throw 'Delete then recreate did not clear old contents and retain later writes.'
                    }
                } finally { if ($reset) { $reset.Dispose() } }
            } finally { if ($included) { $included.Dispose() }; if ($excluded) { $excluded.Dispose() } }
        } finally {
            $user32.DeleteSubKeyTree($classKey, $false)
            $user64.DeleteSubKeyTree($classKey, $false)
            $user32.Dispose()
            $user64.Dispose()
        }
        Switch-RegistryView '64-bit registry'
        Wait-Ui { param($items) $items.name -contains $fixtureName -and $items.name -notcontains '32-bit registry view' }
        if ($hadStatus) {
            Open-Menu 'Settings'
            Ui @('invoke', 'StatusBarSwitch', '--action', 'toggle-on') | Out-Null
            Browser
        }
    }

    Test-Ui 'Canceling import and remote connection keeps the original browser' {
        Browser
        $tabs = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Open-Menu 'Import registry file…'
        File-Picker 'Cancel'
        Open-Menu 'Connect network registry…' -Group 'Network registry'
        Fill 'Computer name' 'unused-guest-test'
        Press 'Cancel'
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and $items.name -notcontains 'Computer name' }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $tabs) { throw 'Cancel opened a new registry tab.' }
    }

    Test-Ui 'Shared tool target readouts support keyboard copy in tab and panel' {
        Browser
        $targetParts = @('KeyboardTool') + @(1..4 | ForEach-Object { ('LongSegment' + $_ + ('p' * 160)) }) + @('Trailing target ')
        $targetSubKey = $subKey + '\' + ($targetParts -join '\')
        $targetAddress = 'HKEY_CURRENT_USER\' + $targetSubKey
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($targetSubKey)
        $key.Dispose()
        $targetName = 'Keyboard target ' + ('full title ' * 40) + 'END'
        Open-Menu 'Favorites' -Group 'Tools' -MainTab
        Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        Fill 'Search tool items' ''
        Show-RootFavorites
        Press 'Add favorite…'
        Fill 'Name' $targetName
        Fill 'Registry location' $targetAddress
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Fill 'Search tool items' 'Keyboard target'
        $row = Select-Row 'Keyboard target'
        Set-Preview $true
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'PathText' -and $_.value -ceq $targetAddress }).Count -eq 1 }
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-KeyboardReadout 'Heading' $targetName 'shared-target-tab-name'
        Assert-KeyboardReadout 'PathText' $targetAddress 'shared-target-tab-path'
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -contains 'PathText' }
        $row = Select-Row 'Keyboard target'
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-KeyboardReadout 'Heading' $targetName 'shared-target-panel-name'
        Assert-KeyboardReadout 'PathText' $targetAddress 'shared-target-panel-path'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'shared-target-keyboard.png')) | Out-Null
        Press 'Open tool in tab'
        if ((Read-Ui).automationId -contains 'ToolSelector') { Press 'Close tool panel' }
        $row = Select-Row 'Keyboard target'
        Ui @('send-keys', 'delete', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Fill 'Search tool items' ''
        Open-Menu 'Close tab'
        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteSubKeyTree('KeyboardTool') }
        finally { $key.Dispose() }
    }

    Test-Ui 'Snapshot selected target supports keyboard copy of long and unnamed values' {
        Browser
        $snapshotSubKey = $subKey + '\KeyboardSnapshot'
        $relative = (@(1..3 | ForEach-Object { ('LongSegment' + $_ + ('s' * 160)) }) + @('Trailing key ')) -join '\'
        $targetSubKey = $snapshotSubKey + '\' + $relative
        $targetAddress = 'HKEY_CURRENT_USER\' + $targetSubKey
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($targetSubKey)
        $key.Dispose()
        Go-ToKey ('HKEY_CURRENT_USER\' + $snapshotSubKey)
        $snapshotFile = Join-Path $ArtifactDirectory 'keyboard-target.regsnap'
        Snapshot-Action 'Capture branch snapshot…'
        File-Picker 'Save' $snapshotFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot saved' }
        $valueName = 'KeyboardValue' + ('v' * 512) + ' '
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($targetSubKey, $true)
        try {
            $key.SetValue($valueName, [byte[]](0, 255, 42), [Microsoft.Win32.RegistryValueKind]::Binary)
            $key.SetValue('', 'unnamed value')
        } finally { $key.Dispose() }
        Snapshot-Action 'Compare saved snapshot with registry…'
        File-Picker 'Open' $snapshotFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot comparison' }
        if ((Read-Ui).automationId -contains 'SelectedTarget') { throw 'Snapshot shows a target without a selected difference.' }
        $row = Select-Row 'KeyboardValue'
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-KeyboardReadout 'SelectedTarget' ($targetAddress + "`r`n" + $valueName) 'snapshot-long-target'
        $differences = Pick 'List' 'Snapshot differences' -All
        $unnamed = @(Elements $differences.children | Where-Object {
            $_.type -eq 'ListItem' -and @((Elements $_.children).name) -ccontains '(Default)'
        })
        if ($unnamed.Count -ne 1) { throw "Expected one unnamed snapshot difference, found $($unnamed.Count)." }
        $row = $unnamed[0]
        Ui @('invoke', $row.selector, '--action', 'select') | Out-Null
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Assert-KeyboardReadout 'SelectedTarget' ($targetAddress + "`r`n(Default)") 'snapshot-unnamed-target'
        Press 'Clear selection'
        Wait-Ui { param($items) $items.automationId -notcontains 'SelectedTarget' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'snapshot-target-keyboard.png')) | Out-Null
        $comparison = Pick 'Window' 'Snapshot comparison'
        $close = @($comparison.children | Where-Object automationId -eq 'CloseButton')[0]
        Ui @('invoke', $close.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Snapshot comparison' }
        Go-ToKey $address
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $key.DeleteSubKeyTree('KeyboardSnapshot') }
        finally { $key.Dispose() }
    }

    Test-Ui 'Saved snapshot comparison exports precisely the selected raw value' {
        Browser
        $snapshotFile = Join-Path $ArtifactDirectory 'branch.regsnap'
        Snapshot-Action 'Capture branch snapshot…'
        File-Picker 'Save' $snapshotFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot saved' }
        $snapshot = Get-Content -Raw -LiteralPath $snapshotFile | ConvertFrom-Json
        if ($snapshot.Root.SubKey -cne $subKey -or $snapshot.Keys.Count -lt 2) { throw 'Captured the wrong snapshot root.' }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.SetValue('Phase5Chosen', [byte[]](0,255,127), [Microsoft.Win32.RegistryValueKind]::Binary)
            $key.SetValue('Phase5Omitted', 'unchosen')
        } finally { $key.Dispose() }
        Snapshot-Action 'Compare saved snapshot with registry…'
        File-Picker 'Open' $snapshotFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot comparison' }
        if ((Pick 'Button' 'Export selected').isEnabled) { throw 'Comparison export enabled without a selection.' }
        Select-Row 'Phase5Chosen' | Out-Null
        $exported = Join-Path $ArtifactDirectory 'selected.reg'
        Press 'Export selected'
        File-Picker 'Save' $exported
        Wait-Ui { param($items) $items.name -contains 'Differences exported' }
        $content = Get-Content -Raw -LiteralPath $exported
        if ($content -notmatch '(?m)^"Phase5Chosen"=hex(?:\(3\))?:00,ff,7f\r?$' -or $content.Contains('Phase5Omitted')) { throw 'Comparison export lost raw bytes or included another change.' }
        $comparison = Pick 'Window' 'Snapshot comparison'
        $close = @($comparison.children | Where-Object automationId -eq 'CloseButton')[0]
        Ui @('invoke', $close.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Snapshot comparison' }
    }

    Test-Ui 'Live snapshot comparison stays responsive when resized and reopened' {
        Browser
        $snapshotFile = Join-Path $ArtifactDirectory 'snapshot-layout.regsnap'
        Snapshot-Action 'Capture branch snapshot…'
        File-Picker 'Save' $snapshotFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot saved' }
        Snapshot-Action 'Compare last capture with registry'
        Wait-Ui { param($items) $items.name -contains 'Snapshot comparison' }
        foreach ($width in 760, 1380) {
            if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, $width, 900, 0x16)) {
                throw 'Could not resize the snapshot comparison window.'
            }
            Wait-Ui { param($items) $items.name -contains 'No differences. Keys, types, and raw value bytes match.' }
            if ((Pick 'Button' 'Export selected').isEnabled) { throw 'An unchanged snapshot enabled export.' }
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'snapshot-layout.png')) | Out-Null
        $comparison = Pick 'Window' 'Snapshot comparison'
        $close = @($comparison.children | Where-Object automationId -eq 'CloseButton')[0]
        Ui @('invoke', $close.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Snapshot comparison' }
        Snapshot-Action 'Reopen last comparison'
        Wait-Ui { param($items) $items.name -contains 'No differences. Keys, types, and raw value bytes match.' }
        $comparison = Pick 'Window' 'Snapshot comparison'
        $close = @($comparison.children | Where-Object automationId -eq 'CloseButton')[0]
        Ui @('invoke', $close.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Snapshot comparison' }
    }

    Test-Ui 'Historical version 1 snapshot opens through the browser command' {
        Browser
        $started = [DateTimeOffset]::Parse('2020-01-02T03:04:05.0000000+00:00')
        $legacyFile = Join-Path $ArtifactDirectory 'historical-v1.regsnap'
        $snapshot = @{
            Version = 1
            Root = @{ Hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser; SubKey = $subKey; View = 256 }
            Started = $started.ToString('o'); Completed = $started.AddMinutes(1).ToString('o')
            Keys = @(@{ Path = ''; Values = @(@{ Name = 'Counter'; Type = 4; Data = [Convert]::ToBase64String([BitConverter]::GetBytes(0)) }) })
            Computer = $env:COMPUTERNAME
        }
        [IO.File]::WriteAllText($legacyFile, ($snapshot | ConvertTo-Json -Depth 8))
        Snapshot-Action 'Compare saved snapshot with registry…'
        File-Picker 'Open' $legacyFile
        Wait-Ui { param($items) $items.name -contains 'Snapshot comparison' }
        $comparison = Pick 'Window' 'Snapshot comparison'
        $difference = @(Elements $comparison.children | Where-Object {
            $_.type -eq 'ListItem' -and $_.name -like 'Data changed: Counter,*'
        })
        if ($difference.Count -ne 1) { throw 'The historical v1 Counter data was not visible as a data change.' }
        Ui @('invoke', $difference[0].selector, '--action', 'select') | Out-Null
        $close = @($comparison.children | Where-Object automationId -eq 'CloseButton')[0]
        Ui @('invoke', $close.selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains 'Snapshot comparison' }
    }

    Test-Ui 'Branch export writes exact UTF-16 Counter and Bytes values' {
        Browser
        $exported = Join-Path $ArtifactDirectory 'release-branch.reg'
        Press 'Export…'
        File-Picker 'Save' $exported
        Wait-Ui { param($items) $items.name -contains 'Export complete' }
        $bytes = [IO.File]::ReadAllBytes($exported)
        if ($bytes.Length -lt 2 -or $bytes[0] -ne 0xff -or $bytes[1] -ne 0xfe) { throw 'Branch export did not use UTF-16 with a BOM.' }
        $content = [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
        if ($content -notmatch '(?m)^"Counter"=hex\(4\):01,00,00,00\r?$' -or
            $content -notmatch '(?m)^"Bytes"=hex\(3\):00,01,02,03,04,05,06,07,08,09,0a,0b,0c,0d,0e,0f\r?$' -or
            -not $content.Contains("[$address]")) {
            throw 'The exported branch omitted or altered its exact Counter, Bytes, or root key data.'
        }
    }

    Test-Ui 'Connection dialog focuses its input and Enter cancels without connecting' {
        Browser
        $tabs = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Open-Menu 'Connect network registry…' -Group 'Network registry'
        Wait-Ui {
            param($items)
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            $items.automationId -contains 'PrimaryButton' -and $focused.hasFocus -and $focused.element.name -eq 'Computer name'
        }
        if (@(Read-Ui | Where-Object name -like 'Uses your Windows credentials.*').Count -ne 1) { throw 'Connection guidance is missing.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'connection-dialog.png')) | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $tabs) { throw 'Canceling a connection opened a registry tab.' }
    }

    Test-Ui 'Print range cancel retains the chosen browser and opens no preview' {
        Browser
        Press 'Print…'
        Wait-Ui { param($items) $items.name -contains 'Print range' }
        $range = Pick 'RadioButton' 'Current key'
        if (-not $range.isEnabled) { throw 'The print range is unavailable for a registry key.' }
        Ui @('invoke', (Pick 'RadioButton' 'Selected branch').selector, '--action', 'select') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'print-range-dialog.png')) | Out-Null
        Press 'Cancel'
        Wait-Ui { param($items) $items.name -notcontains 'Print range' -and $items.automationId -contains 'EntriesList' }
    }

    Test-Ui 'Native print preview opens for the selected key' {
        Browser; Press 'Print…'
        Wait-Ui { param($items) $items.name -contains 'Print range' }
        Primary
        Open-VerifiedPrintPreview
    }

    Test-Ui 'Multipage print preview opens and cancels without printing' {
        Browser
        $printFixtureNames = @(1..48 | ForEach-Object { 'PrintFixture{0:D2}' -f $_ })
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            for ($n = 1; $n -le $printFixtureNames.Count; $n++) {
                $key.SetValue($printFixtureNames[$n - 1], ('Fixture value {0:D2}: ' -f $n) + ('x' * 256))
            }
        } finally { $key.Dispose() }
        try {
            Press 'Refresh'
            Press 'Print…'
            Wait-Ui { param($items) $items.name -contains 'Print range' }
            Primary
            Open-VerifiedPrintPreview -MultiplePages
        } finally {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { foreach ($name in $printFixtureNames) { $key.DeleteValue($name, $false) } }
            finally { $key.Dispose() }
            $remainingPreview = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json |
                Where-Object { $_.hwnd -ne $mainWindow -and $_.title -ceq "$address - Print" })
            if (-not $remainingPreview.Count -and @(Read-Ui | Where-Object { $_.automationId -eq 'EntriesList' }).Count) {
                Press 'Refresh'
            }
        }
    }

    Test-Ui 'Large value keyboard inspection retains full contents with preview hidden' {
        Browser
        $largeText = ('x' * (524288 - 12)) + 'suffixneedle'
        $largeKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $largeKey.SetValue('LargeKeyboardValue', $largeText, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $largeKey.Dispose() }
        try {
            Press 'Refresh'
            Press 'Filter'; Fill 'Filter this key' 'suffixneedle'
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'LargeKeyboardValue,*' }).Count -eq 1 }
            $row = Select-Row 'LargeKeyboardValue'
            if ($row.name.Length -gt 600 -or $row.name -like '*suffixneedle*') { throw 'Row automation exposes full large value contents.' }
            if (@(Read-Ui | Where-Object automationId -eq 'PreviewData').Count) { throw 'The optional value preview must be hidden.' }
            Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'FullValueData' }
            $focusSteps = [Collections.Generic.List[object]]::new()
            try {
                for ($step = 0; $step -lt 12; $step++) {
                    $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
                    $focusSteps.Add(@{
                        hasFocus = $focused.hasFocus; type = $focused.element.type
                        name = $focused.element.name; automationId = $focused.element.automationId
                    })
                    if ($focused.hasFocus -and $focused.element.automationId -eq 'FullValueData') { break }
                    Ui @('send-keys', 'tab', '--via', 'send-input') | Out-Null
                }
                if (-not $focused.hasFocus -or $focused.element.automationId -ne 'FullValueData') {
                    throw 'Tab traversal did not reach the full value field. See inspector-keyboard-focus.json.'
                }
            } finally {
                $focusSteps.ToArray() | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'inspector-keyboard-focus.json') -Encoding utf8
            }
            $copySentinel = 'Large value keyboard inspection sentinel'
            Set-Clipboard -Value $copySentinel
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
            Ui @('send-keys', 'ctrl+c', '--via', 'send-input') | Out-Null
            Assert-Clipboard $largeText 'large-value' $copySentinel
            Ui @('send-keys', 'ctrl+end', '--via', 'send-input') | Out-Null
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'large-value-keyboard.png')) | Out-Null
            Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) $items.automationId -notcontains 'FullValueData' }
            $items = @(Read-Ui)
            $filter = @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Filter this key' })
            $rows = @($items | Where-Object type -eq 'ListItem')
            if ($filter.Count -ne 1 -or $filter[0].value -cne 'suffixneedle' -or $rows.Count -ne 1 -or
                $rows[0].name -notlike 'LargeKeyboardValue,*') { throw 'Dialog Escape changed the browser filter or its results.' }
            # Reopen without selecting or focusing a row to check that dialog dismissal retained selection.
            Ui @('send-keys', 'ctrl+i', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'FullValueData' }
            if ((Pick 'Edit' 'Value name').value -cne 'LargeKeyboardValue') { throw 'Dialog Escape changed the selected value.' }
            Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) $items.automationId -notcontains 'FullValueData' }
            if ((Pick 'Edit' 'Filter this key').value -cne 'suffixneedle') { throw 'Dismissing the reopened inspector changed the filter.' }
            Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Filter this key' }).Count -eq 0 }
        } finally {
            $largeKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
            try { $largeKey.DeleteValue('LargeKeyboardValue', $false) }
            finally { $largeKey.Dispose() }
        }
        Press 'Refresh'
    }

    Test-Ui 'Value inspector exposes decimal hexadecimal and bits' {
        Browser; Select-Row 'Counter' | Out-Null
        $row = Select-Row 'Counter'
        Ui @('click', $row.selector, '--right') | Out-Null
        $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
        $popup = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | Sort-Object height -Descending)[0]
        $item = Pick 'MenuItem' 'Inspect value…' -Window $popup.hwnd
        Ui @('invoke', $item.selector, '--action', 'invoke') -Window $popup.hwnd | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Decimal (unsigned)' }
        $items = Read-Ui
        foreach ($name in @('Decimal (unsigned)', 'Hexadecimal', 'Bits (most significant first)', 'Interpret selected bytes as')) { if ($items.name -notcontains $name) { throw "Missing inspector field: $name" } }
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
    }

    Test-Ui 'Recently inspected remembers explicit value inspections and reopens them' {
        Browser
        $historyFile = Join-Path $dataFolder 'inspection-history.json'
        Tool-Option 'Recently inspected…'
        Wait-Ui { param($items) $items.automationId -contains 'RecordInspections' -and $items.name -contains 'No inspections recorded.' }
        if (Test-Path -LiteralPath $historyFile) { throw 'Opening history created a file before opt-in.' }
        Ui @('invoke', 'RecordInspections', '--action', 'toggle-on') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RecordInspections' -and $_.isEnabled }).Count -eq 1 }
        Ui @('invoke', 'DialogDismissButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'RecordInspections' }

        if ((Read-Ui).name -contains 'Show detail panel') { Press 'Show detail panel' }
        $row = Select-Row 'Counter'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Select-Row 'NeedleOne' | Out-Null
        Tool-Option 'Recently inspected…'
        Wait-Ui { param($items) $items.automationId -contains 'RecentInspectionList' -and $items.name -contains 'Counter' }
        $history = Get-Content -LiteralPath $historyFile -Raw | ConvertFrom-Json
        if ($history.Visits.Count -ne 1 -or $history.Visits[0].ValueName -ne 'Counter') { throw 'History recorded a passive selection or missed the explicit inspection.' }

        $tabs = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'RecordInspections' -and $items.name -contains 'Hexadecimal' }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne ($tabs + 1)) { throw 'The recorded value did not reopen in a new tab.' }
        Open-Menu 'Close tab'
        Browser
        Tool-Option 'Recently inspected…'
        Wait-Ui { param($items) $items.automationId -contains 'RecentInspectionList' }
        Ui @('invoke', 'ClearInspections', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'No inspections recorded.' }
        Ui @('invoke', 'RecordInspections', '--action', 'toggle-off') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RecordInspections' -and $_.isEnabled }).Count -eq 1 }
        Ui @('invoke', 'DialogDismissButton', '--action', 'invoke') | Out-Null
    }

    Test-Ui 'Activity filters work in Advanced search, the search box, and the omnibox, including with key dates' {
        function Select-SearchOption([string] $Id, [string] $Name) {
            Ui @('invoke', $Id, '--action', 'expand') | Out-Null
            $options = Read-ContextMenu $Name -Types @('ListItem')
            $option = @($options.Items | Where-Object name -eq $Name)[0]
            Ui @('invoke', $option.selector, '--action', 'select') -Window $options.Window | Out-Null
        }

        function Assert-ActivityResults([string] $Query, [string[]] $Expected, [switch] $CopyQuery) {
            Wait-Ui { param($items) $items.name -contains "Find: $Query" -and $items.automationId -contains 'BottomTabs' }
            Press 'Open panel tab in main tab'
            Wait-Ui {
                param($items)
                $rows = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*ActivityFixture*' })
                foreach ($name in $Expected) { if (-not ($rows.name -like "*$name*")) { return $false } }
                $items.automationId -contains 'ShowInPanelButton' -and $rows.Count -eq $Expected.Count -and
                    @($items | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Run search' -and $_.isEnabled }).Count -eq 1
            }
            if ($CopyQuery) {
                $sentinel = 'copy search query sentinel'
                Set-Clipboard -Value $sentinel
                Press 'Copy query' -CommandBarId 'ItemCommands'
                Assert-Clipboard $Query 'combined-search-query' $sentinel
            }
            Open-Menu 'Close tab'
            Browser
        }

        Browser
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.SetValue('ActivityFixtureEdited', 'before')
            $key.SetValue('ActivityFixtureInspected', 'inspect')
            $key.SetValue('ActivityFixtureUnrecorded', 'unrecorded')
        } finally { $key.Dispose() }
        Press 'Refresh'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'ActivityFixture*' }).Count -eq 3 }
        $edited = Select-Row 'ActivityFixtureEdited'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $edited.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Edit value ActivityFixtureEdited' }
        Fill 'Value data' 'after'
        Primary
        Wait-Ui { param($items) $items.name -contains 'ActivityFixtureEdited, REG_SZ, after' }

        Tool-Option 'Recently inspected…'
        Wait-Ui { param($items) $items.automationId -contains 'RecordInspections' }
        Ui @('invoke', 'RecordInspections', '--action', 'toggle-on') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RecordInspections' -and $_.isEnabled }).Count -eq 1 }
        Ui @('invoke', 'DialogDismissButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'RecordInspections' }
        if ((Read-Ui).name -contains 'Show detail panel') { Press 'Show detail panel' }
        $inspected = Select-Row 'ActivityFixtureInspected'
        Ui @('send-keys', 'ctrl+i', '--via', 'send-input', '--target', $inspected.selector) | Out-Null

        Press 'Advanced search'
        Wait-Ui {
            param($items)
            $items.automationId -contains 'SearchActivity' -and $items.name -contains 'Query and scope' -and
                $items.name -contains 'Filters' -and $items.name -contains 'My activity' -and $items.name -contains 'Match options'
        }
        Ui @('send-keys', 'ctrl+a backspace', '--via', 'send-input', '--target', 'QueryInput') | Out-Null
        Ui @('invoke', 'SearchScope', '--action', 'expand') | Out-Null
        $scope = Read-ContextMenu 'All hives on this computer · 64-bit' -Types @('ListItem')
        $allHives = @($scope.Items | Where-Object name -like 'All hives*')[0]
        Ui @('invoke', $allHives.selector, '--action', 'select') -Window $scope.Window | Out-Null
        Select-SearchOption 'SearchActivity' 'Edited'
        Wait-Ui { param($items) $items.automationId -notcontains 'SearchInspectionHint' }
        Select-SearchOption 'SearchActivity' 'Inspected'
        Wait-Ui { param($items) $items.automationId -contains 'SearchInspectionHint' }
        Select-SearchOption 'SearchActivity' 'Either'
        Wait-Ui { param($items) $items.automationId -contains 'SearchInspectionHint' }
        Fill 'Activity within' '7d'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'activity-search-dialog.png')) | Out-Null
        Primary
        Assert-ActivityResults 'activity:either within:7d' @('ActivityFixtureEdited', 'ActivityFixtureInspected')

        Fill 'Search registry' 'activity:edited within:7d'
        $search = Pick 'Edit' 'Search registry'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $search.selector) | Out-Null
        Assert-ActivityResults 'activity:edited within:7d' @('ActivityFixtureEdited')

        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
        Ui @('set-value', 'OmniboxInput', '?activity:inspected within:7d') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Search this branch' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Assert-ActivityResults 'activity:inspected within:7d' @('ActivityFixtureInspected')

        Fill 'Search registry' 'ActivityFixture modified:24h'
        Press 'Start search'
        $recorded = @('ActivityFixtureEdited', 'ActivityFixtureInspected')
        Assert-ActivityResults 'ActivityFixture modified:24h' ($recorded + 'ActivityFixtureUnrecorded')

        Fill 'Search registry' 'ActivityFixture'
        Press 'Advanced search'
        Select-SearchOption 'SearchActivity' 'Either'
        Fill 'Activity within' '7d'
        Select-SearchOption 'SearchKeyModified' 'Last 24 hours'
        Primary
        $combined = 'ActivityFixture activity:either within:7d modified:24h'
        Assert-ActivityResults $combined $recorded -CopyQuery

        $copied = Get-Clipboard -Raw
        Fill 'Search registry' $copied
        Press 'Start search'
        Assert-ActivityResults $combined $recorded

        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
        Ui @('set-value', 'OmniboxInput', "?$copied") | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Search this branch' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Assert-ActivityResults $combined $recorded

        Tool-Option 'Recently inspected…'
        Wait-Ui { param($items) $items.automationId -contains 'RecentInspectionList' }
        Ui @('invoke', 'ClearInspections', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'No inspections recorded.' }
        Ui @('invoke', 'RecordInspections', '--action', 'toggle-off') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'RecordInspections' -and $_.isEnabled }).Count -eq 1 }
        Ui @('invoke', 'DialogDismissButton', '--action', 'invoke') | Out-Null
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.DeleteValue('ActivityFixtureEdited')
            $key.DeleteValue('ActivityFixtureInspected')
            $key.DeleteValue('ActivityFixtureUnrecorded')
        } finally { $key.Dispose() }
        Press 'Refresh'
    }

    Test-Ui 'Key last modified works with presets, custom dates, and typed search' {
        function Assert-ModificationResults([string] $Query, [string] $Match) {
            Wait-Ui { param($items) $items.name -contains "Find: $Query" -and $items.automationId -contains 'BottomTabs' }
            Press 'Open panel tab in main tab'
            Wait-Ui {
                param($items)
                $items.automationId -contains 'ShowInPanelButton' -and
                    @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like "$Match,*Key last modified*" }).Count -gt 0
            }
            Open-Menu 'Close tab'
            Browser
        }

        Browser
        Fill 'Search registry' 'modified:'
        Press 'Advanced search'
        Wait-Ui {
            param($items)
            $items.automationId -contains 'SearchKeyModified' -and $items.automationId -contains 'DialogError' -and
                @($items | Where-Object { $_.automationId -eq 'QueryInput' -and -not $_.value }).Count -eq 1
        }
        Ui @('invoke', 'SearchKeyModified', '--action', 'expand') | Out-Null
        $options = Read-ContextMenu 'Last 24 hours' -Types @('ListItem')
        $preset = @($options.Items | Where-Object name -eq 'Last 24 hours')[0]
        Ui @('invoke', $preset.selector, '--action', 'select') -Window $options.Window | Out-Null
        Primary
        Assert-ModificationResults 'modified:24h' $fixtureName

        $today = Get-Date -Format 'yyyy-MM-dd'
        $custom = "modified:$today..$today"
        Fill 'Search registry' "Counter $custom"
        $search = Pick 'Edit' 'Search registry'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $search.selector) | Out-Null
        Assert-ModificationResults "Counter $custom" 'Counter'

        Press 'Advanced search'
        Wait-Ui {
            param($items)
            $items.automationId -contains 'SearchModifiedFrom' -and $items.automationId -contains 'SearchModifiedThrough' -and
                @($items | Where-Object { $_.automationId -eq 'QueryInput' -and $_.value -eq 'Counter' }).Count -eq 1
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'key-modified-custom-range.png')) | Out-Null
        Primary
        Assert-ModificationResults "Counter $custom" 'Counter'

        Ui @('invoke', 'EditAddressButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'OmniboxInput' }
        Ui @('set-value', 'OmniboxInput', "?Counter modified:$today..") | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Search this branch' }
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Assert-ModificationResults "Counter modified:$today.." 'Counter'
    }

    Test-Ui 'Notes sidebar uses contextual commands and the full editor width' {
        function Assert-ContextualNotes {
            Wait-Ui { param($items) $items.name -contains 'Local note' -and $items.automationId -contains 'ToolSelector' }
            $items = @(Read-Ui)
            foreach ($name in 'New note…','Show details','Hide details','List view') {
                if ($items.name -contains $name) { throw "Page-only action '$name' leaked into the Notes sidebar." }
            }
            if (@($items | Where-Object automationId -in @('InspectDetails','NotesEdit','NotesOpen','NotesCopy')).Count) {
                throw 'The sidebar exposes collection-page commands.'
            }
            $editor = @($items | Where-Object { $_.type -eq 'Edit' -and $_.name -eq 'Local note' })[0]
            $selector = @($items | Where-Object automationId -eq 'ToolSelector')[0]
            if ($editor.x -gt $selector.x + 8) { throw 'Inspector padding leaves a redundant gutter in the Notes sidebar.' }
        }
        Browser; Press 'Notes for this key'
        Assert-ContextualNotes
        Fill 'Local note' 'Contextual sidebar note'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteStatus' -and $_.name -eq 'Saved' }).Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'contextual-notes-sidebar.png')) | Out-Null
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        Wait-Ui {
            param($items)
            $items.name -contains 'New note…' -and $items.automationId -contains 'InspectDetails' -and
                @($items | Where-Object { $_.automationId -in @('NotesEdit','NotesOpen','NotesCopy') -and $_.isEnabled }).Count -eq 3
        }
        if (@(Read-Ui | Where-Object automationId -eq 'NotesCurrentKeyFilter').Count) {
            throw 'The detached organizer still shows a current-key filter.'
        }
        Set-Preview $false
        Fill 'Search tool items' 'no matching note here'
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -in 'InspectDetails','NotesEdit','NotesOpen','NotesCopy' -and -not $_.isEnabled }).Count -eq 4
        }
        $bar = (Read-Ui | Where-Object automationId -eq 'Commands')
        $more = Elements $bar.children | Where-Object automationId -eq 'MoreButton'
        Ui @('invoke', $more.selector, '--action', 'invoke') | Out-Null
        $menu = Read-ContextMenu -Expected 'Delete note' -Types @('Button','MenuItem')
        if (($menu.Items | Where-Object name -eq 'Delete note').isEnabled) { throw 'Delete is enabled for a filtered-out note.' }
        Close-ContextMenu $menu
        Fill 'Search tool items' ''
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NotesEdit' -and $_.isEnabled }).Count -eq 1 }
        Ui @('invoke', 'NotesEdit', '--action', 'invoke') | Out-Null
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Contextual sidebar note' }).Count -eq 1
        }
        Set-Preview $false
        Press 'Show in panel'
        Assert-ContextualNotes
        if ((Pick 'Edit' 'Local note').value -ne 'Contextual sidebar note') { throw 'Returning from the collection page hid or lost the contextual note.' }
    }

    Test-Ui 'Full tool pages use the same toolbar spacing and alignment' {
        function Read-ToolbarLayout([string] $Page) {
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'Heading' -and $_.name -eq $Page }).Count -eq 1 }
            $items = @(Read-Ui)
            $header = $items | Where-Object automationId -eq 'DescriptionText' | Select-Object -First 1
            if (-not $header) { throw "$Page is missing its header description." }
            $bar = $items | Where-Object { $_.type -eq 'AppBar' -and $_.y -ge ($header.y + $header.height) } | Select-Object -First 1
            if (-not $bar) { throw "$Page is missing its toolbar." }
            $controls = @(Elements $bar.children | Where-Object { $_.type -in 'Button','CheckBox','Edit','ComboBox','SplitButton' -and $_.height -gt 0 })
            if (-not $controls.Count) { throw "$Page has no visible toolbar controls." }
            foreach ($control in $controls) {
                if ([Math]::Abs(($control.y + $control.height / 2) - ($bar.y + $bar.height / 2)) -gt 1) {
                    throw "The '$($control.name)' control on $Page is off the toolbar centerline."
                }
            }
            $screenshot = ($Page -replace '[^a-zA-Z0-9]+', '-').ToLowerInvariant() + '-toolbar.png'
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory $screenshot)) | Out-Null
            @{ Gap = $bar.y - ($header.y + $header.height); Height = $bar.height; Left = $bar.x }
        }

        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Dark' }
        Browser
        Press 'Notes for this key'
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        $layouts = [ordered]@{ Notes = Read-ToolbarLayout 'Notes' }
        foreach ($page in 'Favorites', 'Watches', 'Change journal', 'Saved searches', 'Workspaces', 'Registry Usage') {
            Browser
            Open-Menu $page -Group 'Tools' -MainTab:($page -in 'Favorites', 'Watches', 'Change journal')
            $layouts[$page] = Read-ToolbarLayout $page
        }
        Browser
        Fill 'Search registry' 'needle'
        Start-SearchInMainTab
        Wait-Ui { param($items) $items.automationId -contains 'SearchResultFilter' }
        $layouts['Search results'] = Read-ToolbarLayout 'Find: needle'
        $layouts | ConvertTo-Json | Set-Content (Join-Path $ArtifactDirectory 'toolbar-layout.json')
        foreach ($page in $layouts.Keys) {
            foreach ($dimension in 'Gap', 'Height', 'Left') {
                if ([Math]::Abs($layouts[$page][$dimension] - $layouts.Notes[$dimension]) -gt 1) {
                    throw "$page toolbar ${dimension}: $($layouts[$page][$dimension]) px, Notes: $($layouts.Notes[$dimension]) px."
                }
            }
        }
    }

    Test-Ui 'Notes share drafts across panel and page and survive view closure' {
        Browser
        Press 'Notes for this key'
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' }
        Fill 'Local note' 'Investigation fixture draft'
        Wait-Ui { param($items) $items.name -contains 'Saved' }
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        if ((Pick 'Edit' 'Local note').value -ne 'Investigation fixture draft') { throw 'Draft did not transfer to the tab.' }
        Fill 'Local note' 'Investigation fixture note'
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (@($saved.Notes | Where-Object { $_.Location.SubKey -eq $subKey -and $_.Text -eq 'Investigation fixture note' }).Count -ne 1) { throw 'Note was not persisted.' }
        Select-Row $address | Out-Null
        $items = @(Read-Ui)
        $count = $items | Where-Object automationId -eq 'NotesStatusText'
        $status = $items | Where-Object automationId -eq 'NoteStatus'
        if ($status.name -ne 'Saved' -or [Math]::Abs($count.y - $status.y) -gt 1 -or $status.x -le $count.x) {
            throw 'The saved state is not on the right side of the note count in the status bar.'
        }
        if ($items.name -notcontains 'Note type' -or $items.name -notcontains 'Key note' -or
            $items.name -notcontains 'Registry' -or $items.name -contains 'Matches') {
            throw 'The selected key note metadata is missing or includes a matching rule.'
        }
        $registry = $items | Where-Object name -eq 'This computer · 64-bit registry'
        $editor = $items | Where-Object automationId -eq 'NoteEditor'
        if (-not $registry -or [Math]::Abs(($registry.x + $registry.width) - ($editor.x + $editor.width)) -gt 1) {
            throw 'The Notes metadata does not align with the right edge of the editor.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'notes.png')) | Out-Null
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Fill 'Local note' 'Retry fixture note'
            Wait-Ui { param($items) $items.automationId -contains 'RetryNoteSave' }
            $items = @(Read-Ui)
            $retry = $items | Where-Object automationId -eq 'RetryNoteSave'
            $count = $items | Where-Object automationId -eq 'NotesStatusText'
            if ([Math]::Abs(($retry.y + $retry.height / 2) - ($count.y + $count.height / 2)) -gt 1) {
                throw 'Retry is outside the Notes status bar.'
            }
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'notes-save-error.png')) | Out-Null
        } finally { $locked.Dispose() }
        Ui @('invoke', 'RetryNoteSave') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'RetryNoteSave' -and $items.name -contains 'Saved' }
        Fill 'Local note' 'Investigation fixture note'
        Wait-Ui { param($items) $items.name -contains 'Saved' }
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.automationId -contains 'EntriesList' }
        if ((Pick 'Edit' 'Local note').value -ne 'Investigation fixture note') { throw 'Panel did not share the saved note.' }
        $focused = (Ui @('get-focused', '--json')) -join "`n"
        if ($focused -notmatch 'NoteEditor') { throw 'Returning to the panel did not focus the note editor.' }
        Fill 'Local note' 'Draft before close'
        Press 'Close tool panel'; Press 'Notes for this key'
        if ((Pick 'Edit' 'Local note').value -ne 'Draft before close') { throw 'Closing the panel lost the draft.' }
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Fill 'Local note' 'Draft after close'
            Wait-Ui { param($items) $items.name -contains 'Could not complete the action' }
            if ((Pick 'Edit' 'Local note').value -ne 'Draft after close') { throw 'Failed save lost the draft.' }
            $appProcess.CloseMainWindow() | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Unsaved notes' }
            Press 'Cancel'
            if ($appProcess.HasExited) { throw 'Cancel closed the window.' }
        } finally { $locked.Dispose() }
        Fill 'Local note' 'Investigation fixture note'; Press 'Close tool panel'
        Go-ToKey "$address\Child"
        $notes = Pick 'TabItem' 'Notes' -All
        Ui @('invoke', $notes.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Investigation fixture note' }).Count -eq 1 }
        $noteKeyTab = Middle-Open (Pick 'Button' 'Open key') $fixtureName
        if ((Read-Ui).automationId -notcontains 'NoteEditor' -or
            @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Notes' }).Count -ne 1) {
            throw 'Middle-clicking Open key dismissed the source Notes page.'
        }
        Close-MiddleTab $noteKeyTab
        if ((Read-Ui).automationId -notcontains 'NoteEditor') { throw 'Closing the middle-clicked key tab dismissed the source note editor.' }
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Counter*' }).Count -eq 1 }
        if ((Pick 'Edit' 'Local note').value -ne 'Investigation fixture note') { throw 'Returning an older note to the panel lost its key or draft.' }
        if ((Read-Ui).name -notcontains 'Saved') { throw 'The note save state is hidden by its long registry path.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'notes-panel.png')) | Out-Null
        Press 'Close tool panel'
    }

    Test-Ui 'Workspace preview cancel and partial Replace retain drafts running watches and usable tabs' {
        Browser
        $counter = Select-Row 'Counter'
        Ui @('click', $counter.selector, '--right') | Out-Null
        $menu = Read-ContextMenu 'Watch value'
        Ui @('invoke', ($menu.Items | Where-Object name -eq 'Watch value').selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Start-WatchPreview $address 'Counter'
        Wait-Ui { param($items) $items.automationId -contains 'RunningWatches' }
        Open-Menu 'Close tab'; Browser
        Press 'Notes for this key'
        $previousNote = (Pick 'Edit' 'Local note').value
        Fill 'Local note' 'Workspace preview pending draft'
        Press 'Close tool panel'
        $childName = 'WorkspacePartialTarget'
        $childPath = "$subKey\$childName"
        [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($childPath).Dispose()
        Ui @('send-keys', 'ctrl+t', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Go-ToKey "$address\$childName"
        Open-Menu 'Workspaces' -Group 'Tools'; Press 'Save workspace…'
        Fill 'Workspace name' 'Phase7 preview workspace'; Primary
        Wait-Ui { param($items) $items.name -contains 'Workspace saved' }
        Select-Row 'Phase7 preview workspace' | Out-Null
        $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Press 'Open workspace'
        Wait-Ui { param($items) $items.automationId -contains 'WorkspaceReplaceTabs' -and $items.automationId -contains 'WorkspaceStartInvestigations' }
        $preview = (Read-Ui -All | ForEach-Object name) -join "`n"
        if (-not $preview.Contains("$address\$childName") -or -not $preview.Contains('Counter') -or -not $preview.Contains('64-bit')) { throw 'Workspace preview omitted its actual tab, view or named watch target.' }
        Press 'Cancel'
        Wait-Ui { param($items) $items.automationId -notcontains 'WorkspaceReplaceTabs' }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $count -or (Read-Ui).automationId -notcontains 'RunningWatches') { throw 'Canceling preview changed tabs or ended the running watch.' }
        Browser; Press 'Notes for this key'
        if ((Pick 'Edit' 'Local note').value -cne 'Workspace preview pending draft') { throw 'Canceling preview discarded the note draft.' }
        Press 'Close tool panel'
        $parent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try { $parent.DeleteSubKeyTree($childName, $false) } finally { $parent.Dispose() }
        Open-Menu 'Workspaces' -Group 'Tools'; Select-Row 'Phase7 preview workspace' | Out-Null
        Press 'Open workspace'; Confirm-WorkspacePreview -Replace -Start
        Wait-Ui { param($items) $items.name -contains "Some workspace items couldn't be opened" -and @($items | Where-Object name -like "*Tab:*${childName}*").Count -gt 0 }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -le $count -or (Read-Ui).automationId -notcontains 'RunningWatches') { throw 'Partial Replace removed usable tabs or failed to add its staged successes.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'workspace-partial-preserved.png')) | Out-Null
        Browser; Press 'Notes for this key'
        if ((Pick 'Edit' 'Local note').value -cne 'Workspace preview pending draft') { throw 'Failed Replace discarded the note draft.' }
        Fill 'Local note' $previousNote
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Press 'Close tool panel'
        Ui @('invoke', (Pick 'TabItem' $childName).selector, '--action', 'select') | Out-Null
        Open-Menu 'Close tab'; Browser
    }

    Test-Ui 'Workspace persists tabs searches and watched values' {
        Browser
        $row = Select-Row 'Counter'
        Ui @('click', $row.selector, '--right') | Out-Null
        $menu = Read-ContextMenu
        Ui @('invoke', ($menu.Items | Where-Object name -eq 'Watch value').selector, '--action', 'invoke') -Window $menu.Window | Out-Null
        Start-WatchPreview $address 'Counter'
        Wait-Ui { param($items) @($items | Where-Object name -like '*Watching*').Count -gt 0 }
        Browser; Fill 'Search registry' 'workspace fixture query'; Start-SearchInMainTab
        Wait-Ui { param($items) $items.name -contains 'No matches found' }
        $searchTabs = @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -like 'Find: *' } | ForEach-Object name | Sort-Object)
        Open-Menu 'Workspaces' -Group 'Tools'; Press 'Save workspace…'
        Fill 'Workspace name' 'UI fixture workspace'; Primary
        Wait-Ui { param($items) $items.name -contains 'Workspace saved' }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $workspace = @($saved.Workspaces | Where-Object Name -eq 'UI fixture workspace')[0]
        $savedSearchTabs = @($workspace.Searches | ForEach-Object { 'Find: ' + $_.Options.Text } | Sort-Object)
        if (-not $workspace -or $workspace.Tabs.Count -lt 1 -or ($savedSearchTabs -join "`n") -cne ($searchTabs -join "`n") -or @($workspace.Watches | Where-Object { $_.Location.SubKey -eq $subKey -and $_.ValueName -ceq 'Counter' }).Count -ne 1) { throw 'Workspace omitted its investigation state.' }
        Select-Row 'UI fixture workspace' | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'workspaces.png')) | Out-Null
        Press 'Open workspace'
        Confirm-WorkspacePreview -Start
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Find: workspace fixture query' }).Count -eq 2 } -All
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'workspace.png')) | Out-Null
    }

    Test-Ui 'Default value watch starts unset and retains creation changes and deletion' {
        Browser; Press 'Refresh'
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '(Default),*(value not set)*' }).Count -eq 1 }
        $row = Select-Row '(Default)'
        Ui @('click', $row.selector, '--right') | Out-Null
        $windows = Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json
        $popup = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.label -eq 'popup' } | Sort-Object height -Descending)[0]
        $item = Pick 'MenuItem' 'Watch value' -Window $popup.hwnd
        Ui @('invoke', $item.selector, '--action', 'invoke') -Window $popup.hwnd | Out-Null
        Start-WatchPreview $address '(Default)'
        Wait-Ui {
            param($items)
            if ($items.name -contains 'Select a stored value to watch.') { throw 'The unset default value was rejected by Watch value.' }
            $items.automationId -contains 'WatchPicker' -and @($items | Where-Object name -like '*Watching*').Count -gt 0
        }
        Fill 'Search tool items' ''
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey, $true)
        try {
            $key.SetValue('OtherWatchedValue', 'unrelated')
            $child = $key.OpenSubKey('Child', $true)
            try { $child.SetValue('', 'child default') } finally { $child.Dispose() }
            $key.SetValue('', 'default before')
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Value added: (Default),*' }).Count -eq 1 }
            $key.SetValue('', 'default after')
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Data changed: (Default),*' }).Count -eq 1 }
            $key.DeleteValue('')
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Value removed: (Default),*' }).Count -eq 1 }
        } finally { $key.Dispose() }
        $rows = @(Read-Ui | Where-Object type -eq 'ListItem')
        if ($rows.Count -ne 3 -or @($rows | Where-Object name -notlike "*: (Default), $address*").Count) { throw 'Default value watch included unrelated values or subkeys.' }
        Select-Row 'Data changed: (Default)' | Out-Null
        $details = Read-Details
        if ($details -notlike '*default before*' -or $details -notlike '*default after*') { throw 'Default value watch lost its before or after data.' }
        Press 'Stop watching'
        Wait-Ui { param($items) @($items | Where-Object name -like '*Stopped*').Count -gt 0 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'default-value-watch.png')) | Out-Null
    }

    Test-Ui 'Settings exposes only the Data font and accepts its family and size at a narrow width' {
        Browser
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 780, 720, 0x16)) { throw 'Could not narrow Settings.' }
        try {
            if (@(Read-Ui -All | Where-Object name -eq 'Registry font').Count) { throw 'The removed Registry font setting is still exposed.' }
            $expander = Pick 'Button' 'Data column font'
            Ui @('invoke', $expander.selector, '--action', 'expand') | Out-Null
            Wait-Ui { param($items) $items.automationId -contains 'DataFontSelector' }
            if ($null -eq (Get-Content $settingsFile -Raw | ConvertFrom-Json).DataFont) {
                Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'CustomDataFontSwitch') | Out-Null
            }
            $family = Pick 'ComboBox' 'Data column font family'
            Set-TestWindowForeground
            [UxTestWindow]::LeftClickAt([int]($family.x + 20), [int]($family.y + $family.height / 2))
            Ui @('send-keys', 'ctrl+a', '--via', 'send-input') | Out-Null
            Ui @('send-keys', 'Courier New', '--verbatim', '--via', 'send-input') | Out-Null
            Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
            Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).DataFont.Family -eq 'Courier New' }
            $size = (Get-Content $settingsFile -Raw | ConvertFrom-Json).DataFont.Size + 1
            $sizeBox = @(Read-Ui -All | Where-Object automationId -eq 'DataFontSizeBox')[0]
            $up = @(Elements $sizeBox.children | Where-Object automationId -eq 'UpSpinButton')[0]
            Ui @('invoke', $up.selector, '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).DataFont.Size -eq $size }
            Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
            Ui @('send-keys', 'down', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
            Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Light' }
        } finally { [void][UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16) }
        Browser
    }

    Test-Ui 'Settings tab reuses cards and synchronizes saved preferences with View' {
        function Set-PreferenceSwitch([string] $Id, [string] $Property, [bool] $Value) {
            $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            if ($saved.$Property -ne $Value) { Ui @('send-keys', 'space', '--via', 'send-input', '--target', $Id) | Out-Null }
            Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).$Property -eq $Value }
        }
        Browser
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Open-Menu 'Settings'
        if (@(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -ne 1) { throw 'Settings opened duplicate tabs.' }

        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Wait-Ui { param($items) (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Dark' }
        Set-PreferenceSwitch 'ShowKeysSwitch' 'ShowKeysInList' $true
        Set-PreferenceSwitch 'ParentKeySwitch' 'ShowParentKeyInList' $true
        Set-PreferenceSwitch 'ValuePreviewSwitch' 'ShowValuePreview' $true
        Set-PreferenceSwitch 'ShowKeysSwitch' 'ShowKeysInList' $false
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'ParentKeySwitch' -and -not $_.isEnabled }).Count -eq 1 } -All
        if (-not (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ShowParentKeyInList) { throw 'Hiding keys erased the parent-row preference.' }

        $before = [IO.File]::ReadAllText($settingsFile)
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Ui @('send-keys', 'space', '--via', 'send-input', '--target', 'StatusBarSwitch') | Out-Null
            Wait-Ui { param($items) $items.name -contains 'Settings could not be saved' } -All
            if ([IO.File]::ReadAllText($settingsFile) -cne $before) { throw 'Failed preference save replaced settings.' }
        } finally { $locked.Dispose() }
        Set-PreferenceSwitch 'StatusBarSwitch' 'ShowStatusBar' $false

        Browser
        View-Option 'Show keys in list'
        Open-Menu 'Settings'
        Wait-Ui { param($items) ($items | Where-Object automationId -eq 'ParentKeySwitch').isEnabled } -All
        Set-PreferenceSwitch 'ShowKeysSwitch' 'ShowKeysInList' $false
        Browser
        if (@(Read-Ui | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Child,*' }).Count) { throw 'Settings did not update the open registry tab.' }
        Open-Menu 'Settings'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'settings.png')) | Out-Null
        Open-Menu 'Close tab'
        Open-Menu 'Settings'
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        if ((Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).Theme -ne 'Dark') { throw 'Closing Settings lost the selected theme.' }
    }

    Test-Ui 'Favorites organizer shows real folders without a synthetic root' {
        Open-Menu 'Favorites' -Group 'Tools' -MainTab
        Fill 'Search tool items' ''
        Show-RootFavorites
        if (@((Read-Ui) | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Favorites' }).Count) {
            throw 'The organizer still exposes the synthetic Favorites folder.'
        }
        Press 'Add favorite…'
        Fill 'Name' 'Unfiled root favorite'
        Fill 'Registry location' $address
        Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Unfiled root favorite*' }).Count -eq 1 }
        Press 'New folder'; Fill 'Name' 'Top folder'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoritesFolderTitle' -and $_.name -eq 'Top folder' }).Count -eq 1 }
        Press 'New folder'; Fill 'Name' 'Nested folder'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Nested folder' }).Count -eq 1 }
        Show-RootFavorites
        foreach ($name in 'Top folder', 'Nested folder', 'Top folder', 'Nested folder') {
            Ui @('click', (Pick 'TreeItem' $name).selector) | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoritesFolderTitle' -and $_.name -eq $name }).Count -eq 1 }
            Assert-TreeSelection $name
        }
        Show-RootFavorites
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Unfiled root favorite*' }).Count -eq 1 }
        $items = @(Read-Ui)
        if ($items.automationId -contains 'FavoritesRoot' -or @($items | Where-Object { $_.type -eq 'TreeItem' -and $_.name -eq 'Favorites' }).Count) {
            throw 'Returning to the top level left a redundant root entry or navigation link.'
        }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-without-root.png')) | Out-Null
    }

    Test-Ui 'Standalone Favorites use explicit locations and responsive shared surfaces' {
        function Show-FavoritesOrganizer {
            $existing = @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Favorites' })
            if ($existing.Count) { Ui @('invoke', $existing[0].selector, '--action', 'select') | Out-Null }
            else { Open-Menu 'Favorites' -Group 'Tools' -MainTab }
            Wait-Ui { param($items) $items.automationId -contains 'ShowInPanelButton' }
        }
        Browser
        Show-FavoritesOrganizer
        Fill 'Search tool items' ''
        Show-RootFavorites
        Press 'New folder'
        Wait-Ui {
            param($items)
            $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
            $items.automationId -contains 'PrimaryButton' -and $focused.hasFocus -and $focused.element.name -eq 'Name'
        }
        Ui @('send-keys', 'Explicit locations', '--verbatim', '--via', 'send-input') | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Press 'Add favorite…'
        if ((Pick 'Edit' 'Registry location').value) { throw 'Standalone favorite silently inherited a browser location.' }
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if (-not $focused.hasFocus -or $focused.element.name -ne 'Registry location') { throw 'A new standalone favorite did not focus its location.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorite-dialog.png')) | Out-Null
        Fill 'Name' 'Entered child location'
        Fill 'Registry location' 'not a registry path'
        Primary
        Wait-Ui { param($items) $items.automationId -contains 'DialogError' -and $items.automationId -contains 'PrimaryButton' }
        if ((Pick 'Edit' 'Name').value -cne 'Entered child location' -or (Pick 'Edit' 'Registry location').value -cne 'not a registry path') { throw 'A failed favorite lost its entered fields.' }
        Fill 'Registry location' ($address + '\Child')
        Ui @('invoke', (Pick 'RadioButton' '32-bit').selector, '--action', 'select') | Out-Null
        Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $favorite = @($saved.Favorites | Where-Object Name -eq 'Entered child location')[0]
        $folder = @($saved.FavoriteFolders | Where-Object Name -eq 'Explicit locations')[0]
        if ($favorite.Address -ne "$address\Child" -or -not $favorite.Is32Bit -or $favorite.FolderId -ne $folder.Id) { throw 'Explicit favorite ignored the entered location, registry view, or selected folder.' }
        Select-Row 'Entered child location' | Out-Null
        if ((Read-Ui).name -contains 'Folder: Favorites / Explicit locations') { throw 'Optional bookmark details are initially visible.' }
        Set-Preview $true
        Wait-Ui { param($items) @($items | Where-Object name -like '*Folder: Favorites / Explicit locations*').Count -eq 1 }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-organizer-dark.png')) | Out-Null
        Fill 'Search tool items' 'Entered child'
        if ((Read-Ui).automationId -notcontains 'FavoriteTree') { throw 'Global search hid the folder tree.' }
        Fill 'Search tool items' ''
        if ((Read-Ui).name -notcontains 'Explicit locations') { throw 'Clearing search lost the selected folder.' }
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 800, 900, 0x16)) { throw 'Could not narrow the fixture window.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-organizer-narrow.png')) | Out-Null
        if ((Read-Ui).automationId -notcontains 'FavoriteTree' -or (Read-Ui).name -notcontains 'Add favorite…') { throw 'Narrow organizer lost hierarchy or creation actions.' }
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not restore fixture width.' }

        foreach ($tab in @(Read-Ui -All | Where-Object { $_.type -eq 'TabItem' -and $_.name -ne 'Favorites' })) {
            Ui @('invoke', $tab.selector, '--action', 'select') | Out-Null
            Open-Menu 'Close tab'
        }
        Press 'Add favorite…'
        if ((Pick 'Edit' 'Registry location').value) { throw 'Favorite entry depends on an implicit context with no registry tab.' }
        Fill 'Name' 'No browser context'; Fill 'Registry location' $address; Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        Press 'Add favorite…'; Fill 'Name' 'Canceled bookmark'; Fill 'Registry location' $address; Press 'Cancel'
        if (@((Get-Content $settingsFile -Raw | ConvertFrom-Json).Favorites | Where-Object Name -eq 'Canceled bookmark').Count) { throw 'Cancel persisted a bookmark.' }
        Select-Row 'No browser context' | Out-Null
        Press 'Open'
        Wait-Ui { param($items) $items.name -contains 'Registry keys and values' }
        Open-Menu 'Settings'
        Ui @('send-keys', 'home', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Ui @('send-keys', 'down', '--via', 'send-input', '--target', 'ThemeSelector') | Out-Null
        Wait-Ui { param($items) (Get-Content $settingsFile -Raw | ConvertFrom-Json).Theme -eq 'Light' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'settings-light.png')) | Out-Null
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 720, 900, 0x16)) { throw 'Could not narrow Settings.' }
        Wait-Ui { param($items) $items.automationId -contains 'ThemeSelector' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'settings-narrow.png')) | Out-Null
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not restore Settings width.' }
        Show-FavoritesOrganizer
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'favorites-organizer-light.png')) | Out-Null
    }
    Test-Ui 'Enter activates registry values keys and parent rows' {
        Browser
        $value = Select-Row 'Counter'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $value.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'PrimaryButton' }
        $items = Read-Ui
        if (@($items | Where-Object { $_.automationId -eq 'DialogHeading' -and $_.name -eq 'Edit value Counter' }).Count -ne 1) {
            throw 'Enter opened the wrong registry value.'
        }
        if ($items.automationId -notcontains 'DialogHelpButton') { throw 'The value editor is missing its Help button.' }
        Ui @('invoke', 'DialogDismissButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'DialogDismissButton' }
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if (-not $saved.ShowKeysInList) { View-Option 'Show keys in list' }
        if (-not $saved.ShowParentKeyInList) { View-Option 'Show parent key ..' }
        Press 'Filter'
        Wait-Ui { param($items) $items.automationId -contains 'FilterBox' }
        $child = Select-Row 'Child, Key,'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $child.selector) | Out-Null
        Wait-Ui { param($items) $items.name -contains ".., Parent key, $address" }
        if ((Read-Ui).automationId -contains 'FilterBox') { throw 'Navigating to another key left its empty filter open.' }
        $parent = Select-Row ".., Parent key, $address"
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $parent.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*' }).Count -eq 1 }
        $value = Select-Row 'Counter'
        Ui @('send-keys', 'f2', '--via', 'send-input', '--target', $value.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'InlineEditor' }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'InlineEditor') | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'InlineEditor' }
        Press 'Filter'; Fill 'Filter this key' 'Counter'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
        if ((Read-Ui).automationId -contains 'PrimaryButton') { throw 'Enter in the filter activated a registry value.' }
        Ui @('send-keys', 'esc', '--via', 'send-input', '--target', 'FilterBox') | Out-Null
        if (-not $saved.ShowParentKeyInList) { View-Option 'Show parent key ..' }
        if (-not $saved.ShowKeysInList) { View-Option 'Show keys in list' }
    }

    Test-Ui 'Enter opens Favorites once and focuses the selected note editor' {
        Browser
        Open-Menu 'Favorites' -Group 'Tools' -MainTab
        Fill 'Search tool items' ''; Show-RootFavorites
        Press 'Add favorite…'; Fill 'Name' 'Enter fixture favorite'; Fill 'Registry location' ($address + '\Child'); Primary
        Wait-Ui { param($items) $items.automationId -notcontains 'PrimaryButton' }
        $favorite = Select-Row 'Enter fixture favorite'
        $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $favorite.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq $count + 1 } -All
        Expect-HistoryLocation ($address + '\Child')
        Ui @('invoke', (Pick 'TabItem' 'Favorites').selector, '--action', 'select') | Out-Null
        Press 'Show in panel'
        if ((Read-Ui).name -contains 'Unpin tool panel') { Press 'Unpin tool panel' }
        $favorite = Select-Row 'Enter fixture favorite'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $favorite.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -notcontains 'ToolSelector' }
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $count + 1) { throw 'Enter in the Favorites panel opened an extra tab.' }
        Browser; Press 'Notes for this key'; Fill 'Local note' 'Enter fixture note'
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        $note = Select-Row $address
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $note.selector) | Out-Null
        if (((Ui @('get-focused', '--json')) -join "`n") -notmatch 'NoteEditor') { throw 'Enter did not focus the selected note editor.' }
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        if ((Pick 'Edit' 'Local note').value -cnotmatch '^Enter fixture note(?:\r\n|\r|\n)\z') { throw 'Enter did not append exactly one line break to the note.' }
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' }
    }

    Test-Ui 'Enter runs the selected search result saved search and workspace' {
        Browser; Fill 'Search registry' 'needle'; Start-SearchInMainTab
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 }
        $result = Select-Row 'NeedleOne'
        Ui @('send-keys', 'ctrl+a', '--via', 'send-input', '--target', $result.selector) | Out-Null
        if ((Pick 'Button' 'Open selected result').isEnabled) { throw 'Multiple search results unexpectedly allow activation.' }
        $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $result.selector) | Out-Null
        if (@(Read-Ui -All | Where-Object type -eq 'TabItem').Count -ne $count) { throw 'Enter opened multiple selected results.' }
        Press 'Save search'; Fill 'Search name' 'Enter fixture search'; Primary
        Wait-Ui { param($items) $items.name -contains 'Search saved' }
        $result = Select-Row 'NeedleOne'
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $result.selector) | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'EntriesList' -and @($items | Where-Object type -eq 'TabItem').Count -eq $count + 1 } -All
        $opened = Pick 'ListItem' 'NeedleOne, REG_SZ, needle one'
        $selection = Ui @('get-property', $opened.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json
        if ($selection.properties.IsSelected -ne 'True') { throw 'Enter lost the search result value selection.' }
        Open-Menu 'Saved searches' -Group 'Tools'
        $search = Select-Row 'Enter fixture search'
        $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $search.selector) | Out-Null
        Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $count + 1 -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Needle*' }).Count -eq 2 } -All
        Open-Menu 'Workspaces' -Group 'Tools'; Press 'Save workspace…'; Fill 'Workspace name' 'Enter fixture workspace'; Primary
        Wait-Ui { param($items) $items.name -contains 'Workspace saved' }
        $workspace = @((Get-Content $settingsFile -Raw | ConvertFrom-Json).Workspaces | Where-Object Name -eq 'Enter fixture workspace')[0]
        $row = Select-Row 'Enter fixture workspace'
        $count = @(Read-Ui -All | Where-Object type -eq 'TabItem').Count
        Ui @('send-keys', 'enter', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Confirm-WorkspacePreview -Start
        Wait-Ui { param($items) @($items | Where-Object type -eq 'TabItem').Count -eq $count + $workspace.Tabs.Count + $workspace.Searches.Count } -All
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'enter-workspace.png')) | Out-Null
    }
    Test-Ui 'Native permissions sheet identifies the chosen key and cancels' {
        Browser
        $button = Pick 'Button' 'Permissions…'
        Ui @('click', $button.selector) | Out-Null
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $windows = @(Invoke-WinAppChecked @('ui', 'list-windows', '--json') | ConvertFrom-Json)
            $sheet = @($windows | Where-Object { $_.processId -eq $appProcess.Id -and $_.className -eq '#32770' -and $_.title -like '*Permissions*' })[0]
            if ($sheet) { break }
            $notice = @(Read-Ui | Where-Object name -eq "Couldn't open permissions")
            if ($notice.Count) { throw 'Windows could not open the permissions sheet. See the app log.' }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 15)
        if (-not $sheet -or -not $sheet.title.Contains($address)) { throw 'The permissions sheet did not identify the chosen key.' }
        $cancel = Pick 'Button' 'Cancel' -Window $sheet.hwnd
        Ui @('invoke', $cancel.selector, '--action', 'invoke') -Window $sheet.hwnd | Out-Null
    }

    Test-Ui 'Bulk watch refresh and live append preserve selection and scroll' {
        Browser
        $watchKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\BulkWatch")
        try {
            Go-ToKey "$address\BulkWatch"
            Press 'Watch this branch'; Start-WatchPreview "$address\BulkWatch"
            Wait-Ui { param($items) @($items | Where-Object name -like 'Watching*').Count }
            foreach ($n in 1..80) { $watchKey.SetValue(('Bulk observation {0:D3}' -f $n), "value $n") }
            Wait-Ui { param($items) $items.name -contains '80 items' -and @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Bulk observation 080*' }).Count -eq 1 }
            Ui @('scroll', (Pick 'List' 'Tool items').selector, '--to', 'top') | Out-Null
            Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*Bulk observation 001*' }).Count -eq 1 }
            Select-Row 'Bulk observation 001' | Out-Null
            $viewport = Get-ResultViewport 'Tool items' 'Bulk observation 001'
            Fill 'Search tool items' 'Bulk observation'
            Wait-Ui { param($items) $items.name -contains '80 items · 1 selected' }
            Assert-ResultViewport $viewport
            $watchKey.SetValue('Bulk observation 081', 'value 81')
            Wait-Ui { param($items) $items.name -contains '81 items · 1 selected' }
            Assert-ResultViewport $viewport
            if ((Read-Details) -notmatch '\bvalue 1\b') { throw 'Filtering or appending changed the selected observation.' }
            Fill 'Search tool items' 'Bulk observation 081'
            Wait-Ui { param($items) $items.name -contains '1 item' -and $items.name -contains 'Select an item' }
            Press 'Stop watching'
            Open-Menu 'Close tab'
            Go-ToKey $address
        } finally { $watchKey.Dispose() }
    }
    Test-Ui 'Browser columns resize by keyboard and selected long tabs keep their path and close button' {
        Browser; Go-ToKey $address
        $before = @(Read-Ui | Where-Object automationId -eq 'TypeSortButton')[0]
        $scale = (Get-Content -Raw -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') | ConvertFrom-Json).windows[0].scale
        if ($scale -le 0) { throw 'The selected window did not report a valid UI scale.' }
        Ui @('send-keys', 'right', '--via', 'send-input', '--target', 'NameResize') | Out-Null
        Wait-Ui { param($items) [Math]::Abs(@($items | Where-Object automationId -eq 'TypeSortButton')[0].x - $before.x - 20 * $scale) -le 2 }
        $header = @(Read-Ui | Where-Object automationId -eq 'TypeSortButton')[0]
        $row = Select-Row 'Counter'
        $type = @(Elements $row.children | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'REG_DWORD' })[0]
        if (-not $type -or [Math]::Abs($type.x - $header.x) -gt 2) { throw 'Resizing Name misaligned the Type header and realized value row.' }
        $dataBefore = @(Read-Ui | Where-Object automationId -eq 'DataSortButton')[0]
        $thumb = @(Read-Ui | Where-Object automationId -eq 'TypeResize')[0]
        $dragTo = '{0},{1}' -f [int][Math]::Round($thumb.x + $thumb.width / 2 + 24 * $scale), [int][Math]::Round($thumb.y + $thumb.height / 2)
        Ui @('drag', 'TypeResize', $dragTo) | Out-Null
        Wait-Ui { param($items) [Math]::Abs(@($items | Where-Object automationId -eq 'DataSortButton')[0].x - $dataBefore.x - 24 * $scale) -le 3 }
        $longName = 'Long layout tab ' + ('Header' * 35)
        $longAddress = "$address\$longName"
        [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\$longName").Dispose()
        Ui @('send-keys', 'ctrl+t', '--via', 'send-input', '--target', $row.selector) | Out-Null
        Go-ToKey $longAddress
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -ceq $longName }).Count -eq 1 }
        $tab = Pick 'TabItem' $longName
        if ($tab.width -gt 300 * $scale + 2) { throw 'A long selected title displaced the native tab close affordance.' }
        $help = Ui @('get-property', $tab.selector, '--property', 'HelpText', '--json') | ConvertFrom-Json
        if (-not ([string]$help.properties.HelpText).Contains($longAddress)) { throw 'The long tab did not expose its complete registry path through its tooltip.' }
        $close = @(Elements $tab.children | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq 'CloseButton' })
        if ($close.Count -ne 1 -or $close[0].isOffscreen -or $close[0].x + $close[0].width -gt $tab.x + $tab.width + 2) { throw 'The selected long tab has no reachable close button.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'browser-columns-long-tab.png')) | Out-Null
        Ui @('invoke', $close[0].selector, '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -notcontains $longName -and $items.automationId -contains 'EntriesList' }
        Browser
    }

    Test-Ui 'Window layout and loaded Favorites survive restart and a recoverable removal failure' {
        Browser
        $target = "$address\LayoutSaved"
        [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$subKey\LayoutSaved").Dispose()
        Go-ToKey $target
        $original = Pick 'TabItem' 'LayoutSaved'
        Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Fill 'Name' 'Layout state favorite'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        Ui @('send-keys', 'ctrl+t', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'LayoutSaved' }).Count -eq 2 -and @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        $other = @(Read-Ui | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'LayoutSaved' -and $_.selector -ne $original.selector })[0]
        $before = [IO.File]::ReadAllBytes($settingsFile)
        $locked = [IO.File]::Open($settingsFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
            Wait-Ui { param($items) $items.name -contains "Couldn't complete the operation" }
            if (-not ([Convert]::ToBase64String($before) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsFile))) -or (Pick 'Button' 'Remove favorite').automationId -ne 'FavoriteButton') { throw 'Failed removal changed the saved favorite or its loaded star.' }
        } finally { $locked.Dispose() }
        Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 }
        Ui @('invoke', $original.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 }
        Ui @('send-keys', 'ctrl+d', '--via', 'send-input', '--target', 'EntriesList') | Out-Null
        Fill 'Name' 'Layout state favorite'; Primary
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        Ui @('invoke', $other.selector, '--action', 'select') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        Open-Menu 'Close tab'
        Ui @('send-keys', 'right', '--via', 'send-input', '--target', 'NameResize') | Out-Null
        $savedNameWidth = @(Read-Ui | Where-Object automationId -eq 'NameSortButton')[0].width
        if ((Read-Ui).automationId -contains 'RegistryTree') { View-Option 'Navigation pane' }
        if ((Read-Ui).automationId -contains 'ToolSelector') { Press 'Close tool panel' }
        Tool-Option 'Right sidebar'
        Wait-Ui { param($items) $items.automationId -contains 'PinTools' -and $items.automationId -contains 'ToolSelector' }
        Ui @('invoke', 'PinTools', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Unpin tool panel' -and $items.automationId -contains 'ToolSelector' }
        Ui @('send-keys', 'left', '--via', 'send-input', '--target', 'ToolResize') | Out-Null
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $pattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($pattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) {
            $pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        }
        $layout = (Get-Content $settingsFile -Raw | ConvertFrom-Json).WindowSessions[-1].Layout
        if (-not $layout.PaneOpen -or -not $layout.PanePinned -or (Read-Ui).automationId -notcontains 'ToolSelector') {
            throw 'The pinned panel closed before the window was resized.'
        }
        $currentPlacement = $layout.Placement
        $normalWidth = [int][Math]::Min(900 * $currentPlacement.Scale, $currentPlacement.WorkArea.Width)
        $normalHeight = [int][Math]::Min(650 * $currentPlacement.Scale, $currentPlacement.WorkArea.Height)
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 80, 60, $normalWidth, $normalHeight, 0x14)) { throw 'Could not set the normal test window bounds.' }
        Wait-Ui { param($items) [Math]::Abs($root.Current.BoundingRectangle.Width - $normalWidth) -le 2 -and [Math]::Abs($root.Current.BoundingRectangle.Height - $normalHeight) -le 2 }
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.name -contains 'Unpin tool panel' }
        Press 'Unpin tool panel'; Press 'Pin tool panel'
        $layoutTimer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $normal = (Get-Content $settingsFile -Raw | ConvertFrom-Json).WindowSessions[-1].Layout
            $savedNormal = $normal.Placement.Bounds.Width -eq $normalWidth -and $normal.Placement.Bounds.Height -eq $normalHeight -and
                $normal.PaneOpen -and $normal.PanePinned -and $normal.Placement.WorkArea.Width -gt 0 -and $normal.Placement.Scale -gt 0
            if ($savedNormal) { break }
            Start-Sleep -Milliseconds 100
        } while ($layoutTimer.Elapsed.TotalSeconds -lt 5)
        @{ expectedWidth = $normalWidth; expectedHeight = $normalHeight; layout = $normal } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'saved-normal-layout.json')
        if (-not $savedNormal) { throw 'The window did not save normal bounds, work area, scale and pane preferences.' }
        $pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
        Wait-Ui { param($items) $pattern.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized }
        Restart-Editor
        $saved = (Get-Content $settingsFile -Raw | ConvertFrom-Json).WindowSessions[-1].Layout
        if (-not $saved.Placement.Maximized -or $saved.Placement.Bounds.Width -ne $normal.Placement.Bounds.Width -or $saved.Placement.Bounds.Height -ne $normal.Placement.Bounds.Height) { throw 'Maximized close overwrote the saved normal bounds.' }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainWindow)
        $pattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($pattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Maximized) { throw 'The saved maximized state was not restored.' }
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and $items.name -contains 'Unpin tool panel' -and @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Remove favorite' }).Count -eq 1 }
        $pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Wait-Ui { param($items) [Math]::Abs($root.Current.BoundingRectangle.Width - $normalWidth) -le 2 -and [Math]::Abs($root.Current.BoundingRectangle.Height - $normalHeight) -le 2 }
        Press 'Unpin tool panel'; Press 'Close tool panel'
        $restoredNameWidth = @(Read-Ui | Where-Object automationId -eq 'NameSortButton')[0].width
        if ([Math]::Abs($restoredNameWidth - $savedNameWidth) -gt 2) { throw "Restored Name column width changed: $savedNameWidth -> $restoredNameWidth" }
        if ((Read-Ui).automationId -contains 'RegistryTree') { throw 'The tab did not restore its hidden navigation pane.' }
        View-Option 'Navigation pane'
        Ui @('invoke', 'FavoriteButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'FavoriteButton' -and $_.name -eq 'Add favorite' }).Count -eq 1 }
        Go-ToKey $address
        if (-not [UxTestWindow]::SetWindowPos($mainWindow, 0, 0, 0, 1380, 900, 0x16)) { throw 'Could not restore the fixture width.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'window-layout-restored.png')) | Out-Null
    }
    Test-Ui 'Legacy tab and tool preferences remain readable without layout metadata' {
        Browser; Go-ToKey $address
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The editor did not close before the legacy fixture was written.' }
        $legacy = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $legacy.PSObject.Properties.Remove('WindowSessions')
        $legacy.Tabs = @(@{ Address = $address; Is32Bit = $false })
        $legacy | Add-Member -NotePropertyName 'ToolPanePinned' -NotePropertyValue $true -Force
        $legacy | Add-Member -NotePropertyName 'ToolPaneWidth' -NotePropertyValue 420 -Force
        $legacy | Add-Member -NotePropertyName 'SelectedTool' -NotePropertyValue 'Notes' -Force
        [IO.File]::WriteAllText($settingsFile, ($legacy | ConvertTo-Json -Depth 64))
        Restart-Editor
        Wait-Ui { param($items) $items.automationId -contains 'NoteEditor' -and $items.automationId -contains 'ToolSelector' -and $items.name -contains 'Unpin tool panel' }
        Expect-HistoryLocation $address
        if ((Read-SortStatus 'Name') -ne 'Sorted ascending') { throw 'Legacy tabs did not restore the default ascending Name sort.' }
        Press 'Unpin tool panel'; Press 'Close tool panel'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'legacy-layout-restored.png')) | Out-Null
    }
    Test-Ui 'Inspector previews one value note and opens other matches in the sidebar' {
        Browser
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The fixture editor did not close.' }
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -AsHashtable
        $hive = [int][Microsoft.Win32.RegistryHive]::CurrentUser
        $settings.Theme = 'Dark'
        $settings.ShowKeysInList = $true
        $destination = "$subKey\Child"
        $child = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($destination, $true)
        try { $child.SetValue('Counter', 2, [Microsoft.Win32.RegistryValueKind]::DWord) }
        finally { $child.Dispose() }
        $keyText = (1..5 | ForEach-Object { "Key note line $_" }) -join "`n"
        $settings.Notes = @(
            @{ Location = @{ Hive = $hive; SubKey = $subKey }; Text = $keyText },
            @{ Location = @{ Hive = $hive; SubKey = "$subKey\Child" }; Text = 'Child key note' },
            @{ Location = @{ Hive = $hive; SubKey = "$subKey\Child" }; ValueName = ''; Text = 'Child default note' },
            @{ Location = @{ Hive = $hive; SubKey = $subKey }; ValueName = 'Counter'; Text = 'Exact Counter note' },
            @{ Location = @{ Hive = $hive; SubKey = $subKey }; ValueName = 'Counter'; Text = 'Branch Counter note'; MatchRule = @{} },
            @{ Location = @{ Hive = $hive; SubKey = 'Software' }; ValueName = 'Counter'; Text = 'Parent Counter note'; MatchRule = @{} }
        )
        [IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 64))
        Restart-Editor
        Browser
        Select-Row 'Counter' | Out-Null
        if ((Read-Ui).automationId -notcontains 'ValueNameHeading') {
            Ui @('invoke', 'ToggleValueInspector', '--action', 'invoke') | Out-Null
        }
        Wait-Ui {
            param($items)
            $items.name -contains 'Exact Counter note' -and $items.name -contains '2 more shared notes' -and
                @($items | Where-Object { $_.automationId -eq 'KeyNoteExcerpt' -and $_.name -like 'Key note line 1*Key note line 5' -and $_.height -gt 65 }).Count -eq 1
        }
        $names = (Read-Ui).name
        if ($names -contains 'Branch Counter note' -or $names -contains 'Parent Counter note') { throw 'An additional note body is visible in the preview.' }
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-note-preview.png')) | Out-Null
        Ui @('hover', 'NoteExcerpt') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-note-edit-hover.png')) | Out-Null
        Ui @('hover', 'KeyNoteExcerpt') | Out-Null
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'key-note-edit-hover.png')) | Out-Null
        Ui @('hover', 'ValueNameHeading') | Out-Null
        Ui @('send-keys', 'tab', '--via', 'send-input', '--target', 'OpenByteInterpreter') | Out-Null
        $focused = Ui @('get-focused', '--json') | ConvertFrom-Json
        if ($focused.element.automationId -ne 'EditValueNote') { throw 'Tab did not reach the value note edit action.' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'value-note-edit-focus.png')) | Out-Null
        Ui @('invoke', 'MoreNotes', '--action', 'invoke') | Out-Null
        $menu = Read-ContextMenu 'Software and subkeys'
        if ($menu.Items.name -notcontains "$fixtureName and subkeys") { throw 'The menu is missing the other matching branch.' }
        Ui @('invoke', (Pick 'MenuItem' 'Software and subkeys' -Window $menu.Window).selector) -Window $menu.Window | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Parent Counter note' }).Count -eq 1 }
        Press 'Edit matching rule…' 'NoteCommands'
        Wait-Ui { param($items) $items.automationId -contains 'NoteRuleBranch' }
        $branch = (Pick 'Edit' 'Key or branch').value
        Fill 'Key or branch' 'Computer'
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and -not $_.isEnabled }).Count -eq 1 -and
                $items.name -contains 'Enter a registry key on the same computer.'
        }
        Fill 'Key or branch' $address
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and -not $_.isEnabled }).Count -eq 1 -and
                $items.name -contains 'A matching rule already exists for this value name and branch.'
        }
        Fill 'Key or branch' $branch
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and $_.isEnabled }).Count -eq 1 }
        Press 'Cancel'
        Wait-Ui { param($items) $items.automationId -notcontains 'NoteRuleBranch' }
        Press 'Close tool panel'
        Ui @('invoke', 'EditValueNote', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Exact Counter note' }).Count -eq 1 }
        Fill 'Local note' ' '
        Press 'Close tool panel'
        Wait-Ui { param($items) $items.name -contains 'Branch Counter note' -and $items.name -contains '1 more shared note' -and $items.automationId -contains 'AddValueNote' }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'shared-note-preview.png')) | Out-Null
        Ui @('invoke', 'EditMatchingNote', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Branch Counter note' }).Count -eq 1 }
        Ui @('invoke', 'ExpandToolButton', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'NotesStatusText' -and @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Branch Counter note' }).Count -eq 1 }
        Press 'Show in panel'
        Wait-Ui { param($items) $items.automationId -contains 'ToolSelector' -and @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Branch Counter note' }).Count -eq 1 }

        Press 'Edit matching rule…' 'NoteCommands'
        Wait-Ui { param($items) $items.automationId -contains 'NoteScopeChoice' }
        Ui @('send-keys', 'end', '--via', 'send-input', '--target', 'NoteScopeChoice') | Out-Null
        Wait-Ui {
            param($items)
            $items.automationId -notcontains 'NoteScopeConditions' -and
                @($items | Where-Object { $_.automationId -eq 'PrimaryButton' -and -not $_.isEnabled }).Count -eq 1 -and
                @($items | Where-Object { $_.automationId -eq 'NoteScopeSummary' -and $_.name -like 'Undo Delete is available*' }).Count -eq 1
        }
        Fill 'Key or branch' "$address\Child"
        Primary
        Wait-Ui {
            param($items)
            $items.automationId -notcontains 'NoteScopeChoice' -and
                @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Branch Counter note' }).Count -eq 1
        }
        Fill 'Local note' 'Moved Counter note'
        Ui @('send-keys', 'ctrl+s', '--via', 'send-input', '--target', 'NoteEditor') | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Saved' }
        $notes = Pick 'TabItem' 'Notes' -All
        Ui @('invoke', $notes.selector, '--action', 'select') | Out-Null
        Wait-Ui {
            param($items)
            $items.automationId -contains 'NotesStatusText' -and $items.name -contains 'Value note' -and
                @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Moved Counter note' }).Count -eq 2
        }
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $counter = @($saved.Notes | Where-Object { $_.Location.SubKey -eq $destination -and $_.ValueName -eq 'Counter' })
        if ($counter.Count -ne 1 -or $counter[0].MatchRule -or $counter[0].Text -ne 'Moved Counter note' -or
            @($saved.Notes | Where-Object { $_.Location.SubKey -eq $subKey -and $_.ValueName -eq 'Counter' }).Count -ne 0) {
            throw 'The scope change did not move the branch note to the exact value in Child.'
        }
        Fill 'Search tool items' 'moved counter'
        Wait-Ui {
            param($items)
            $rows = @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -match ', (Value note|Key note|Match rule),' })
            $rows.Count -eq 1 -and $rows[0].name -like 'Counter, Value note,*'
        }
        Browser
        if ((Read-Ui).name -contains 'Close tool panel') { Press 'Close tool panel' }
        Select-Row 'Counter' | Out-Null
        Ui @('invoke', 'AddValueNote', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) $items.automationId -contains 'UndoNoteDelete' }
        Ui @('invoke', 'UndoNoteDelete', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Exact Counter note' }).Count -eq 1 }
        Press 'Close tool panel'
        Select-Row 'Child, Key,' | Out-Null
        Wait-Ui { param($items) $items.name -contains 'Child key note' -and $items.automationId -contains 'EditKeyNote' -and $items.automationId -notcontains 'MoreNotes' }
        Ui @('invoke', 'EditKeyNote', '--action', 'invoke') | Out-Null
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Child key note' }).Count -eq 1 }
        Press 'Close tool panel'

        $child = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($destination, $true)
        try { $child.DeleteValue('Counter') }
        finally { $child.Dispose() }
        Fill 'Search registry' 'Moved Counter match:notes'
        Press 'Advanced search'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'SearchNotes' -and $_.name -eq 'Notes' }).Count -eq 1 }
        Primary
        Wait-Ui { param($items) $items.automationId -contains 'BottomTabs' }
        Press 'Open panel tab in main tab'
        Wait-Ui {
            param($items)
            @($items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like 'Counter,*Value note*Moved Counter note*' }).Count -eq 1
        }

        Browser
        Open-Menu 'Notes' -Group 'Tools' -MainTab
        Fill 'Search tool items' 'Child default note'
        Select-Row '(Default)' | Out-Null
        Press 'Show in panel'
        Wait-Ui { param($items) @($items | Where-Object { $_.automationId -eq 'NoteEditor' -and $_.value -eq 'Child default note' }).Count -eq 1 }
        Press 'Open value' 'NoteCommands'
        Wait-Ui {
            param($items)
            $row = $items | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '(Default),*' }
            $row -and (Ui @('get-property', $row.selector, '--property', 'IsSelected', '--json') | ConvertFrom-Json).properties.IsSelected -eq 'True'
        }
    }

    Test-Ui 'Favorites bar switches between open folder menus on hover' {
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(15000)) { throw 'The editor did not close for Favorites setup.' }
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -AsHashtable
        $settings.ShowFavoritesBar = $true
        $settings.FavoriteFolders = @(
            @{ Id = 'hover-first'; Name = 'First folder' }
            @{ Id = 'hover-second'; Name = 'Second folder' }
        ) + @(1..16 | ForEach-Object { @{ Id = "hover-extra-$_"; Name = "Additional favorites folder $_" } })
        $settings.Favorites = @(
            @{ Id = 'hover-first-key'; Name = 'First folder key'; FolderId = 'hover-first'; Address = $address }
            @{ Id = 'hover-second-key'; Name = 'Second folder key'; FolderId = 'hover-second'; Address = "$address\Child" }
        )
        [IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 64))
        Restart-Editor
        Browser
        Set-TestWindowForeground
        Ui @('click', 'FavoriteBarFolder_hover-first') | Out-Null
        [void](Read-ContextMenu 'First folder key')
        Ui @('hover', 'FavoriteBarFolder_hover-second') | Out-Null
        [void](Read-ContextMenu 'Second folder key')
        Ui @('hover', 'FavoriteBarOverflowButton') | Out-Null
        [void](Read-ContextMenu 'Open Favorites sidebar')
        Ui @('hover', 'FavoriteBarFolder_hover-first') | Out-Null
        $menu = Read-ContextMenu 'First folder key'
        Ui @('send-keys', 'esc', '--via', 'send-input') -Window $menu.Window | Out-Null
        Ui @('hover', 'FavoriteBarFolder_hover-second') | Out-Null
        Ui @('click', 'FavoriteBarFolder_hover-second') | Out-Null
        [void](Read-ContextMenu 'Second folder key')
    }

    if ($results.Count -eq 0) { throw "No UI test matched '$TestFilter'." }
}
catch {
    if ($appProcess) {
        $appLog = Get-ChildItem -LiteralPath (Join-Path $dataFolder 'Logs') -Filter '*.log' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($appLog) {
            $pidMarker = '[' + $appProcess.Id + ']'
            $entries = (Get-Content -LiteralPath $appLog.FullName -Tail 180) -join [Environment]::NewLine
            $entries -split '(?m)(?=^\d{4}-\d\d-\d\dT)' | Where-Object { $_.Contains($pidMarker) } |
                Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'app-log.txt')
        }
    }
    if ($mainWindow) { try { Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'failure.png')) | Out-Null } catch { } }
    if (-not $results.Where({ $_.status -eq 'FAIL' }).Count) { $results.Add(@{ name = 'UI test setup'; status = 'FAIL'; detail = "$_" }) }
    Write-Output "FAIL: $_"
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        if ($mainWindow) { [UxTestWindow]::CloseWindow($mainWindow) }
        else { $appProcess.CloseMainWindow() | Out-Null }
        if (-not $appProcess.WaitForExit(10000)) {
            $results.Add(@{ name = 'Graceful shutdown'; status = 'FAIL'; detail = 'The fixture editor did not exit.' })
            $appProcess.Kill(); $appProcess.WaitForExit(5000) | Out-Null
        }
    }
    $software = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software', $true)
    try { $software.DeleteSubKeyTree($fixtureName, $false) } finally { $software.Dispose() }
    try { Invoke-WinAppChecked @('ui', 'yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    @{ results = $results.ToArray(); target = 'local' } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    Write-Output "Results: $ArtifactDirectory"
}
if ($results.Where({ $_.status -eq 'FAIL' }).Count) { exit 1 }
