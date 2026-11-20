// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalPersistenceTests
{
    public static void Run(Action<bool, string> check, bool registry = false)
    {
        JournalCaptureTests.Run(check, registry);
        var folder = Directory.CreateTempSubdirectory("Regedit.JournalFormat.");
        try
        {
            var oldOptions = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, MaxDepth = 32 };
            var location = RegistryLocation.Parse(@"\\journal-test\HKLM\Software\Existing", RegistryView.Registry32);
            var time = DateTimeOffset.UtcNow;
            var snapshot = new RegistryBranchSnapshot(
                1, location, time, time,
                [new SnapshotKey(string.Empty, [new RegistryValue(string.Empty, (RegistryValueType)99, [0, 255, 66])])], "journal-test");
            var security = new JournalSecurity(string.Empty, "O:SYG:SYD:(A;;KA;;;SY)");
            var before = new JournalState(new JournalScope(location), snapshot, [security]);
            var legacy = new JournalEntry(Guid.NewGuid(), time, "Delete key", [before], null);
            var legacyJson = JsonSerializer.SerializeToNode(legacy, oldOptions)!.AsObject();
            legacyJson["Differences"] = new JsonArray(new JsonObject { ["Untrusted derived content"] = "ignored" });
            File.WriteAllText(Path.Combine(folder.FullName, $"{legacy.Id:N}.json"), legacyJson.ToJsonString());

            var journal = new RegistryJournal(folder.FullName);
            var loaded = journal.ReadEntries().Single();
            var state = loaded.Before.Single();
            check(
                loaded.Id == legacy.Id
                && loaded.After is null
                && state.Scope.Location == location
                && state.Security.Single() == security
                && state.Snapshot.Keys.Single().Values.Single() is { Name: "", Type: (RegistryValueType)99, Data: [0, 255, 66] },
                "The journal reads pre-AOT before-states with their original registry view, ACL and raw value bytes");

            journal.RecordActivity("Legacy reader check", location, "Activity details", () => { });
            var activity = journal.ReadEntries().Single(entry => entry.Activity is not null);
            var json = File.ReadAllText(Path.Combine(folder.FullName, $"{activity.Id:N}.json"));
            check(
                !JsonNode.Parse(json)!.AsObject().ContainsKey("Differences"),
                "New journal output omits computed differences while legacy derived content is ignored");
            var oldReader = JsonSerializer.Deserialize<JournalEntry>(json, oldOptions);
            check(
                oldReader is { Activity: "Activity details", After.Length: 0 }
                && oldReader.ActivityLocation == location
                && JsonNode.DeepEquals(JsonNode.Parse(json), JsonSerializer.SerializeToNode(activity, oldOptions)),
                "New journal entries retain the original JSON shape and load in the previous reader");
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
