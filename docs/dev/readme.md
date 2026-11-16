# Development

Requires Windows 10 version 2004 or later, the .NET 11 SDK, and Windows SDK 10.0.26100. Visual Studio 2026 (18.12 or later) with the WinUI workload is recommended for editing XAML. .NET 11 requires x86-64-v2 instructions on x64 and LSE on ARM64.

From the repository root:

```powershell
.\eng\Run.ps1
.\eng\Run.ps1 -RegistryPath 'HKCU\Software'
dotnet run --project tests/Regedit.Tests
```

Open `JPSoftworks.Regedit.slnx` in Visual Studio for the full solution.

GitHub Actions builds the solution in Release for x64 and runs the default unit test suite on pull requests and pushes to `master`. The suite uses a console runner (`dotnet run --project tests/Regedit.Tests`), not `dotnet test`. UI tests and opt-in registry/native integration tests are excluded from CI.

Build self-contained Native AOT releases for x64 and ARM64 with the MSVC Desktop development with C++ tools installed:

```powershell
.\Build-Release.ps1
```

The script clears `artifacts/release` and creates app and symbol ZIPs for each architecture there. Distribute `Regedit-win-<architecture>.zip`; retain `Regedit-win-<architecture>-symbols.zip` for debugging. Published satellite resources are limited to `en-US`.

For Inno Setup installers and production signing, see [Windows installers](../../installer/readme.md).

The default network-registry tests mock authentication and registry access. They do not display credential prompts, contact a server, or require an administrator account. Real Windows logon-token checks are opt-in because local security policies can restrict token creation:

```powershell
dotnet run --project tests/Regedit.Tests -- --suite network-registry-native
```

That native suite uses synthetic credentials without connecting to a server or requiring a configured test account.
