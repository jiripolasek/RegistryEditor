// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit;
using JPSoftworks.Regedit.Core.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class LaunchTests
{
    public static void Run(Action<bool, string> check)
    {
        const string installedExecutable = @"C:\Program Files\Modern Registry Editor\regedit2.exe";
        check(
            RegistryLaunch.ProtocolCommandTargetsExecutable($"\"{installedExecutable}\" \"%1\"", installedExecutable),
            "Installer protocol registration targets the exact executable and passes the URI");
        check(
            RegistryLaunch.ProtocolCommandTargetsExecutable(
                $"  \"{installedExecutable.ToUpperInvariant()}\"   \"%1\"  ", installedExecutable),
            "Installer protocol comparison ignores Windows path casing and command whitespace");
        const string userExecutable
            = @"C:\Users\Jiří Polášek\AppData\Local\Programs\Modern Registry Editor\regedit2.exe";
        check(
            RegistryLaunch.ProtocolCommandTargetsExecutable($"\"{userExecutable}\" \"%1\"", userExecutable),
            "Per-user installer registration preserves Unicode paths and spaces");
        foreach (var command in new string?[]
                 {
                     null, string.Empty, " ", $"\"{installedExecutable}.other.exe\" \"%1\"",
                     "\"C:\\Source\\regedit2.exe\" \"%1\"", $"\"{installedExecutable}\"",
                     $"\"{installedExecutable}\" \"%1\" --32", $"\"{installedExecutable}\" \"%2\"",
                     $"\"{installedExecutable}\" \"%1\"\0"
                 })
        {
            check(
                !RegistryLaunch.ProtocolCommandTargetsExecutable(command, installedExecutable),
                "Missing, different or malformed protocol commands retain source-build registration: " + command);
        }

        check(
            !RegistryLaunch.ProtocolCommandTargetsExecutable($"\"{installedExecutable}\" \"%1\"", null),
            "An unavailable process path cannot match an installed protocol command");

        var elevationTarget = RegistryLocation.Parse("HKCU\\Software\\Trailing key ", RegistryView.Registry32);
        var elevation = RegistryLaunch.CreateElevationStartInfo("regedit2.exe", elevationTarget);
        check(
            elevation.UseShellExecute && elevation.Verb == "runas" && elevation.FileName == "regedit2.exe" &&
            elevation.ArgumentList.SequenceEqual([elevationTarget.Address, "--32"]),
            "Explicit elevation uses OS runas with only the exact captured path and 32-bit view");
        var elevation64 = RegistryLaunch.CreateElevationStartInfo(
            "regedit2.exe", elevationTarget with { View = RegistryView.Registry64 });
        check(
            elevation64.ArgumentList.SequenceEqual([elevationTarget.Address]),
            "64-bit elevation adds no view switch or editor draft arguments");
        check(
            RegistryLaunch.StandardAccessCommandLine(@"C:\Program Files\Regedit\regedit2.exe", elevationTarget) ==
            "\"C:\\Program Files\\Regedit\\regedit2.exe\" \"HKEY_CURRENT_USER\\Software\\Trailing key \" --32\0",
            "Standard access quotes the executable and exact path and preserves the 32-bit view");
        check(
            RegistryLaunch.StandardAccessCommandLine(
                "regedit2.exe", RegistryLocation.Parse(@"\\SERVER\HKLM\Software")) ==
            "\"regedit2.exe\" \"\\\\SERVER\\HKEY_LOCAL_MACHINE\\Software\"\0",
            "Standard access preserves remote paths without adding a 64-bit switch");
        check(
            RegistryLaunch.StandardAccessCommandLine(
                "regedit2.exe", new RegistryLocation(RegistryHive.CurrentUser, "Quoted \"key\"")) ==
            "\"regedit2.exe\" \"HKEY_CURRENT_USER\\Quoted \\\"key\\\"\"\0",
            "Standard access escapes quotes in registry key names");
        check(
            RegistryLaunch.StandardAccessCommandLine("regedit2.exe", RegistryLocation.Parse(@"\\SERVER\")) ==
            "\"regedit2.exe\" \"\\\\SERVER\"\0", "Standard access preserves a remote computer overview");
        try
        {
            RegistryLaunch.StandardAccessCommandLine(
                "regedit2.exe",
                new RegistryLocation(
                    RegistryHive.CurrentUser, string.Join('\\', Enumerable.Repeat(new string('a', 200), 6))));
            check(false, "Standard access rejects command lines beyond the Windows API limit");
        }
        catch (ArgumentException)
        {
            check(true, "Standard access rejects command lines beyond the Windows API limit");
        }

        check(RegistryLaunch.ParseArguments([]) is null, "No launch path restores the session");
        foreach (var path in new[]
                 {
                     @"HKCU\Software", @"HKEY_CURRENT_USER\Software", @"Computer\HKCU\Software",
                     "\"HKCU\\Software\"", @"HKCU:\Software", @"Registry::HKEY_CURRENT_USER\Software",
                     @"[HKEY_CURRENT_USER\Software]", @"'HKCU:\Software'"
                 })
        {
            check(
                RegistryLaunch.ParseArguments([path, "--32"]) == RegistryLocation.Parse(
                    @"HKCU\Software", RegistryView.Registry32),
                "Launch accepts registry path formats and preserves the view: " + path);
        }

        check(
            RegistryLaunch.ParseArguments([@"HKCU\Software\My App", "--32"]) == RegistryLocation.Parse(
                @"HKCU\Software\My App", RegistryView.Registry32), "Launch preserves spaces and 32-bit view");
        check(
            RegistryLaunch.ParseArguments(["--32", @"\\SERVER\HKLM\Software"]) == RegistryLocation.Parse(
                @"\\SERVER\HKLM\Software", RegistryView.Registry32),
            "Launch preserves remote paths and leading view option");
        check(RegistryLaunch.ParseArguments(["Computer"])!.IsComputer, "Launch accepts computer overview");
        check(
            RegistryLaunch.ParseArguments(["HKCU\\Tail ", "--32"]) == new RegistryLocation(
                RegistryHive.CurrentUser, "Tail ", View: RegistryView.Registry32),
            "Command-line paths preserve trailing key spaces after shell quote removal");
        check(
            RegistryLaunch.ParseArguments(["\\\\SERVER\\HKLM\\ "]) ==
            new RegistryLocation(RegistryHive.LocalMachine, " ", "SERVER"),
            "Remote launch paths preserve whitespace-only key names");

        foreach (var uri in new[]
                 {
                     "regedit2://HKCU/Software", "REGEDIT2://hkey_current_user/Software/",
                     @"regedit2://HKCU\Software"
                 })
        {
            check(
                RegistryLaunch.ParseUri(uri) == RegistryLocation.Parse(@"HKCU\Software"),
                "Registry URI accepts aliases, case and separators: " + uri);
        }

        check(
            RegistryLaunch.ParseUri("regedit2://HKLM/Software?view=32").View == RegistryView.Registry32,
            "URI selects 32-bit registry view");
        check(
            RegistryLaunch.ParseArguments(["--32", "regedit2://HKLM/Software?view=64"])!.View ==
            RegistryView.Registry64, "Explicit URI view overrides the command-line default");
        check(
            RegistryLaunch.ParseArguments(["regedit2://HKCU/Software", "--32"])!.View == RegistryView.Registry32,
            "Command line accepts a URI and registry view");
        check(RegistryLaunch.ParseUri("regedit2://Computer").IsComputer, "URI accepts local computer overview");
        check(
            RegistryLaunch.ParseUri("regedit2://HKCU/Software/My%20App/%C4%8Desk%C3%BD%20%F0%9F%90%88").SubKey ==
            "Software\\My App\\český 🐈", "URI decodes spaces and Unicode");
        check(
            RegistryLaunch.ParseUri("regedit2://HKCU/A%2FB%23C%3FD%25E+F%2520").SubKey == "A/B#C?D%E+F%20",
            "URI preserves escaped separators, reserved characters and literal plus signs with one decoding pass");
        check(
            RegistryLaunch.ParseUri("regedit2://HKCU/./../Tail%20").SubKey == ".\\..\\Tail ",
            "URI preserves dot keys and trailing spaces");
        check(
            RegistryLaunch.ParseUri("regedit2://HKCU/%20/Tail%20?view=32") == new RegistryLocation(
                RegistryHive.CurrentUser, " \\Tail ", View: RegistryView.Registry32),
            "URI preserves whitespace-only segments and trailing spaces in the selected view");

        foreach (var arguments in new[]
                 {
                     new[] { "--unknown" }, ["--32"], ["HKCU", "HKLM"], [string.Empty], ["UNKNOWN\\Key"],
                     ["https://HKCU/Software"]
                 })
        {
            Reject(
                () => RegistryLaunch.ParseArguments(arguments),
                "Invalid launch arguments are rejected: " + string.Join(" ", arguments));
        }

        foreach (var uri in new[]
                 {
                     "https://HKCU/Software", "regedit2:HKCU/Software", "regedit2://", "regedit2://UNKNOWN/Key",
                     "regedit2://Computer/Software", "regedit2://SERVER/HKLM/Software",
                     "regedit2://HKCU:80/Software", "regedit2://user@HKCU/Software", "regedit2://HKCU/Bad%",
                     "regedit2://HKCU/Bad%2", "regedit2://HKCU/Bad%GG", "regedit2://HKCU/Bad%00Key",
                     "regedit2://HKCU/A%5CB", "regedit2://HKCU//Key", "regedit2://HKCU/Software#fragment",
                     "regedit2://HKCU:/Software", "regedit2://Registry::HKCU/Software",
                     "regedit2://%5BHKCU%5D/Software", "regedit2://HKCU/Software?view=16",
                     "regedit2://HKCU/Software?view=32&view=64", "regedit2://HKCU/Software?command=delete"
                 })
        {
            Reject(() => RegistryLaunch.ParseUri(uri), "Invalid registry URI is rejected: " + uri);
        }

        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                check(true, name);
                return;
            }

            check(false, name);
        }
    }
}
