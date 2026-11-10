function Assert-UiTestBuild([string] $Executable) {
    if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf) -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Executable)).ProductName -ne 'Regedit UI test build') {
        throw 'Pass the regedit2.exe built with -p:RegeditUiTests=true in a separate output directory.'
    }
    $assembly = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($Executable), '.dll')
    if ([IO.File]::Exists($assembly) -and [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductName -ne 'Regedit UI test build') {
        throw 'The executable and managed entry assembly must be from the same RegeditUiTests build.'
    }
}

function Assert-ShippingUiBuild([string] $Executable) {
    if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf) -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Executable)).ProductName -notin @('regedit2', 'Modern Registry Editor') -or
        [IO.File]::Exists([IO.Path]::ChangeExtension($Executable, '.dll'))) {
        throw 'Pass the published shipping executable to the Sandbox account check.'
    }
}

function Assert-ShippingUiAccount([guid] $SandboxRun) {
    $account = 'RegeditUi' + $SandboxRun.ToString('N').Substring(0, 10)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($identity.Name -ne "$env:COMPUTERNAME\$account" -or
            [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Shipping UI checks require the fresh standard account created for this Windows Sandbox run.'
        }
        return $identity.User.Value
    } finally { $identity.Dispose() }
}

function Assert-UiTestProcess([int] $AppPid, [string] $Target = 'local', [guid] $SandboxRun = [guid]::Empty) {
    if ($SandboxRun -ne [guid]::Empty) {
        $sid = Assert-ShippingUiAccount $SandboxRun
        if ($Target -ne 'local' -or (Get-CimInstance Win32_Process -Filter "ProcessId = $AppPid" | Invoke-CimMethod -MethodName GetOwnerSid).Sid -ne $sid) {
            throw 'The shipping test process must belong to this disposable Windows account.'
        }
        Assert-ShippingUiBuild (Get-Process -Id $AppPid -ErrorAction Stop).Path
        return
    }
    if ($Target -eq 'local') {
        Assert-UiTestBuild (Get-Process -Id $AppPid -ErrorAction Stop).Path
        return
    }
    $check = 'function Assert-UiTestBuild { ' + ${function:Assert-UiTestBuild}.ToString() + " }`n" +
        "Assert-UiTestBuild (Get-Process -Id $AppPid -ErrorAction Stop).Path"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($check))
    & winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The selected Sandbox process must be the isolated RegeditUiTests build.' }
}

function Assert-UiTestPath([string] $Path, [switch] $ForProfile, [guid] $SandboxRun = [guid]::Empty) {
    $fullPath = [IO.DirectoryInfo]::new($Path)
    $normal = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RegeditModern'
    $reserved = @(Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'RegeditModern.AdministratorJournal')
    if ($SandboxRun -eq [guid]::Empty) {
        $reserved += $normal
    } else {
        $null = Assert-ShippingUiAccount $SandboxRun
        if ($fullPath.FullName -ne $normal) { throw 'Use the application data folder of the disposable Windows account.' }
    }
    for ($dir = $fullPath; $dir; $dir = $dir.Parent) {
        if ($dir.FullName.TrimEnd('\') -in $reserved) { throw 'The normal application profile and administrator journal are reserved.' }
        if ($ForProfile -and $dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Use a disposable profile without directory links.' }
    }
}

function New-UiTestProfile([string] $TestDataDirectory, [guid] $SandboxRun = [guid]::Empty) {
    if (-not $TestDataDirectory -or -not [IO.Path]::IsPathFullyQualified($TestDataDirectory)) {
        throw 'Pass an absolute -TestDataDirectory for a fresh disposable profile.'
    }
    $path = [IO.DirectoryInfo]::new($TestDataDirectory)
    Assert-UiTestPath $path.FullName -ForProfile -SandboxRun $SandboxRun
    if (Test-Path -LiteralPath $path.FullName) { throw 'Use a new test profile. The requested path already exists.' }
    (New-Item -ItemType Directory -Path $path.FullName -ErrorAction Stop).FullName
}
