// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class PowerShellRunTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        CheckDiscoveryAndSettings(check);

        var target = RegistryLocation.Parse(@"HKCU\Software\RegeditTest");
        var success = await RunScriptAsync(new PowerShellRun("$null = 2 + 2", [target]));
        check(success.Succeeded && success.Error is null && success.Targets.SequenceEqual([target]),
            "An approved script reports success and preserves its registry scope");

        var source = $"#{new string('x', 40000)}\r\nthrow 'expected script failure'";
        var failedRun = new PowerShellRun(source, [target]);
        check(!failedRun.StartupCommand.Contains("expected script failure", StringComparison.Ordinal),
            "Script data stays out of the PowerShell command line");
        var failure = await RunScriptAsync(failedRun);
        check(!failure.Succeeded && failure.Error == "expected script failure" && failure.Targets.SequenceEqual([target]),
            "A script larger than the Windows command-line limit returns its failure and scope");
    }

    private static void CheckDiscoveryAndSettings(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditTerminalSettings.");
        try
        {
            var settingsFile = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(settingsFile, "{}");
            var settings = AppSettings.Load(settingsFile);
            check(settings.PowerShellPath == string.Empty && settings.OfferWindowsPowerShellFallback,
                "Existing settings default to PowerShell 7 discovery with an offered fallback");

            var path = Path.Combine(directory.FullName, "pwsh.exe");
            var model = new SettingsViewModel(settings, change => settings.UpdatePreferences(change, settingsFile));
            model.PowerShellPath = $"  {path}  ";
            model.OfferWindowsPowerShellFallback = false;
            var reloaded = AppSettings.Load(settingsFile);
            check(reloaded.PowerShellPath == path && !reloaded.OfferWindowsPowerShellFallback,
                "Terminal preferences are trimmed and persisted across app sessions");
            check(PowerShellDiscovery.FindPowerShell7(path, false, out var error, out _) is null &&
                  error?.Contains("configured", StringComparison.Ordinal) == true,
                "A missing configured executable reports the error without falling back");
            File.WriteAllText(path, "not an executable");
            check(PowerShellDiscovery.FindPowerShell7(path, false, out error, out _) is null && error is not null,
                "Discovery rejects a file that cannot be launched as PowerShell 7");
            check(PowerShellDiscovery.FindPowerShell7("pwsh.exe", false, out error, out _) is null && error is not null,
                "Discovery does not search PATH for a relative executable");

            var use32Bit = !Environment.Is64BitOperatingSystem;
            if (PowerShellDiscovery.FindPowerShell7(string.Empty, use32Bit, out _, out var version) is { } installed)
            {
                check(PowerShellDiscovery.FindPowerShell7($"\"{installed}\"", use32Bit, out error, out var configuredVersion)
                          == installed && error is null && version is { Length: > 0 } && configuredVersion == version,
                    "Discovery accepts a quoted executable path and preserves the selected version");
                check(PowerShellDiscovery.FindPowerShell7(installed, !use32Bit, out error, out _) is null &&
                      error?.Contains("registry view", StringComparison.Ordinal) == true,
                    "Discovery rejects PowerShell of the wrong width for the registry view");
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task<PowerShellRunResult> RunScriptAsync(PowerShellRun run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = run.DeliverAsync(deadline.Token);
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(run.StartupCommand))
            }
        };
        using var shell = Process.Start(start)!;
        var errors = shell.StandardError.ReadToEndAsync(deadline.Token);
        var output = shell.StandardOutput.ReadToEndAsync(deadline.Token);
        try
        {
            await shell.WaitForExitAsync(deadline.Token);
            if (shell.ExitCode != 0)
            {
                throw new InvalidOperationException(await errors + await output);
            }

            return await result;
        }
        finally
        {
            await deadline.CancelAsync();
            if (!shell.HasExited)
            {
                shell.Kill(true);
            }
        }
    }
}
