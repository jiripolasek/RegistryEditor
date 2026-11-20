param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$WinApp,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\notifications\ui'),
    [switch]$Styles,
    [switch]$IconsOnly,
    [switch]$LayoutOnly,
    [switch]$Keyboard,
    [Parameter(Mandatory)][guid]$SandboxRun,
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\..'),
    [switch]$TemplatesOnly,
    [switch]$ReviewFixesOnly,
    [switch]$MiddleClickOnly
)

$ErrorActionPreference = 'Stop'
if ($env:REGEDIT_UI_TEST_SANDBOX_RUN -ne $SandboxRun.ToString('N')) {
    throw 'Run notification UI checks through eng/Test-Sandbox.ps1 -Suite Notifications or NativeNotifications.'
}
$PSNativeCommandUseErrorActionPreference = $false
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$ArtifactDirectory = [IO.Path]::GetFullPath((Join-Path $ArtifactDirectory ([guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$results = [Collections.Generic.List[string]]::new()
$process = Get-Process -Id $AppPid
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$windowHandle = [string]$root.Current.NativeWindowHandle
$secondWindow = $null

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAILED: $Description" }
    $results.Add($Description)
    Write-Output "PASS: $Description"
}

function Find-Control([string]$Id, $WindowRoot = $root) {
    $control = $WindowRoot.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
    if ($null -eq $control) { throw "Missing control: $Id" }
    return $control
}

function Find-RawControl([string]$Id, $Element = $root) {
    if ($Element.Current.AutomationId -eq $Id) { return $Element }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    for ($child = $walker.GetFirstChild($Element); $null -ne $child; $child = $walker.GetNextSibling($child)) {
        $found = Find-RawControl $Id $child
        if ($null -ne $found) { return $found }
    }
}

function Invoke-Control([string]$Id, $WindowRoot = $root) {
    (Find-Control $Id $WindowRoot).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Set-Choice([string]$Id, [string]$Label) {
    Wait-Until { $null -ne (Find-Control $Id) } "choice $Id"
    $combo = Find-Control $Id
    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Label)
    Wait-Until { $null -ne $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) } "option $Label"
    $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) { $expand.Collapse() }
    Wait-Until {
        $selected = (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        $selected.Count -eq 1 -and $selected[0].Current.Name -eq $Label
    } "selected $Label"
    Start-Sleep -Milliseconds 150
}

function Find-VisibleName([string]$Name, [System.Windows.Automation.ControlType]$ControlType = $null) {
    @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)) |
        Where-Object { -not $_.Current.IsOffscreen -and [double]::IsFinite($_.Current.BoundingRectangle.Left) -and
                       $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.Height -gt 0 -and
                       ($null -eq $ControlType -or $_.Current.ControlType -eq $ControlType) }) | Select-Object -First 1
}

function Get-Cards($WindowRoot = $root) {
    @($WindowRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.AutomationId.StartsWith('Notification_', [StringComparison]::Ordinal) -and -not $_.Current.IsOffscreen })
}

function Get-AllCards($WindowRoot = $root) {
    @($WindowRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.AutomationId.StartsWith('Notification_', [StringComparison]::Ordinal) })
}

function Toggle-Stack {
    $toggle = Find-Control 'NotificationStackToggle'
    $toggle.SetFocus()
    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
}

function Check-CustomIcons([string]$Description) {
    $iconIdentity = (Get-Cards)[0].Current.AutomationId
    $symbolLeft = (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle.Left
    (Find-VisibleName 'Use edit icon').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { $null -ne (Find-VisibleName 'Hide icon') } 'replace symbol with font glyph'
    $glyphLeft = (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle.Left
    (Find-VisibleName 'Hide icon').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { $null -ne (Find-VisibleName 'Use save icon') -and
                 (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle.Left -lt [math]::Min($symbolLeft, $glyphLeft) } 'clear a custom icon and update its layout'
    $plainLeft = (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle.Left
    Check ($symbolLeft -gt $plainLeft -and $glyphLeft -gt $plainLeft -and
           (Get-Cards)[0].Current.AutomationId -eq $iconIdentity -and (Get-Cards)[0].Current.Name -eq 'Document saved') "$Description replaces and clears custom icons without changing identity or severity"
    (Find-VisibleName 'Use save icon').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { $null -ne (Find-VisibleName 'Use edit icon') -and
                 (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle.Left -eq $symbolLeft } 'restore a custom save symbol'
    Check ((Get-Cards)[0].Current.AutomationId -eq $iconIdentity) "$Description restores the save symbol on the same notification"
}

function Check-CustomIconStyles {
    [xml]$iconMarkup = Get-Content -LiteralPath (Join-Path $SourceRoot 'src/Regedit/Notifications/WinUI/Themes/NotificationCard.xaml') -Raw
    $iconBrush = $iconMarkup.SelectSingleNode("//*[@*[local-name()='Name']='PART_CustomIcon']").GetAttribute('Foreground')
    Check ($iconBrush -eq '{TemplateBinding IconForeground}') 'Custom icons default to a gray theme brush independent of severity and user accent'
    Set-Choice 'PrototypeMaterial' 'Acrylic'
    foreach ($iconStyle in @('Fluent 2', 'Accent rail', 'Compact', 'Native InfoBar')) {
        Set-Choice 'PrototypeStyle' $iconStyle
        foreach ($iconTheme in @('Light', 'Dark')) {
            Set-Choice 'PrototypeTheme' $iconTheme
            Check-CustomIcons "$iconStyle / $iconTheme"
            $iconImage = ('icon-' + $iconStyle + '-' + $iconTheme).ToLowerInvariant().Replace(' ', '-') + '.png'
            Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory $iconImage)) | Out-Null
        }

        Set-Choice 'PrototypeTheme' 'Light'
        Wait-Until { $null -ne (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)) -and
                     [double]::IsFinite((Get-Cards)[0].Current.BoundingRectangle.Left) } 'themed icon layout'
        $tintImage = Join-Path $ArtifactDirectory (('icon-' + $iconStyle + '-theme-switch.png').ToLowerInvariant().Replace(' ', '-'))
        Ui @('screenshot', '--output', $tintImage) | Out-Null
        Add-Type -AssemblyName System.Drawing
        $tintBitmap = [Drawing.Bitmap]::new($tintImage)
        try {
            $titleBounds = (Find-VisibleName 'Document saved' -ControlType ([System.Windows.Automation.ControlType]::Text)).Current.BoundingRectangle
            $cardBounds = (Get-Cards)[0].Current.BoundingRectangle
            $windowBounds = $root.Current.BoundingRectangle
            $scale = $cardBounds.Width / 400
            $frameInset = ($windowBounds.Width - $tintBitmap.Width) / 2
            $tintLeft = [int]([math]::Max($cardBounds.Left + 8 * $scale, $titleBounds.Left - 40 * $scale) - $windowBounds.Left - $frameInset)
            $tintRight = [int]($titleBounds.Left - 4 * $scale - $windowBounds.Left - $frameInset)
            $tintTop = [int]($titleBounds.Top - 4 * $scale - $windowBounds.Top)
            $tintBottom = [int]($titleBounds.Bottom + 4 * $scale - $windowBounds.Top)
            $grayPixels = 0
            for ($y = [math]::Max(0, $tintTop); $y -lt [math]::Min($tintBitmap.Height, $tintBottom); $y++) {
                for ($x = [math]::Max(0, $tintLeft); $x -lt [math]::Min($tintBitmap.Width, $tintRight); $x++) {
                    $pixel = $tintBitmap.GetPixel($x, $y)
                    $brightest = [math]::Max($pixel.R, [math]::Max($pixel.G, $pixel.B))
                    $darkest = [math]::Min($pixel.R, [math]::Min($pixel.G, $pixel.B))
                    if ($brightest -lt 190 -and $brightest - $darkest -lt 16) { $grayPixels++ }
                }
            }

            Check ($grayPixels -gt 10) "$iconStyle retints the existing save symbol after switching from dark to light"
        } finally {
            $tintBitmap.Dispose()
        }
    }
}

function Wait-Until([scriptblock]$Predicate, [string]$Description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            if (& $Predicate) { return }
        } catch {
            if ($_.Exception.Message -notlike 'Missing control:*' -and
                $_.Exception -isnot [System.Windows.Automation.ElementNotAvailableException]) { throw }
        }
        if ($watch.Elapsed.TotalSeconds -ge 10) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 100
    }
}

