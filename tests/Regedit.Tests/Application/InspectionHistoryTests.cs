// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class InspectionHistoryTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("regedit-inspections-");
        try
        {
            var path = Path.Combine(directory.FullName, "inspection-history.json");
            var clock = new TestClock();
            using var first = new InspectionHistoryStore(path, NullLogger<InspectionHistoryStore>.Instance, clock);
            using var second = new InspectionHistoryStore(path, NullLogger<InspectionHistoryStore>.Instance, clock);
            var location = RegistryLocation.Parse("HKCU\\Software");
            await first.RecordAsync(location, "Before opt-in");
            check(!(await second.ReadAsync()).IsRecording && !File.Exists(path), "No inspection history is written before opt-in");

            await first.SetRecordingAsync(true);
            var clipboard = new RegistryEntryClipboard();
            var browser = new BrowserViewModel(new RegistryService(), location, clipboard, inspectionHistory: first);
            browser.SetSelection([RegistryEntry.ForValue(new RegistryValue("", RegistryValueType.Binary, [1, 2, 3]), null, location)]);
            check((await second.ReadAsync()).Visits.Length == 0, "Selection in an open inspector does not record a visit");
            await browser.InspectCommand.ExecuteAsync(null);
            check((await second.ReadAsync()).Visits is [{ ValueName: "", DisplayName: "(Default)" }], "Explicit pane inspection records the default value");

            browser.IsInspectorOpen = false;
            browser.InspectorRequested += _ => Task.CompletedTask;
            browser.SetSelection([RegistryEntry.ForValue(new RegistryValue("Setting", RegistryValueType.Binary, [4, 5, 6]), null, location)]);
            await browser.InspectCommand.ExecuteAsync(null);
            clock.Now += TimeSpan.FromDays(1);
            await Task.WhenAll(
                second.RecordAsync(location with { SubKey = "SOFTWARE" }, "SETTING"),
                first.RecordAsync(location with { View = RegistryView.Registry32 }, "Setting"));
            var visits = (await first.ReadAsync()).Visits;
            check(visits.Length == 3 && visits.Take(2).All(visit => visit.LastInspectedUtc == clock.Now)
                                     && visits.Select(visit => visit.Location.View).Distinct().Count() == 2,
                "Windows merge their histories, deduplicate case-insensitively and keep both registry views");
            check(!File.ReadAllText(path).Contains("\"Data\"", StringComparison.Ordinal), "Inspection history only writes value identity and time");

            clock.Now += TimeSpan.FromDays(30);
            check((await second.ReadAsync()).Visits.Length == 2, "Visits expire after 30 days and a scope reads the latest data");
            await Task.WhenAll(first.RecordAsync(location, "Before clear"), first.ClearAsync());
            await second.SetRecordingAsync(false);
            await first.RecordAsync(location, "While paused");
            check((await first.ReadAsync()) is { IsRecording: false, Visits.Length: 0 }, "Clear and pause apply across windows");

            await first.SetRecordingAsync(true);
            for (var index = 0; index < 101; index++)
            {
                await first.RecordAsync(location, $"Value {index}");
            }

            visits = (await second.ReadAsync()).Visits;
            check(visits.Length == 100 && visits[0].ValueName == "Value 100" && visits[^1].ValueName == "Value 1",
                "Only the 100 most recent inspected values are kept");

            File.WriteAllText(path, "{ damaged history");
            await first.RecordAsync(location, "Corrupt history");
            check(File.ReadAllText(path) == "{ damaged history", "Recording never replaces an unreadable history file");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
