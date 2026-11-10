param(
    [Parameter(Mandatory)]
    [int] $AppPid,
    [string] $ArtifactDirectory = (Join-Path $PSScriptRoot "../../artifacts/tree-view-ui/$([guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiTestProfile.ps1')
Assert-UiTestProcess $AppPid
Assert-UiTestPath $ArtifactDirectory
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null

function Invoke-Ui([string[]] $Arguments) {
    $output = @(& winapp ui @Arguments --on local -a $AppPid)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    $output
}

function Get-Elements($Nodes) {
    foreach ($node in $Nodes) {
        if ($null -eq $node) { continue }
        $node
        if ($node.children) { Get-Elements $node.children }
    }
}

function Read-Tree([string] $Name, [switch] $AllowEmpty) {
    $inspection = Invoke-Ui @('inspect', '-d', '12', '--hide-offscreen', '--json') | ConvertFrom-Json
    $inspection | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory "$Name.json") -Encoding utf8
    $elements = @(Get-Elements @($inspection.windows | ForEach-Object { $_.elements }))
    $items = @($elements | Where-Object { $_.type -eq 'TreeItem' })
    if ($items.Count -eq 0 -and -not $AllowEmpty) { throw 'No realized registry tree items were inspected.' }
    return ,$items
}

function Assert-Labels($Items) {
    foreach ($item in $Items) {
        if ($item.name -ne 'Computer' -and $item.name -notlike 'HKEY_*') { throw "Missing registry path on tree item '$($item.name)'." }
        $expected = ($item.name -split '\\')[-1]
        $labels = @(Get-Elements $item.children | Where-Object { $_.type -eq 'Text' -and $_.className -eq 'TextBlock' } | ForEach-Object { $_.name })
        if ($labels.Count -ne 1 -or $labels[0] -cne $expected) {
            throw "Tree node $($item.name) displays '$($labels -join ' | ')' instead of '$expected'."
        }
    }
}

function Invoke-Tree([string] $Path, [string] $Action) {
    $items = Read-Tree 'latest'
    $item = @($items | Where-Object { $_.name -eq $Path })
    if ($item.Count -ne 1) { throw "Expected one tree item for $Path, found $($item.Count)." }
    Invoke-Ui @('invoke', $item[0].selector, '--action', $Action) | Out-Null
}

function Wait-Tree([string] $Path) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $items = Read-Tree 'latest' -AllowEmpty
        if ($items.name -contains $Path) { return }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw "Tree node did not appear: $Path"
}

function Assert-Children($Items, [Microsoft.Win32.RegistryHive] $Hive, [string] $Root) {
    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, [Microsoft.Win32.RegistryView]::Registry64)
    try { $expected = @($key.GetSubKeyNames() | ForEach-Object { "$Root\$_" }) }
    finally { $key.Dispose() }
    $children = @($Items | Where-Object { $_.name.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase) })
    if ($children.Count -ne $expected.Count) { throw "$Root displays $($children.Count) children; the registry contains $($expected.Count)." }
    foreach ($child in $children) {
        if ($child.name -notin $expected) { throw "Unexpected child under ${Root}: $($child.name)" }
    }
}

function Save-Screenshot([string] $Name) {
    $file = Join-Path $ArtifactDirectory "$Name.png"
    if (Test-Path -LiteralPath $file) { throw "Evidence already exists: $file" }
    Invoke-Ui @('screenshot', '--output', $file) | Out-Null
    if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Screenshot was not captured: $file" }
    $screenshots.Add($file)
}

function Test-Tree([string] $Name, [scriptblock] $Check) {
    try {
        & $Check
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Output "PASS: $Name"
    }
    catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
        Write-Output "FAIL: $Name - $_"
        throw
    }
}

try {
    Invoke-Ui @('wait-for', 'RegistryTree', '-t', '15000') | Out-Null
    $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
    if (@($windows | Where-Object { $_.className -eq 'WinUIDesktopWin32WindowClass' }).Count -ne 1) {
        throw 'Expected exactly one local WinUI registry editor window.'
    }
    foreach ($root in @('HKEY_CLASSES_ROOT', 'HKEY_CURRENT_USER', 'HKEY_LOCAL_MACHINE', 'HKEY_USERS', 'HKEY_CURRENT_CONFIG')) {
        Invoke-Tree $root 'collapse'
    }
    Test-Tree 'Initial root labels' { Assert-Labels (Read-Tree '01-roots') }

    Test-Tree 'Classes root rows after scrolling' {
        $classes = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::ClassesRoot, [Microsoft.Win32.RegistryView]::Registry64)
        try { $classNames = $classes.GetSubKeyNames(); [Array]::Sort($classNames, [StringComparer]::OrdinalIgnoreCase); $firstClass = $classNames[0] }
        finally { $classes.Dispose() }
        $loadWatch = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Tree 'HKEY_CLASSES_ROOT' 'expand'
        Wait-Tree "HKEY_CLASSES_ROOT\$firstClass"
        Write-Output "Classes Root: $($classNames.Length) children ready in $($loadWatch.ElapsedMilliseconds) ms, including UI automation."
        Invoke-Ui @('scroll', 'ListControl', '--to', 'bottom') | Out-Null
        Invoke-Ui @('scroll', 'ListControl', '--to', 'top') | Out-Null
        Assert-Labels (Read-Tree '02-classes-scrolled')
        Invoke-Tree 'HKEY_CLASSES_ROOT' 'collapse'
    }

    Test-Tree 'Users children after classes root is collapsed' {
        Invoke-Tree 'HKEY_USERS' 'expand'
        Wait-Tree 'HKEY_USERS\.DEFAULT'
        $items = Read-Tree '03-users-after-recycling'
        Assert-Labels $items
        Assert-Children $items ([Microsoft.Win32.RegistryHive]::Users) 'HKEY_USERS'
        Save-Screenshot '03-users-after-recycling'
        Invoke-Tree 'HKEY_USERS' 'collapse'
    }

    Test-Tree 'Current config children after recycling' {
        Invoke-Tree 'HKEY_CURRENT_CONFIG' 'expand'
        Wait-Tree 'HKEY_CURRENT_CONFIG\Software'
        $items = Read-Tree '04-current-config'
        Assert-Labels $items
        Assert-Children $items ([Microsoft.Win32.RegistryHive]::CurrentConfig) 'HKEY_CURRENT_CONFIG'
        Save-Screenshot '04-current-config'
        Invoke-Tree 'HKEY_CURRENT_CONFIG' 'collapse'
    }

    Test-Tree 'Repeated users expansion keeps correct labels' {
        Invoke-Tree 'HKEY_USERS' 'expand'
        $items = Read-Tree '05-users-reexpanded'
        Assert-Labels $items
        Assert-Children $items ([Microsoft.Win32.RegistryHive]::Users) 'HKEY_USERS'
        Save-Screenshot '05-users-reexpanded'
    }
}
catch {
    if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -eq 0) {
        $results.Add(@{ name = 'UI test setup'; status = 'FAIL'; detail = "$_" })
    }
}
finally {
    try { & winapp ui yield --on local | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    $report = @{ results = @($results.ToArray()); screenshots = @($screenshots.ToArray()); appPid = $AppPid; target = 'local' }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
    Write-Output "Results: $ArtifactDirectory"
}
if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
