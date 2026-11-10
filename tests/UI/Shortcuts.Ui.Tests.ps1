param(
    [Parameter(Mandatory)][int] $AppPid,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/shortcuts-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
. (Join-Path $PSScriptRoot 'UiTestWindow.ps1')
Assert-UiTestProcess $AppPid
Assert-UiTestPath $ArtifactDirectory
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
Add-Type @'
using System.Runtime.InteropServices;
public static class ShortcutTestLayout {
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int count, [Out] nint[] layouts);
    [DllImport("user32.dll")] public static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] public static extern short VkKeyScanExW(ushort character, nint layout);
    [DllImport("user32.dll")] public static extern int PostMessageW(nint window, uint message, nuint key, nint data);
}
'@

$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$window = (Get-Process -Id $AppPid).MainWindowHandle
[UxTestWindow]::TargetWindow = $window
$targetProcessId = 0
$thread = [ShortcutTestLayout]::GetWindowThreadProcessId($window, [ref]$targetProcessId)
$originalLayout = [ShortcutTestLayout]::GetKeyboardLayout($thread)
$layouts = [IntPtr[]]::new([ShortcutTestLayout]::GetKeyboardLayoutList(0, $null))
$null = [ShortcutTestLayout]::GetKeyboardLayoutList($layouts.Length, $layouts)
$results = [Collections.Generic.List[object]]::new()

function Ui([string[]] $Arguments) {
    $output = & winapp ui @Arguments -w "$window" --on local
    if ($LASTEXITCODE) { throw "winapp ui $($Arguments -join ' ') failed." }
    $output
}

function Elements($Nodes) {
    foreach ($node in $Nodes) { $node; if ($node.children) { Elements $node.children } }
}

function Read-Ui {
    $json = Ui @('inspect', '-d', '14', '--hide-offscreen', '--json')
    $json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'latest.json') -Encoding utf8
    @(Elements (($json | ConvertFrom-Json).windows[0].elements))
}

function Keys([string] $Value) {
    Set-TestWindowForeground
    Ui @('send-keys', $Value, '--via', 'send-input') | Out-Null
}

function Assert-Shortcuts {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $elements = Read-Ui
        if ($elements.name -contains 'Keyboard shortcut reference' -and
            @($elements | Where-Object { $_.type -eq 'TabItem' -and $_.name -eq 'Settings' }).Count -eq 1) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The shortcut reference did not open in one Settings tab.'
}

try {
    if ($window -eq 0 -or $layouts.Length -eq 0) { throw 'Expected a test window and an installed keyboard layout.' }
    $initialTabs = @((Read-Ui) | Where-Object type -eq TabItem).Count
    foreach ($layout in $layouts) {
        Set-TestWindowForeground
        if ([ShortcutTestLayout]::PostMessageW($window, 0x0050, 0, $layout) -eq 0 -or
            -not [Threading.SpinWait]::SpinUntil({ [ShortcutTestLayout]::GetKeyboardLayout($thread) -eq $layout }, 3000)) {
            throw 'Could not switch the test window keyboard layout.'
        }
        $slash = [ShortcutTestLayout]::VkKeyScanExW(47, $layout)
        if ($slash -eq -1) { continue }
        $combo = 'ctrl+'
        if ($slash -band 0x100) { $combo += 'shift+' }
        if ($slash -band 0x400) { $combo += 'alt+' }
        $combo += 'vk=0x{0:X2}' -f ($slash -band 0xFF)
        foreach ($keys in @($combo, 'ctrl+vk=0x6F')) {
            Write-Output ('Testing layout 0x{0:X}: {1}' -f $layout.ToInt64(), $keys)
            Keys 'ctrl+l'
            Keys $keys
            Assert-Shortcuts
            Keys $keys
            Assert-Shortcuts
            if (@((Read-Ui) | Where-Object type -eq TabItem).Count -ne $initialTabs + 1) {
                throw 'Repeated shortcut use created an extra Settings tab.'
            }
            $results.Add(@{ layout = ('0x{0:X}' -f $layout.ToInt64()); keys = $keys; status = 'PASS' })
            Write-Output "PASS: $keys opens and reuses the shortcut reference from an address textbox."
            Keys 'ctrl+w'
            Keys 'escape'
        }
    }
    if ($results.Count -eq 0) { throw 'No installed layout could produce a slash.' }
    Ui @('screenshot', '--output', (Join-Path $ArtifactDirectory 'completed.png')) | Out-Null
}
catch {
    $results.Add(@{ status = 'FAIL'; detail = "$_" })
    throw
}
finally {
    $null = [ShortcutTestLayout]::PostMessageW($window, 0x0050, 0, $originalLayout)
    & winapp ui yield --on local | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    $results.ToArray() | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}
