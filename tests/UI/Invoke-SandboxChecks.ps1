param(
    [Parameter(Mandatory)][string] $Base,
    [Parameter(Mandatory)][guid] $Run,
    [switch] $UiWorker,
    [string] $ActivateForSid,
    [string] $ExpectedAppSid
)

$ErrorActionPreference = 'Stop'
if ($Base -notmatch '^C:\\Regedit-[0-9a-f]{32}$' -or -not (Test-Path -LiteralPath "$Base-input\Invoke-SandboxChecks.ps1")) {
    throw 'Start this guest worker through eng/Test-Sandbox.ps1.'
}
if (-not $UiWorker -and [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -notmatch '-504$') {
    throw 'The Sandbox launcher must run as the built-in WDAGUtilityAccount in the guest.'
}
$output = "$Base-results\" + $Run.ToString('N')
$log = Join-Path $output 'guest.log'
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RegeditSandboxTools'))
$shippingUser = $null
$previewPrinter = $null
$previousPrinter = $null
$addedDriver = $false

if ($ActivateForSid) {
    # The signed-in desktop user can activate the secondary user's verified editor window.
    $point = Get-Content -LiteralPath "$output\activate.json" -Raw | ConvertFrom-Json
    $app = Get-Process -Id $point.Pid -ErrorAction Stop
    $owner = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $app.Id) | Invoke-CimMethod -MethodName GetOwnerSid
    $request = Get-Content -LiteralPath "$output\request.json" -Raw | ConvertFrom-Json
    $build = if ($request.Suite -eq 'NativeNotifications') { 'notifications-native' } elseif ($request.Suite -eq 'Notifications') { 'notifications' } elseif ($request.Suite -eq 'NativeUi') { 'ui-aot' } else { 'ui' }
    $expectedPath = Join-Path $cacheRoot "$build\regedit2.exe"
    if ($owner.Sid -ne $ActivateForSid -or $app.Path -ne $expectedPath -or
        [long]$app.MainWindowHandle -ne $point.Hwnd) { throw 'Unexpected isolated test window.' }
    . "$Base-input\UiTestProfile.ps1"
    if ($request.Suite -eq 'NativeUi') { Assert-ShippingUiBuild $app.Path } else { Assert-UiTestBuild $app.Path }
    . "$Base-input\UiTestWindow.ps1"
    [UxTestWindow]::TargetWindow = $app.MainWindowHandle
    [UxTestWindow]::ActivateAt($point.X, $point.Y)
    return
}

# Running from the shared folder leaves host build products locked by the guest.
function Copy-TestBuild([ValidateSet('registry', 'ui', 'ui-aot', 'notifications', 'notifications-native')][string] $Build) {
    $destination = [IO.Path]::GetFullPath((Join-Path $cacheRoot $Build))
    if ([IO.Path]::GetDirectoryName($destination) -ne $cacheRoot) { throw 'Invalid guest build cache directory.' }
    & robocopy.exe "$Base-input\builds\$Build" $destination /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Could not copy $Build to the guest disk." }
    $destination
}