function Ui([string[]]$Arguments) {
    $output = & $WinApp ui @Arguments -w $windowHandle
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $output" }
    return $output
}

function Clear-Cards {
    Invoke-Control 'PrototypeDismissAll'
    Wait-Until { (Get-Cards).Count -eq 0 } 'clear active cards'
}

function Get-Windows {
    @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)) |
        Where-Object { $_.Current.Name -eq 'Notifications prototype' })
}

function Check-HostLayout {
    Set-Choice 'PrototypeStyle' 'Minimal'
    $legacyHistory = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NotificationHistory'))
    Check ($null -eq $legacyHistory) 'Toasts have no persistent history control'
    $layoutEditorBounds = (Find-Control 'PrototypeEditor').Current.BoundingRectangle
    $scale = $root.Current.BoundingRectangle.Width / 1100
    foreach ($area in @('Full window', 'Workspace pane')) {
        Set-Choice 'PrototypeArea' $area
        Wait-Until { (Find-Control 'PrototypeList').Current.BoundingRectangle.Width -gt 0 } 'workspace layout after changing host region'
        $region = if ($area -eq 'Workspace pane') { (Find-Control 'PrototypeList').Current.BoundingRectangle } else { $root.Current.BoundingRectangle }
        foreach ($position in @('Bottom right', 'Bottom center', 'Bottom left', 'Top right', 'Top center', 'Top left')) {
            Set-Choice 'PrototypePosition' $position
            Clear-Cards
            Invoke-Control 'PrototypeDefaultAction'
            Wait-Until { (Get-Cards).Count -eq 1 -and $null -ne (Find-VisibleName 'Undo') } 'positioned minimal toast'
            $cardBounds = (Get-Cards)[0].Current.BoundingRectangle
            $titleBounds = (Find-VisibleName 'Changes saved').Current.BoundingRectangle
            $openBounds = (Find-VisibleName 'Open').Current.BoundingRectangle
            $undoBounds = (Find-VisibleName 'Undo').Current.BoundingRectangle
            $horizontal = if ($position.EndsWith('left')) {
                $cardBounds.Left - $region.Left -ge 12 * $scale -and $cardBounds.Left - $region.Left -le 32 * $scale
            } elseif ($position.EndsWith('right')) {
                $region.Right - $cardBounds.Right -ge 12 * $scale -and $region.Right - $cardBounds.Right -le 32 * $scale
            } else {
                [math]::Abs(($cardBounds.Left + $cardBounds.Right - $region.Left - $region.Right) / 2) -lt 3 * $scale
            }
            $groupTop = $cardBounds.Top
            $groupBottom = $cardBounds.Bottom
            $vertical = if ($position.StartsWith('Top')) {
                $groupTop - $region.Top -ge 12 * $scale -and $groupTop - $region.Top -le 90 * $scale
            } else {
                $region.Bottom - $groupBottom -ge 12 * $scale -and $region.Bottom - $groupBottom -le 32 * $scale
            }
            Check ($horizontal -and $vertical -and $cardBounds.Top -ge $region.Top -and $cardBounds.Bottom -le $region.Bottom) "$area / $position anchors the toast within its host region"
            Check ([math]::Abs(($titleBounds.Top + $titleBounds.Bottom - $openBounds.Top - $openBounds.Bottom) / 2) -lt 5 * $scale -and
                   [math]::Abs(($titleBounds.Top + $titleBounds.Bottom - $undoBounds.Top - $undoBounds.Bottom) / 2) -lt 5 * $scale -and
                   $cardBounds.Height -lt 60 * $scale) "$area / $position keeps title and actions on a single line"
            Check ((Find-VisibleName 'Undo').Current.HelpText -eq 'Default action' -and
                   (Find-VisibleName 'Open').Current.HelpText -ne 'Default action') "$area / $position marks only the designated default action"
            Check ((Find-Control 'PrototypeEditor').Current.BoundingRectangle -eq $layoutEditorBounds) "$area / $position preserves workspace geometry"
            Invoke-Control 'PrototypePreview'
            Invoke-Control 'PrototypeDefaultAction'
            Wait-Until { (Get-AllCards).Count -eq 1 } 'collapsed positioned stack'
            $beforeExpansion = (Find-Control 'NotificationStackToggle').Current.BoundingRectangle
            if ($area -eq 'Full window' -and $position -eq 'Bottom right') {
                Check ($beforeExpansion.Height -le 28 * $scale -and $beforeExpansion.Width -le 72 * $scale) 'Stack expansion uses a compact count and chevron'
            }
            (Find-Control 'NotificationStackToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Wait-Until { (Get-AllCards).Count -eq 3 } 'expanded positioned stack'
            $front = (Find-VisibleName 'Changes saved').Current.BoundingRectangle
            $afterExpansion = (Find-Control 'NotificationStackToggle').Current.BoundingRectangle
            Check ($afterExpansion.Top -eq $beforeExpansion.Top -and $afterExpansion.Left -eq $beforeExpansion.Left -and
                   $front.Top -ge $region.Top -and $front.Bottom -le $region.Bottom) "$area / $position expands inward without moving its toggle"
        }
        Set-Choice 'PrototypeStyle' 'Fluent 2'
        foreach ($position in @('Top center', 'Bottom center')) {
            Set-Choice 'PrototypePosition' $position
            Clear-Cards
            Invoke-Control 'PrototypeBurst'
            Wait-Until { (Get-AllCards).Count -eq 1 } 'bounded overflow stack'
            (Find-Control 'NotificationStackToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Wait-Until { (Get-AllCards).Count -gt 2 } 'bounded overflow expansion'
            $scroll = (Find-Control 'NotificationStackViewport').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            Wait-Until { if ($position.StartsWith('Top')) { $scroll.Current.VerticalScrollPercent -le 1 } else { $scroll.Current.VerticalScrollPercent -ge 99 } } 'front-aligned stack scroll'
            Check ($scroll.Current.VerticallyScrollable) "$area / $position scrolls overflow within the bounded host"
        }
        Set-Choice 'PrototypeStyle' 'Minimal'
    }
    Set-Choice 'PrototypeArea' 'Full window'
    Set-Choice 'PrototypePosition' 'Bottom center'
    Clear-Cards
    Invoke-Control 'PrototypeDefaultAction'
    Set-Choice 'PrototypeActions' 'Buttons'
    Wait-Until { $null -ne (Find-VisibleName 'Undo') } 'default button presentation'
    Check ((Find-VisibleName 'Undo').Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) 'Default actions also support native button presentation'
    Set-Choice 'PrototypeActions' 'Links'
    Invoke-Control 'PrototypeHistory'
    Wait-Until { $null -ne (Find-Control 'NotificationHistoryList') } 'application-owned history'
    Check ((Find-Control 'NotificationHistoryList').Current.Name.StartsWith('Notification history')) 'An optional application command can open notification history'
    Invoke-Control 'DismissAllNotifications'
    Wait-Until { (Get-Cards).Count -eq 0 } 'close application history and dismiss active examples'
    Invoke-Control 'PrototypeDefaultAction'
}

function Check-MiddleClickDismissal {
    . "$PSScriptRoot/UiTestWindow.ps1"
    [UxTestWindow]::TargetWindow = $process.MainWindowHandle
    Clear-Cards
    foreach ($style in @('Fluent 2', 'Accent rail', 'Compact', 'Minimal', 'Native InfoBar')) {
        Set-Choice 'PrototypeStyle' $style
        foreach ($target in @('Surface', 'Link action', 'Button action', 'Dismiss link')) {
            Set-Choice 'PrototypeActions' $(if ($target -eq 'Button action') { 'Buttons' } else { 'Links' })
            Set-Choice 'PrototypeDismiss' $(if ($target -eq 'Dismiss link') { 'Dismiss link' } else { 'Close icon' })
            Invoke-Control 'PrototypeDefaultAction'
            Wait-Until { (Get-Cards).Count -eq 1 } 'middle-click notification'
            $status = (Find-Control 'PrototypeActionStatus').Current.Name
            $bounds = if ($target -eq 'Surface') { (Get-Cards)[0].Current.BoundingRectangle }
                elseif ($target -eq 'Dismiss link') { (Find-VisibleName 'Dismiss' -ControlType ([System.Windows.Automation.ControlType]::Hyperlink)).Current.BoundingRectangle }
                else { (Find-VisibleName 'Open').Current.BoundingRectangle }
            $x = if ($target -eq 'Surface') { $bounds.Left + 8 } else { $bounds.Left + $bounds.Width / 2 }
            [UxTestWindow]::MiddleClickAt([int]$x, [int]($bounds.Top + $bounds.Height / 2))
            Wait-Until { (Get-Cards).Count -eq 0 } "middle dismissal over $target"
            Check ((Find-Control 'PrototypeActionStatus').Current.Name -eq $status) "$style / $target middle-click dismisses without invoking an action"
        }
    }

    Set-Choice 'PrototypeStyle' 'Fluent 2'
    Set-Choice 'PrototypeActions' 'Links'
    Set-Choice 'PrototypeDismiss' 'Close icon'
    Invoke-Control 'PrototypeDefaultAction'
    Wait-Until { (Get-Cards).Count -eq 1 } 'normal mouse input notification'
    $card = (Get-Cards)[0]
    $bounds = $card.Current.BoundingRectangle
    [UxTestWindow]::LeftClickAt([int]($bounds.Left + 8), [int]($bounds.Top + $bounds.Height / 2))
    [UxTestWindow]::RightClickAt([int]($bounds.Left + 8), [int]($bounds.Top + $bounds.Height / 2))
    Check ((Get-Cards).Count -eq 1 -and (Get-Cards)[0].Current.AutomationId -eq $card.Current.AutomationId) 'Left and right clicks on the surface keep the toast open'
    $action = (Find-VisibleName 'Open').Current.BoundingRectangle
    [UxTestWindow]::LeftClickAt([int]($action.Left + $action.Width / 2), [int]($action.Top + $action.Height / 2))
    Wait-Until { (Get-Cards).Count -eq 0 } 'normal left-click action'
    Check ((Find-Control 'PrototypeActionStatus').Current.Name -eq 'Secondary action invoked.') 'Left-click still invokes a notification action'

    Invoke-Control 'PrototypePreview'
    Wait-Until { (Get-Cards).Count -eq 2 } 'instance dismissal preview'
    $status = (Find-Control 'PrototypeActionStatus').Current.Name
    $dismiss = (Find-VisibleName 'Not now').Current.BoundingRectangle
    [UxTestWindow]::MiddleClickAt([int]($dismiss.Left + $dismiss.Width / 2), [int]($dismiss.Top + $dismiss.Height / 2))
    Wait-Until { (Get-Cards).Count -eq 1 } 'instance middle dismissal'
    Check ((Get-Cards)[0].Current.Name.Contains('Export complete') -and (Find-Control 'PrototypeActionStatus').Current.Name -eq $status) 'Middle-click on a custom dismiss link closes only its notification'
    Clear-Cards
    Invoke-Control 'PrototypeBurst'
    Wait-Until { (Get-AllCards).Count -eq 1 } 'collapsed middle-dismissal stack'
    Toggle-Stack
    Wait-Until { (Get-AllCards).Count -eq 50 } 'expanded middle-dismissal stack'
    $before = @((Get-AllCards) | ForEach-Object { $_.Current.AutomationId })
    $target = (Get-Cards)[-1]
    $identity = $target.Current.AutomationId
    $bounds = $target.Current.BoundingRectangle
    [UxTestWindow]::MiddleClickAt([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    Wait-Until { (Get-AllCards).Count -eq 49 } 'expanded stack middle dismissal'
    $after = @((Get-AllCards) | ForEach-Object { $_.Current.AutomationId })
    Check ($identity -notin $after -and @($after | Where-Object { $_ -notin $before }).Count -eq 0) 'Middle-click in the faded scroll viewport dismisses only the target card'
    $editor = (Find-Control 'PrototypeEditor').Current.BoundingRectangle
    [UxTestWindow]::MiddleClickAt([int]($editor.Left + $editor.Width / 2), [int]($editor.Top + $editor.Height / 2))
    Check ((Get-AllCards).Count -eq 49) 'Middle-click outside a card leaves notifications alone'
    Clear-Cards
}

function Check-DefaultKeyboard {
    $keyboardEditor = Find-Control 'PrototypeEditor'
    $keyboardEditor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Typing stays here')
    $keyboardEditor.SetFocus()
    Ui @('send-keys', 'ENTER', '--target', 'PrototypeEditor', '--via', 'send-input') | Out-Null
    Start-Sleep -Milliseconds 250
    Check ((Get-Cards).Count -eq 1 -and $keyboardEditor.Current.HasKeyboardFocus) 'Enter in the editor does not invoke a toast default'
    $keyboardCard = (Get-Cards)[0]
    $keyboardCard.SetFocus()
    Ui @('send-keys', 'ENTER', '--target', $keyboardCard.Current.AutomationId, '--via', 'send-input') | Out-Null
    Wait-Until { (Get-Cards).Count -eq 0 } 'focused toast default activation'
    Check ((Find-Control 'PrototypeActionStatus').Current.Name -eq 'Default action invoked.' -and $keyboardEditor.Current.HasKeyboardFocus) 'Enter on the focused toast invokes its marked default and returns editor focus'
    Invoke-Control 'PrototypeDefaultAction'
    Wait-Until { $null -ne (Find-VisibleName 'Open') } 'secondary action keyboard preview'
    $secondaryButton = Find-VisibleName 'Open'
    $secondaryButton.SetFocus()
    Ui @('send-keys', 'ENTER', '--target', $secondaryButton.Current.AutomationId, '--via', 'send-input') | Out-Null
    Wait-Until { (Get-Cards).Count -eq 0 } 'focused secondary activation'
    Check ((Find-Control 'PrototypeActionStatus').Current.Name -eq 'Secondary action invoked.') 'Enter on a focused secondary action invokes that action'
    Invoke-Control 'PrototypeDefaultAction'
}

function Check-LiveHistory {
    Clear-Cards
    Invoke-Control 'PrototypeDefaultAction'
    Wait-Until { (Get-Cards).Count -eq 1 } 'persistent action example'
    $defaultId = (Get-Cards)[0].Current.AutomationId.Substring('Notification_'.Length)
    Invoke-Control 'PrototypeProgress'
    Wait-Until { (Get-Cards).Count -eq 2 } 'progress example'
    $progressId = @((Get-Cards) | Where-Object { $_.Current.AutomationId -ne ('Notification_' + $defaultId) })[0].Current.AutomationId.Substring('Notification_'.Length)
    Invoke-Control 'PrototypeHistory'
    Wait-Until { $null -ne (Find-VisibleName 'Active · Information: Processing example items') } 'active progress in history'
    $focus = Find-Control "HistoryAction_$($defaultId)_0"
    $focus.SetFocus()
    $identity = $focus.GetRuntimeId() -join ','
    Wait-Until { $null -ne (Find-VisibleName 'Active · Success: Processing complete') } 'completion updates open history'
    Check ($null -eq $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "HistoryAction_$($progressId)_0"))) 'Completed history removes the stale Cancel action'
    $retained = Find-Control "HistoryAction_$($defaultId)_0"
    Check ($retained.Current.HasKeyboardFocus -and ($retained.GetRuntimeId() -join ',') -eq $identity) 'History updates preserve the focused action control'
    Check ($null -ne (Find-VisibleName 'Active · Success: Processing complete')) 'History shows completion without reopening the flyout'
    Invoke-Control 'DismissAllNotifications'
    Wait-Until { (Get-Cards).Count -eq 0 -and $null -eq (Find-VisibleName 'Dismiss all active notifications') } 'dismiss active history examples'

    Invoke-Control 'PrototypeBurst'
    Invoke-Control 'PrototypeProgress'
    Invoke-Control 'PrototypeHistory'
    Wait-Until { $null -ne (Find-Control 'NotificationHistoryList') } 'bounded history'
    $list = Find-Control 'NotificationHistoryList'
    $scroll = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1, 60)
    Start-Sleep -Milliseconds 200
    $rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Height -gt 0 } |
        Sort-Object { $_.Current.BoundingRectangle.Top })
    $anchor = $rows[1]
    $anchorId = $anchor.GetRuntimeId() -join ','
    $anchorTop = $anchor.Current.BoundingRectangle.Top
    Start-Sleep -Milliseconds 4500
    Check (($anchor.GetRuntimeId() -join ',') -eq $anchorId -and
        [math]::Abs($anchor.Current.BoundingRectangle.Top - $anchorTop) -le 1) 'Progress completion preserves the history scroll anchor'
    Invoke-Control 'DismissAllNotifications'
    Wait-Until { (Get-Cards).Count -eq 0 -and $null -eq (Find-VisibleName 'Dismiss all active notifications') } 'clear history scroll examples'
}

