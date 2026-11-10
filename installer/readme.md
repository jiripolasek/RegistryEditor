# Windows installers

Requires the release build prerequisites and Inno Setup 6.3 or later. The script publishes self-contained x64 and ARM64 payloads and reads `Version` and `Company` from the evaluated project.

Local testing, without production signatures:

```powershell
.\installer\Build-Installer.ps1 -Unsigned
```

Test installers are named `*-unsigned.exe` in `artifacts/installers/unsigned`. Production installers go to `artifacts/installers/signed`, one EXE per architecture. Each run replaces its selected output folder. Use `-IsccPath` or `-SignToolPath` if a tool is not found.

Production signing uses an OV/EV certificate in the Windows certificate store, or Azure Trusted Signing. Pass credentials and configuration paths at invocation time; keep them outside the repository:

```powershell
.\installer\Build-Installer.ps1 -CertificateThumbprint $thumbprint -TimestampUrl $timestampUrl
# Add -MachineCertificateStore for a certificate in LocalMachine\My.
.\installer\Build-Installer.ps1 -TrustedSigningDll $signingDll -TrustedSigningMetadata $metadataFile -TimestampUrl $timestampUrl
```

The script signs every unsigned executable, DLL, MUI, and WinMD file before packaging and verifies files that are already signed. It uses Inno's SignTool integration to sign Setup, its temporary copies, and the uninstaller with SHA-256 and RFC 3161 timestamps. It rejects invalid and self-signed leaf signatures. Production certificates must chain to a CA in the [Microsoft Trusted Root Program](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements); local Windows trust alone does not establish Store eligibility. Signing remains to be configured and validated with a production certificate.

For Partner Center, host each signed installer at its own versioned HTTPS URL and specify these silent switches:

```text
/VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

New installations default to per-user installation in `%LOCALAPPDATA%\Programs\JPSoftworks\Modern Registry Editor`; `/CURRENTUSER` can select it explicitly. Interactive setup can select a per-machine install in `%ProgramFiles%\JPSoftworks\Modern Registry Editor`, or use `/ALLUSERS`. Upgrades retain the previous installation directory and install mode. Silent installation never launches the app. Uninstall removes the Start-menu shortcut and the protocol key from the install mode's registry hive; user settings are retained.

Per-user app files can be replaced by unelevated processes running as that user. Since the editor can run as administrator, choose a per-machine install when its executable and DLLs should be protected by Program Files permissions.

Close all editor windows before updating, especially administrator windows. A per-user installer cannot close an elevated instance and may fail to replace files while that window remains open.
