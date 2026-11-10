param(
    [Parameter(Mandatory)]
    [int] $AppPid,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/layout-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestPath $ArtifactDirectory
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

$json = & winapp ui inspect -a $AppPid -d 12 --hide-offscreen --json --on local
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the app.' }
$json | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'inspection.json') -Encoding utf8
$inspection = $json | ConvertFrom-Json
$windows = @($inspection.windows | Where-Object {
    $_.className -eq 'WinUIDesktopWin32WindowClass' -or $_.elements[0].className -eq 'WinUIDesktopWin32WindowClass'
})
if ($windows.Count -ne 1) { throw 'Expected one app window.' }
$window = $windows[0]
$elements = @(Get-Elements $window.elements)
$root = $elements | Where-Object { $_.type -eq 'Window' } | Select-Object -First 1
$client = $elements | Where-Object { $_.className -eq 'Microsoft.UI.Content.DesktopChildSiteBridge' } | Select-Object -First 1
$status = $elements | Where-Object { $_.automationId -eq 'AccessStatus' } | Select-Object -First 1
$list = $elements | Where-Object { $_.automationId -eq 'EntriesList' } | Select-Object -First 1
$tree = $elements | Where-Object { $_.automationId -eq 'RegistryTree' } | Select-Object -First 1
$new = $elements | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'New' } | Select-Object -First 1
$more = $elements | Where-Object { $_.automationId -eq 'MoreButton' } | Select-Object -First 1
$back = $elements | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Back' } | Select-Object -First 1
$editAddress = $elements | Where-Object { $_.automationId -eq 'EditAddressButton' } | Select-Object -First 1
$search = $elements | Where-Object { $_.automationId -eq 'QuickSearch' } | Select-Object -First 1
$tools = $elements | Where-Object { $_.automationId -eq 'ToolsButton' } | Select-Object -First 1
$tab = $elements | Where-Object { $_.type -eq 'TabItem' } | Select-Object -First 1
$menu = $elements | Where-Object { $_.automationId -eq 'GlobalMenuButton' } | Select-Object -First 1
$addTab = $elements | Where-Object { $_.automationId -eq 'AddButton' } | Select-Object -First 1
$minimize = $elements | Where-Object { $_.automationId -eq 'Minimize' } | Select-Object -First 1
$shield = $elements | Where-Object { $_.automationId -eq 'AdministratorShield' } | Select-Object -First 1
if (-not $root -or -not $client -or -not $status -or -not $list -or -not $new -or -not $more -or -not $back -or -not $editAddress -or -not $tab -or -not $menu -or -not $addTab -or -not $minimize) { throw 'Missing required layout elements.' }
$scale = $window.scale
$bottom = $root.y + $root.height
$results = @(
    @{ name = 'Status bar stays at window bottom'; passed = [Math]::Abs($bottom - $status.y - $status.height) -le 24 * $scale },
    @{ name = 'Value list fills the available height'; passed = $status.y - $list.y - $list.height -ge 0 -and $status.y - $list.y - $list.height -le 24 * $scale },
    @{ name = 'Toolbar aligns with the value pane'; passed = [Math]::Abs($new.x - $list.x) -le 16 * $scale },
    @{ name = 'List toolbar spans the list with overflow at the right edge'; passed = [Math]::Abs(($more.x + $more.width) - ($list.x + $list.width)) -le 16 * $scale -and $more.x -gt $new.x },
    @{ name = 'Navigation spans the full tab'; passed = $back.x - $client.x -ge 0 -and $back.x - $client.x -le 24 * $scale -and $client.x + $client.width - $menu.x - $menu.width -ge 0 -and $client.x + $client.width - $menu.x - $menu.width -le 24 * $scale },
    @{ name = 'Search and Tools remain visible'; passed = $search -and $tools -and $search.width -gt 100 * $scale -and $tools.x -gt $search.x },
    @{ name = 'Application menu sits beside Tools'; passed = [Math]::Abs($menu.y - $tools.y) -le 2 * $scale -and $menu.x -ge $tools.x + $tools.width -and $menu.x - $tools.x - $tools.width -le 8 * $scale },
    @{ name = 'Tabs occupy the native title bar'; passed = [Math]::Abs($tab.y - $minimize.y) -le 12 * $scale -and $tab.y + $tab.height -le $back.y },
    @{ name = 'Caption buttons have reserved space'; passed = $addTab.x + $addTab.width -le $minimize.x - 48 * $scale },
    @{ name = 'Administrator shield matches process access'; passed = if ($status.name -eq 'Administrator') { $shield -and $shield.x + $shield.width -le $tab.x } else { -not $shield } }
)
# Native client bounds stay accurate when collapsed XAML controls retain old automation bounds.
if ($client.width / $scale -ge 520) {
    $results += @{ name = 'Tree fills the available height'; passed = $tree -and $bottom - $tree.y - $tree.height -ge 0 -and $bottom - $tree.y - $tree.height -le 32 * $scale }
}
else {
    $results += @{ name = 'Navigation pane hides in the compact layout'; passed = -not $tree }
}
$screenshot = Join-Path $ArtifactDirectory 'window.png'
& winapp ui screenshot -a $AppPid --output $screenshot --on local | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not capture the app.' }
@{ appPid = $AppPid; width = $root.width; height = $root.height; treeWidth = $tree.width; scale = $scale; results = $results; screenshot = $screenshot } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
foreach ($result in $results) {
    $label = if ($result.passed) { 'PASS' } else { 'FAIL' }
    Write-Output "${label}: $($result.name)"
}
Write-Output "Results: $ArtifactDirectory"
if ($results.Where({ -not $_.passed }).Count -gt 0) { exit 1 }