function Check-NativeInformationIcon {
    [xml]$markup = Get-Content -LiteralPath (Join-Path $SourceRoot 'src/Regedit/Notifications/WinUI/Themes/NotificationCard.xaml') -Raw
    $native = $markup.SelectSingleNode("//*[@*[local-name()='Name']='PART_NativeNotice']")
    $contrast = $native.SelectSingleNode(".//*[@*[local-name()='Key']='HighContrast']/*[@*[local-name()='Key']='InfoBarInformationalSeverityIconBackground']")
    Check ($contrast.GetAttribute('ResourceKey') -eq 'SystemColorHighlightColorBrush') 'Native information retains the platform High Contrast icon colors'
    Set-Choice 'PrototypeStyle' 'Native InfoBar'
    Set-Choice 'PrototypeMaterial' 'Solid'
    foreach ($theme in @('Light', 'Dark')) {
        Set-Choice 'PrototypeTheme' $theme
        Clear-Cards
        Invoke-Control 'PrototypeSaveIcon'
        Wait-Until { $null -ne (Find-VisibleName 'Use edit icon') } 'custom native save icon'
        Check-CustomIcons "Native InfoBar / $theme after the informational-icon override"
        Clear-Cards
        Invoke-Control 'PrototypeInfo'
        Wait-Until { $null -ne (Find-VisibleName 'Information') } 'native informational toast'
        # This is WinUI's built-in InfoBar close button, outside our template contract.
        (Find-Control 'CloseButton').SetFocus()
        $file = Join-Path $ArtifactDirectory "native-information-$($theme.ToLowerInvariant()).png"
        Ui @('screenshot', '--output', $file) | Out-Null
        Add-Type -AssemblyName System.Drawing
        $bitmap = [Drawing.Bitmap]::new($file)
        try {
            $title = (Find-VisibleName 'Information').Current.BoundingRectangle
            $card = (Get-Cards)[0].Current.BoundingRectangle
            $window = $root.Current.BoundingRectangle
            $scale = $card.Width / 400
            $inset = ($window.Width - $bitmap.Width) / 2
            $left = [int]([math]::Max($card.Left + 8 * $scale, $title.Left - 40 * $scale) - $window.Left - $inset)
            $right = [int]($title.Left - 4 * $scale - $window.Left - $inset)
            $top = [int]($title.Top - 4 * $scale - $window.Top)
            $bottom = [int]($title.Bottom + 4 * $scale - $window.Top)
            $colored = 0
            $gray = 0
            for ($y = [math]::Max(0, $top); $y -lt [math]::Min($bitmap.Height, $bottom); $y++) {
                for ($x = [math]::Max(0, $left); $x -lt [math]::Min($bitmap.Width, $right); $x++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $brightest = [math]::Max($pixel.R, [math]::Max($pixel.G, $pixel.B))
                    $darkest = [math]::Min($pixel.R, [math]::Min($pixel.G, $pixel.B))
                    if ($brightest - $darkest -gt 24) { $colored++ }
                    if ($brightest -lt 190 -and $brightest - $darkest -lt 16) { $gray++ }
                }
            }
            Check ($right -gt $left -and $gray -gt 10 -and $colored -le 3) "Native informational icon is gray in $theme instead of using the user accent"
        } finally {
            $bitmap.Dispose()
        }
    }
}