try {
    $request = Get-Content -LiteralPath "$output/request.json" -Raw | ConvertFrom-Json
    $printCases = @('Native print preview opens for the selected key', 'Multipage print preview opens and cancels without printing')
    $isPrintPreview = $request.Suite -in 'Ui', 'NativeUi' -and [bool]($printCases -match $request.Filter)
    $env:Path = "$Base-shell;$env:Path"
    if ($request.Suite -in 'Registry', 'Edits', 'Fast') {
        $options = @()
        if ($request.Suite -ne 'Registry') { $options += '--browser-edits' }
        if ($request.Suite -ne 'Fast') { $options += '--registry' }
        foreach ($name in $request.ManagedSuites) {
            $options += '--suite', $name
        }
        if ($request.ListManagedSuites) {
            # List every available managed suite, rather than applying the legacy Edits alias.
            $options = @($options | Where-Object { $_ -ne '--browser-edits' })
            $options += '--list-suites'
        }
        $testExe = Join-Path (Copy-TestBuild registry) 'Regedit.Tests.exe'
        & $testExe @options *> $log
        exit $LASTEXITCODE
    }
    if ($request.Suite -notin 'Ui', 'NativeUi', 'NoteClose', 'Launch', 'TreeActions', 'Notifications', 'NativeNotifications') { throw "Unknown guest suite '$($request.Suite)'." }

    $pidFile = Join-Path $output 'ui-worker.pid'
    $build = if ($request.Suite -eq 'NativeNotifications') { 'notifications-native' } elseif ($request.Suite -eq 'Notifications') { 'notifications' } elseif ($request.Suite -eq 'NativeUi') { 'ui-aot' } else { 'ui' }
    $cli = Join-Path $cacheRoot 'winapp'
    if ($UiWorker) {
        [IO.File]::WriteAllText("$pidFile.tmp", [string]$PID)
        [IO.File]::Move("$pidFile.tmp", $pidFile)
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if ((New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'The UI checks require a standard-user token.'
        }
        if ($ExpectedAppSid) {
            if ($identity.User.Value -ne $ExpectedAppSid) { throw 'The isolated UI worker SID does not match its disposable account.' }
            $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$ExpectedAppSid"
            $profileImage = [Environment]::ExpandEnvironmentVariables((Get-ItemPropertyValue $profileKey ProfileImagePath))
            if ([IO.Path]::GetFullPath($profileImage).TrimEnd('\') -ine [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')) {
                throw 'The isolated worker did not load the registered profile for its SID.'
            }
            $localData = [Environment]::GetFolderPath('LocalApplicationData')
            if (-not [IO.Path]::GetFullPath($localData).StartsWith([IO.Path]::GetFullPath($profileImage), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The local application folder is outside the loaded disposable profile.'
            }
        }
        $env:Path = "$cli;$env:Path"
        $executable = Join-Path $cacheRoot "$build\regedit2.exe"
        $profile = if ($ExpectedAppSid) {
            Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("RegeditUiTests-" + $Run.ToString('N'))
        } else {
            Join-Path $env:TEMP ("RegeditProfile-" + $Run.ToString('N'))
        }
        if ($request.Suite -in 'Notifications', 'NativeNotifications') {
            & "$Base-input\Notifications.Sandbox.Tests.ps1" -Executable $executable -ProfileRoot $profile -SourceRoot "$Base-input\notification-source" -ArtifactDirectory $output -WinApp "$cli\winapp.exe" -SandboxRun $Run -TestFilter $request.Filter -ExpectNative:($request.Suite -eq 'NativeNotifications') *> $log
            $code = $LASTEXITCODE
            if ($ExpectedAppSid) { [IO.File]::WriteAllText("$output\shipping.exit", [string]$code) }
            exit $code
        }
        if ($request.Suite -eq 'Launch') {
            & "$Base-input\Launch.Ui.Tests.ps1" -Executable $executable -TestDataDirectory $profile -ResultsFile "$output\results.json" *> $log
            exit $LASTEXITCODE
        }
        if ($request.Suite -eq 'NativeUi') {
            $profile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RegeditModern'
        }
        if ($isPrintPreview) { (New-Object -ComObject WScript.Network).SetDefaultPrinter('Regedit preview ' + $Run.ToString('N')) }
        if ($request.Suite -eq 'NativeUi') {
            & "$Base-input\Investigations.Ui.Tests.ps1" -Executable $executable -TestDataDirectory $profile -SandboxRun $Run -ExpectedAppSid ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -ExpectedPreviewPrinter ('Regedit preview ' + $Run.ToString('N')) -ArtifactDirectory $output -TestFilter $request.Filter *> $log
            $code = $LASTEXITCODE
            [IO.File]::WriteAllText("$output\shipping.exit", [string]$code)
            exit $code
        }
        if ($request.Suite -eq 'TreeActions') {
            & "$Base-input\TreeActions.Ui.Tests.ps1" -Executable $executable -TestDataDirectory $profile -ArtifactDirectory $output *> $log
            exit $LASTEXITCODE
        }
        if ($request.Suite -eq 'NoteClose') {
            & "$Base-input\NoteClose.Ui.Tests.ps1" -Executable $executable -ArtifactDirectory $profile -TestFilter $request.Filter *> $log
            $code = $LASTEXITCODE
            Get-ChildItem -LiteralPath $profile -Filter '*.json' -File | Copy-Item -Destination $output
            exit $code
        }
        & "$Base-input\Investigations.Ui.Tests.ps1" -Executable $executable -ArtifactDirectory $output -TestDataDirectory $profile -ExpectedAppSid $ExpectedAppSid -ExpectedPreviewPrinter ('Regedit preview ' + $Run.ToString('N')) -TestFilter $request.Filter *> $log
        $code = $LASTEXITCODE
        if ($ExpectedAppSid) { [IO.File]::WriteAllText("$output\shipping.exit", [string]$code) }
        exit $code
    }

    if ($isPrintPreview) {
        $driver = 'Microsoft XPS Class Driver'
        if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {
            Add-PrinterDriver -Name $driver
            $addedDriver = $true
        }
        $device = Get-ItemPropertyValue 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' Device -ErrorAction SilentlyContinue
        if ($device) { $previousPrinter = ($device -split ',winspool,', 2)[0] }
        $name = 'Regedit preview ' + $Run.ToString('N')
        Add-Printer -Name $name -DriverName $driver -PortName 'PORTPROMPT:'
        $previewPrinter = $name
        (New-Object -ComObject WScript.Network).SetDefaultPrinter($name)
    }

    $null = Copy-TestBuild $build
    & robocopy.exe "$Base-input\winapp" $cli /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not cache WinApp CLI on the guest disk.' }
    $shellExecutable = "$Base-shell\pwsh.exe"
    if ($request.Suite -in 'Notifications', 'NativeNotifications') {
        $shellCache = Join-Path $cacheRoot 'notification-shell'
        & robocopy.exe "$Base-shell" $shellCache /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Could not cache the notification test shell.' }
        $shellExecutable = Join-Path $shellCache 'pwsh.exe'
    }
    $arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $PSCommandPath -Base $Base -Run $Run -UiWorker"
    $isDebugPrint = $request.Suite -eq 'Ui' -and $isPrintPreview
    if ($request.Suite -in 'NativeUi', 'Notifications', 'NativeNotifications' -or $isDebugPrint) {
        # A disposable standard profile gives UI tests their own desktop identity.
        $account = 'RegeditUi' + $Run.ToString('N').Substring(0, 10)
        $password = ConvertTo-SecureString (([guid]::NewGuid().ToString('N')) + 'aA!7') -AsPlainText -Force
        $shippingUser = New-LocalUser -Name $account -Password $password -Description 'Disposable Regedit UI test'
        $arguments = "$arguments -ExpectedAppSid $($shippingUser.SID.Value)"
        $credential = [pscredential]::new("$env:COMPUTERNAME\$account", $password)
        $worker = Start-Process -FilePath $shellExecutable -ArgumentList $arguments -WorkingDirectory $env:WINDIR -Credential $credential -LoadUserProfile -WindowStyle Hidden -PassThru -RedirectStandardOutput "$output\worker.stdout" -RedirectStandardError "$output\worker.stderr"
        $activation = Join-Path $output 'activate.json'
        while (-not $worker.WaitForExit(200)) {
            if (-not [IO.File]::Exists($activation)) { continue }
            $point = Get-Content -LiteralPath $activation -Raw | ConvertFrom-Json
            $ack = Join-Path $output ("activation-{0}-{1}.exit" -f [int]$point.Pid, [long]$point.Hwnd)
            try {
                & "$Base-shell\pwsh.exe" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $PSCommandPath -Base $Base -Run $Run -ActivateForSid $shippingUser.SID.Value *>> "$output\activation.log"
                $activationCode = $LASTEXITCODE
            } catch {
                $_ | Out-String | Add-Content -LiteralPath "$output\activation.log"
                $activationCode = 1
            }
            [IO.File]::Delete($activation)
            [IO.File]::WriteAllText("$ack.tmp", [string]$activationCode)
            [IO.File]::Move("$ack.tmp", $ack)
        }
        if (-not (Test-Path -LiteralPath "$output\shipping.exit")) { throw 'The isolated UI worker exited without a result.' }
        exit [int][IO.File]::ReadAllText("$output\shipping.exit")
    }
    # The isolated Debug executable supports an explicit per-run profile.
    $command = "$Base-shell\pwsh.exe $arguments"
    & runas.exe /trustlevel:0x20000 $command | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not start the standard-user UI worker (exit $LASTEXITCODE)." }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $pidFile)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The standard-user UI worker did not start.' }
        Start-Sleep -Milliseconds 100
    }
    $worker = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($pidFile))
    $worker.WaitForExit()
    exit $worker.ExitCode
}
catch {
    $_ | Out-String | Add-Content -LiteralPath $log
    exit 1
}
finally {
    if ($shippingUser) {
        Remove-LocalUser -SID $shippingUser.SID
        $userProfile = Get-CimInstance Win32_UserProfile -Filter ("SID = '" + $shippingUser.SID.Value + "'")
        if ($userProfile -and -not $userProfile.Loaded -and -not $userProfile.Special) { $userProfile | Remove-CimInstance }
    }
    if ($previewPrinter) {
        if ($previousPrinter -and (Get-Printer -Name $previousPrinter -ErrorAction SilentlyContinue)) {
            (New-Object -ComObject WScript.Network).SetDefaultPrinter($previousPrinter)
        }
        Remove-Printer -Name $previewPrinter
    }
    if ($addedDriver -and -not (Get-Printer | Where-Object DriverName -eq $driver)) {
        Remove-PrinterDriver -Name $driver
    }
}
