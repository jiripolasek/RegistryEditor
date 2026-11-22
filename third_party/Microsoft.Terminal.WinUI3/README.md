# WinUI terminal renderer

Regedit builds the managed WinUI renderer from source when
`RegeditExperimentalTerminal` is enabled. The EasyWindowsTerminalControl package
supplies the ConPTY session code, and the pinned unofficial renderer package
supplies the native terminal DLL for the target architecture.

The WinUI sources come from [EasyWindowsTerminalControl revision 0741a4b](https://github.com/mitchcapper/EasyWindowsTerminalControl/tree/0741a4b8853c47bcac4412d005ed4ae1d96d2c13/Microsoft.Terminal.WinUI3).
The terminal interop contracts come from its [pinned Terminal submodule](https://github.com/mitchcapper/Terminal/tree/922beefb83764646331662d6d15d70107d556402).
Regedit's changes enable CsWinRT code generation and Native AOT marshalling,
prevent recursive UI Automation peer creation, and guard the HWND lifetime
during teardown. The host uses Win32 subclass callbacks for keyboard, DPI,
and child window messages. See [the terminal notices](../../NOTICE.Terminal.md) for attribution.

From the repository root on Windows, publish the experimental x64 build with
Native AOT enabled.

```powershell
dotnet publish src/Regedit/Regedit.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishAot=true -p:RegeditExperimentalTerminal=true -o artifacts/regedit-terminal
Start-Process -FilePath .\artifacts\regedit-terminal\regedit2.exe
```

Open PowerShell with **Ctrl+;** or **Tools → PowerShell**. PowerShell 7 can be
configured in **Settings → Terminal**.

Add `-p:RegeditUseSystemConPty=true` to the publish command to test the Windows
ConPTY backend. Use a separate output directory when comparing builds. The
default experimental build uses bundled ConPTY. Launching that build through
the Windows shell avoids inheriting redirected streams from a command runner.