try {
    Check ($root.Current.Name -eq 'Notifications prototype') 'Targets the isolated prototype window'
    if ($TemplatesOnly) {
        Invoke-Control 'PrototypeTemplateChecks'
        Wait-Until { (Find-Control 'PrototypeActionStatus').Current.Name -match '^Template checks (passed|failed):' } 'template and theme regressions'
        $status = (Find-Control 'PrototypeActionStatus').Current.Name
        Check ($status.StartsWith('Template checks passed:')) $status
        Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
        return
    }
    if ($MiddleClickOnly) {
        Check-MiddleClickDismissal
        Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
        return
    }
    if ($ReviewFixesOnly) {
        Check-LiveHistory
        Check-NativeInformationIcon
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'review-fixes-completed.png')) | Out-Null
        Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
        return
    }
    if ($LayoutOnly) {
        Check-HostLayout
        if ($Keyboard) { Check-DefaultKeyboard }
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'layout-completed.png')) | Out-Null
        Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
        return
    }
    if ($IconsOnly) {
        Clear-Cards
        Invoke-Control 'PrototypeSaveIcon'
        Wait-Until { (Get-Cards).Count -eq 1 -and $null -ne (Find-VisibleName 'Use edit icon') } 'custom save icon example'
        Check-CustomIcons 'The save icon playground example'
        if ($Styles) { Check-CustomIconStyles }
        Set-Choice 'PrototypeStyle' 'Fluent 2'
        Set-Choice 'PrototypeTheme' 'Light'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'completed.png')) | Out-Null
        Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
        return
    }

    Clear-Cards
    $editor = Find-Control 'PrototypeEditor'
    $list = Find-Control 'PrototypeList'
    $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep this selection while a background message appears.')
    $text = $editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $selection = $text.DocumentRange.FindText('selection', $false, $false)
    $selection.Select()
    $editorBounds = $editor.Current.BoundingRectangle
    $listBounds = $list.Current.BoundingRectangle
    # InvokePattern can focus the publisher button; the delayed sample publishes after focus returns to the editor.
    Invoke-Control 'PrototypeInfo'
    $editor.SetFocus()
    $selection.Select()
    Wait-Until { (Get-Cards).Count -eq 1 } 'asynchronous information toast'
    Check $editor.Current.HasKeyboardFocus 'A background toast preserves editor focus'
    Check ($text.GetSelection()[0].GetText(-1) -eq 'selection') 'A background toast preserves text selection'
    Check ($editor.Current.BoundingRectangle -eq $editorBounds -and $list.Current.BoundingRectangle -eq $listBounds) 'The overlay preserves workspace geometry'
    Ui @('send-keys', 'continued', '--target', 'PrototypeEditor', '--via', 'send-input', '--verbatim') | Out-Null
    Wait-Until { $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value.Contains('continued') } 'typed input reaches the editor'
    Check ($editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value.Contains('continued')) 'Typing continues while a toast is visible'
    Invoke-Control 'PrototypeError'
    Ui @('click', 'PrototypeEditor') | Out-Null
    Check $editor.Current.HasKeyboardFocus 'Empty overlay space allows pointer interaction with the editor'
    Clear-Cards
    Invoke-Control 'PrototypeWarning'
    Invoke-Control 'PrototypeWarning'
    Wait-Until { (Get-Cards).Count -eq 1 } 'keyed warning'
    Check ((Get-Cards).Count -eq 1) 'Repeating a key updates one card'
    Invoke-Control 'PrototypeError'
    Wait-Until { (Get-Cards).Count -eq 2 } 'two visible cards'
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'overlay.png')) | Out-Null
    Clear-Cards
    Invoke-Control 'PrototypeAction'
    Wait-Until { (Get-Cards).Count -eq 1 } 'action card'
    $action = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Show result'))
    $action.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { (Find-Control 'PrototypeActionStatus').Current.Name -eq 'Result action completed.' } 'awaited action result'
    Wait-Until { (Get-Cards).Count -eq 0 } 'successful action dismissal'
    Check $true 'An awaited action completes and dismisses its card'
    Invoke-Control 'PrototypeFailingAction'
    Wait-Until { (Get-Cards).Count -eq 1 } 'failing action card'
    $action = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Try action'))
    $action.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { (Find-Control 'PrototypeActionStatus').Current.Name.StartsWith('Action failed:') } 'failure reporting'
    Check ((Get-Cards).Count -eq 1) 'An action failure leaves a retryable card and a responsive app'
    Clear-Cards
    Invoke-Control 'PrototypeProgress'
    Wait-Until { (Get-Cards).Count -eq 1 } 'progress card'
    $cancel = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Cancel'))
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until {
        $null -ne $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Processing canceled'))
    } 'progress cancellation result'
    Check $true 'Cancel replaces progress with its terminal message'
    Clear-Cards
    $workspaceBeforeStack = (Find-Control 'PrototypeEditor').Current.BoundingRectangle
    Invoke-Control 'PrototypeBurst'
    Wait-Until { (Get-Cards).Count -eq 1 } 'collapsed message burst'
    $stackToggle = Find-Control 'NotificationStackToggle'
    $toggleBounds = $stackToggle.Current.BoundingRectangle
    Check ($stackToggle.Current.Name -eq 'Show all 50 notifications' -and (Get-Cards).Count -eq 1) 'Many notifications collapse into one front card and an accurate counter'
    $frontCard = (Get-Cards)[0]
    $frontBounds = $frontCard.Current.BoundingRectangle
    $frontIdentity = $frontCard.Current.AutomationId
    $editor.SetFocus()
    Ui @('hover', 'PrototypeEditor', '--dwell-time', '250') | Out-Null
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'stack-collapsed.png')) | Out-Null
    Ui @('hover', $frontIdentity, '--dwell-time', '250') | Out-Null
    Check ((Find-RawControl 'NotificationStackPeek').Current.BoundingRectangle.Height -ge 47) 'Hover fans the backplates even when Windows animations are disabled'
    Check ((Get-AllCards).Count -eq 1 -and $frontCard.Current.BoundingRectangle -eq $frontBounds -and
           $stackToggle.Current.BoundingRectangle -eq $toggleBounds -and $editor.Current.HasKeyboardFocus) 'Hover preview preserves front-card geometry, controls and editor focus without expanding'
    Start-Sleep -Seconds 6
    Check ($stackToggle.Current.Name -eq 'Show all 50 notifications' -and (Get-Cards)[0].Current.AutomationId -eq $frontIdentity) 'Hovering the front toast preserves its message without expanding the stack'
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'stack-hover.png')) | Out-Null
    Ui @('hover', 'PrototypeEditor', '--dwell-time', '250') | Out-Null
    Check ($frontCard.Current.BoundingRectangle -eq $frontBounds -and (Get-AllCards).Count -eq 1) 'Leaving the hover preview preserves the collapsed layout'
    Check ((Find-RawControl 'NotificationStackPeek').Current.BoundingRectangle.Height -le 17) 'Leaving the stack restores the resting backplates'
    Toggle-Stack
    Wait-Until { (Get-AllCards).Count -eq 50 } 'expanded active notifications'
    $viewport = Find-Control 'NotificationStackViewport'
    $scroll = $viewport.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    Wait-Until { $scroll.Current.VerticalScrollPercent -ge 99 } 'expanded stack starts at its front card'
    Check ($scroll.Current.VerticallyScrollable -and $scroll.Current.VerticalViewSize -lt 25) 'Expansion provides bounded scrolling for all active cards'
    Check ((Find-Control 'PrototypeEditor').Current.BoundingRectangle -eq $workspaceBeforeStack) 'Expanding the stack preserves workspace geometry'
    Check ($stackToggle.Current.HasKeyboardFocus -and $stackToggle.Current.BoundingRectangle.Top -eq $toggleBounds.Top) 'Expansion preserves toggle focus and its screen position'
    Start-Sleep -Seconds 6
    Check ((Get-AllCards).Count -eq 50) 'Reviewing the expanded stack preserves active notifications'
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'stack-expanded.png')) | Out-Null
    Toggle-Stack
    Wait-Until { (Get-Cards).Count -eq 1 } 'collapse active notifications'
    Check ($stackToggle.Current.Name -eq 'Show all 50 notifications') 'Collapse restores one front card and the active counter'
    $editor.SetFocus()
    $peekBounds = (Get-Cards)[0].Current.BoundingRectangle
    $peekPoint = '{0},{1}' -f [int]($peekBounds.Left + $peekBounds.Width / 2), [int]($peekBounds.Top - 8)
    Ui @('click', '--at', $peekPoint) | Out-Null
    Wait-Until { (Get-AllCards).Count -gt 2 } 'backplate click expansion'
    Check ($editor.Current.HasKeyboardFocus -and (Find-Control 'PrototypeEditor').Current.BoundingRectangle -eq $workspaceBeforeStack) 'Clicking a backplate expands without taking editor focus or moving the workspace'
    Toggle-Stack
    Wait-Until { (Get-AllCards).Count -eq 1 } 'collapse after backplate click'
    $editor.SetFocus()
    $peekBounds = (Get-Cards)[0].Current.BoundingRectangle
    $peekPoint = '{0},{1}' -f [int]($peekBounds.Left + $peekBounds.Width / 2), [int]($peekBounds.Top - 8)
    $editorPoint = '{0},{1}' -f [int]($editorBounds.Left + $editorBounds.Width / 2), [int]($editorBounds.Top + $editorBounds.Height / 2)
    Ui @('hover', '--at', $peekPoint, '--dwell-time', '0') | Out-Null
    Ui @('hover', '--at', $editorPoint, '--dwell-time', '0') | Out-Null
    Start-Sleep -Milliseconds 1200
    Check ((Get-AllCards).Count -eq 1) 'Leaving the backplates early cancels hover expansion'
    Ui @('hover', '--at', $peekPoint, '--dwell-time', '1200') | Out-Null
    Wait-Until { (Get-AllCards).Count -gt 2 } 'backplate dwell expansion'
    $dwellCount = (Get-AllCards).Count
    Check ($editor.Current.HasKeyboardFocus -and (Find-Control 'PrototypeEditor').Current.BoundingRectangle -eq $workspaceBeforeStack) 'Dwelling over a backplate expands without taking editor focus or moving the workspace'
    Ui @('hover', 'PrototypeEditor', '--dwell-time', '250') | Out-Null
    Check ((Get-AllCards).Count -eq $dwellCount) 'An expanded stack stays open after the pointer leaves'
    Ui @('hover', '--at', $peekPoint, '--dwell-time', '0') | Out-Null
    Toggle-Stack
    Wait-Until { (Get-AllCards).Count -eq 1 } 'collapse after backplate dwell'
    Start-Sleep -Milliseconds 1200
    Check ((Get-AllCards).Count -eq 1) 'A layout-generated hover does not reopen a manually collapsed stack'
    Ui @('hover', 'PrototypeEditor', '--dwell-time', '250') | Out-Null
    Invoke-Control 'PrototypeHistory'
    Wait-Until { $null -ne (Find-Control 'NotificationHistoryList') } 'recent messages flyout'
    $recentName = (Find-Control 'NotificationHistoryList').Current.Name
    $countMatch = [regex]::Match($recentName, '\((\d+)\)')
    Check ($countMatch.Success -and [int]$countMatch.Groups[1].Value -le 100) 'Notification history stays within the active and closed history bounds'
    Invoke-Control 'DismissAllNotifications'
    Wait-Until { (Get-Cards).Count -eq 0 } 'dismiss overflow messages'
    Invoke-Control 'PrototypePreview'
    Invoke-Control 'PrototypeNeutral'
    Wait-Until { (Get-Cards).Count -eq 1 } 'three notifications collapse'
    Toggle-Stack
    Wait-Until { (Get-AllCards).Count -eq 3 } 'review earlier notification actions'
    Wait-Until { $null -ne (Find-VisibleName 'Show file') } 'earlier action remains available'
    $earlierAction = Find-VisibleName 'Show file'
    Invoke-Control 'PrototypeInfo'
    $earlierAction.SetFocus()
    $earlierBounds = $earlierAction.Current.BoundingRectangle
    Wait-Until { (Get-AllCards).Count -eq 4 } 'new arrival during stack review'
    Check ($earlierAction.Current.HasKeyboardFocus -and $earlierAction.Current.BoundingRectangle -eq $earlierBounds) 'New arrivals preserve the focused action and its position during review'
    (Find-VisibleName 'Got it').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { (Get-AllCards).Count -eq 3 } 'dismiss one reviewed notification'
    (Find-VisibleName 'Show file').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { (Get-Cards).Count -eq 2 } 'ordinary layout after dismissal'
    Check ((Find-Control 'PrototypeActionStatus').Current.Name -eq 'Preview action invoked.') 'Expanded cards retain their original actions'
    $stackState = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NotificationStackToggle'))
    Check ($null -eq $stackState -or $stackState.Current.IsOffscreen) 'Dismissing down to two cards removes the collapsed stack control'
    Clear-Cards
    Invoke-Control 'PrototypeNewWindow'
    Wait-Until { (Get-Windows).Count -eq 2 } 'second independent window'
    $secondWindow = Get-Windows | Where-Object { $_.Current.NativeWindowHandle -ne $root.Current.NativeWindowHandle } | Select-Object -First 1
    Invoke-Control 'PrototypeDismissAll' $secondWindow
    Wait-Until { (Get-Cards $secondWindow).Count -eq 0 } 'clear second window preview'
    Check ((Get-Cards $secondWindow).Count -eq 0) 'A new window has independently dismissible messages'
    Invoke-Control 'PrototypeError' $secondWindow
    Wait-Until { (Get-Cards $secondWindow).Count -eq 1 } 'second window notification'
    Check ((Get-Cards).Count -eq 0) 'A second window notification does not appear in the first window'
    $secondWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $secondWindow = $null
    Wait-Until { (Get-Windows).Count -eq 1 } 'second window teardown'
    Check $true 'Closing a prototype window leaves the other session running'
    Clear-Cards
    Invoke-Control 'PrototypeNeutral'
    Invoke-Control 'PrototypeSuccess'
    Wait-Until { (Get-Cards).Count -eq 2 -and $null -ne (Find-VisibleName 'Got it') } 'per-instance dismiss label'
    $statusBeforeDismiss = (Find-Control 'PrototypeActionStatus').Current.Name
    (Find-VisibleName 'Got it').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until { (Get-Cards).Count -eq 1 } 'custom dismiss affects one notification'
    Check ((Get-Cards)[0].Current.Name.StartsWith('Success:') -and
           (Find-Control 'PrototypeActionStatus').Current.Name -eq $statusBeforeDismiss) 'Custom dismissal closes only its instance without invoking an action'
    Clear-Cards
    Invoke-Control 'PrototypeSaveIcon'
    Wait-Until { (Get-Cards).Count -eq 1 -and $null -ne (Find-VisibleName 'Use edit icon') } 'custom save icon example'
    Check-CustomIcons 'The save icon playground example'
    Clear-Cards
    if ($Styles) {
        [xml]$cardMarkup = Get-Content -LiteralPath (Join-Path $SourceRoot 'src/Regedit/Notifications/WinUI/Themes/NotificationCard.xaml') -Raw
        $namespaces = [Xml.XmlNamespaceManager]::new($cardMarkup.NameTable)
        $namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
        $namespaces.AddNamespace('ui', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
        $infoBrush = $cardMarkup.SelectSingleNode("//*[@x:Name='SeverityInformation']/ui:VisualState.Setters/ui:Setter[@Target='StatusIcon.Foreground']", $namespaces).GetAttribute('Value')
        $spinnerBrush = $cardMarkup.SelectSingleNode("//*[@x:Name='BusyRing']", $namespaces).GetAttribute('Foreground')
        Check ($infoBrush -notmatch 'Accent' -and $infoBrush -match 'Neutral|Secondary') 'Informational icon color is independent of the user accent'
        Check ($spinnerBrush -notmatch 'Accent' -and $spinnerBrush -match 'Neutral|Secondary') 'Indeterminate progress uses a neutral icon color'
        Invoke-Control 'PrototypePreview'
        Wait-Until { (Get-Cards).Count -eq 2 } 'style preview'
        $identities = ((Get-Cards).Current.AutomationId | Sort-Object) -join ','
        $geometry = (Find-Control 'PrototypeEditor').Current.BoundingRectangle
        foreach ($style in @('Fluent 2', 'Accent rail', 'Compact', 'Native InfoBar')) {
            Set-Choice 'PrototypeStyle' $style
            foreach ($theme in @('Light', 'Dark')) {
                Set-Choice 'PrototypeTheme' $theme
                foreach ($material in @('Solid', 'Acrylic')) {
                    Set-Choice 'PrototypeMaterial' $material
                    Check ((Get-Cards).Count -eq 2 -and (((Get-Cards).Current.AutomationId | Sort-Object) -join ',') -eq $identities -and
                           (Find-Control 'PrototypeEditor').Current.BoundingRectangle -eq $geometry) "$style / $theme / $material preserves notifications and workspace geometry"
                    $neutralTitle = Find-VisibleName 'Your workspace changed' -ControlType ([System.Windows.Automation.ControlType]::Text)
                    $successTitle = Find-VisibleName 'Export complete'
                    Check ($neutralTitle.Current.BoundingRectangle.Left -lt $successTitle.Current.BoundingRectangle.Left) "$style neutral severity has no status icon column"
                    $name = ($style + '-' + $theme + '-' + $material).ToLowerInvariant().Replace(' ', '-') + '.png'
                    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory $name)) | Out-Null
                }
            }
        }

        Set-Choice 'PrototypeStyle' 'Fluent 2'
        Set-Choice 'PrototypeActions' 'Buttons'
        Wait-Until { (Find-VisibleName 'Show file').Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } 'button actions'
        Check ((Find-VisibleName 'Show file').Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) 'Primary actions can use standard buttons'
        Set-Choice 'PrototypeActions' 'Links'
        Check ((Find-VisibleName 'Show file').Current.ControlType -eq [System.Windows.Automation.ControlType]::Hyperlink) 'Primary actions can use lightweight links'
        Set-Choice 'PrototypeDismiss' 'Dismiss link'
        Check ($null -ne (Find-VisibleName 'Dismiss') -and $null -ne (Find-VisibleName 'Not now')) 'Per-instance dismiss text overrides the host default'
        Set-Choice 'PrototypeDismiss' 'Close icon'
        Check ($null -ne (Find-VisibleName 'Not now') -and $null -ne (Find-VisibleName 'Dismiss notification')) 'A custom dismiss link coexists with default close icons'
        Clear-Cards
        Invoke-Control 'PrototypeSaveIcon'
        Wait-Until { (Get-Cards).Count -eq 1 -and $null -ne (Find-VisibleName 'Use edit icon') } 'custom icon appearance preview'
        Check-CustomIconStyles

        Clear-Cards
        Invoke-Control 'PrototypeBurst'
        Wait-Until { (Get-Cards).Count -eq 1 } 'stack appearance preview'
        Set-Choice 'PrototypeTheme' 'Dark'
        foreach ($stackStyle in @('Fluent 2', 'Accent rail', 'Compact', 'Native InfoBar')) {
            Set-Choice 'PrototypeStyle' $stackStyle
            foreach ($stackMaterial in @('Solid', 'Acrylic')) {
                Set-Choice 'PrototypeMaterial' $stackMaterial
                Wait-Until { (Get-Cards).Count -eq 1 -and (Find-Control 'NotificationStackToggle').Current.Name.StartsWith('Show all') } 'collapsed deck after a style change or timed rotation'
                Check $true "$stackStyle / $stackMaterial supports a collapsed deck"
                $stackImage = ('stack-' + $stackStyle + '-' + $stackMaterial).ToLowerInvariant().Replace(' ', '-') + '.png'
                Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory $stackImage)) | Out-Null
                Toggle-Stack
                Wait-Until { (Get-AllCards).Count -gt 2 } 'expanded styled stack'
                Check ((Find-Control 'NotificationStackToggle').Current.Name.StartsWith('Collapse')) "$stackStyle / $stackMaterial expands without losing queued cards"
                Toggle-Stack
                Wait-Until { (Get-Cards).Count -eq 1 } 'collapsed styled stack'
            }
        }

        Clear-Cards
        Invoke-Control 'PrototypePreview'
        Set-Choice 'PrototypeStyle' 'Fluent 2'
        Set-Choice 'PrototypeTheme' 'Light'
        Set-Choice 'PrototypeMaterial' 'Acrylic'
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'fluent2-custom-dismiss.png')) | Out-Null
    }
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'completed.png')) | Out-Null
} catch {
    $failure = $_
    try {
        $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { @{ name = $_.Current.Name; id = $_.Current.AutomationId; offscreen = $_.Current.IsOffscreen; bounds = $_.Current.BoundingRectangle.ToString() } } |
            ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'failure-tree.json') -Encoding utf8
        Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'failure.png')) | Out-Null
    } catch { Write-Warning "Could not capture the failed UI: $_" }
    throw $failure
} finally {
    if ($null -ne $secondWindow) {
        $secondWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }
    & $WinApp ui yield | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    @{ checks = $results.ToArray(); count = $results.Count } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}

Write-Output "$($results.Count) UI checks passed. Evidence: $ArtifactDirectory"
