// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.Specialized;
using JPSoftworks.Regedit.Infrastructure.Collections;

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class RangeObservableCollectionTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> throwsInvalidOperation)
    {
        var collection = new RangeObservableCollection<string>();
        var log = Observe(collection);

        collection.AddRange([]);
        check(log.Count == 0, "Adding nothing raises nothing");
        collection.AddRange(["a"]);
        check(
            log.SequenceEqual(["Count", "Item[]", "Add@0"]),
            "Adding one item raises a precise Add that WinUI lists can apply");
        log.Clear();
        collection.AddRange(["b", "c", "d"]);
        check(
            log.SequenceEqual(["Count", "Item[]", "Reset"]) && collection.SequenceEqual(["a", "b", "c", "d"]),
            "Adding a range raises one Reset");
        log.Clear();

        collection.ReplaceAll(["a", "b", "c", "d"]);
        check(log.Count == 0, "Replacing with equal contents raises nothing");
        collection.ReplaceAll(["d", "c", "b", "a"]);
        check(log.SequenceEqual(["Item[]", "Reset"]), "Reordering raises Reset without a Count change");
        log.Clear();
        collection.ReplaceAll(collection);
        check(log.Count == 0 && collection.Count == 4, "Replacing with itself is safe and silent");

        check(
            collection.RemoveRange(["a", "zzz", "c"]) == 2 && collection.SequenceEqual(["d", "b"]) &&
            log.SequenceEqual(["Count", "Item[]", "Reset"]), "Removing a range removes present items with one Reset");
        log.Clear();
        check(
            collection.RemoveRange(["missing", "other"]) == 0 && log.Count == 0,
            "Removing absent items raises nothing");
        check(
            collection.RemoveRange(["d"]) == 1 && log.SequenceEqual(["Count", "Item[]", "Remove@0"]),
            "Removing one item raises a precise Remove");
        log.Clear();

        var duplicates = new RangeObservableCollection<string?>(["x", null, "x", "y", null]);
        check(
            duplicates.RemoveRange(["x", null]) == 2 && duplicates.SequenceEqual(["x", "y", null]),
            "Range removal removes one occurrence per listed item, including nulls");
        check(
            duplicates.RemoveAll(item => item is null) == 1 && duplicates.SequenceEqual(["x", "y"]),
            "RemoveAll removes matching items");

        using (collection.DeferNotifications())
        {
            collection.Add("1");
            using (collection.DeferNotifications())
            {
                collection.AddRange(["2", "3"]);
                collection.Remove("b");
            }

            check(
                log.Count == 0 && collection.IsDeferring, "Nested deferral holds notifications until the outer scope");
        }

        check(
            log.SequenceEqual(["Count", "Item[]", "Reset"]) && collection.SequenceEqual(["1", "2", "3"]) &&
            !collection.IsDeferring, "Deferred changes coalesce into one Reset");
        log.Clear();
        var scope = collection.DeferNotifications();
        collection.Add("4");
        collection.Remove("4");
        scope.Dispose();
        scope.Dispose();
        check(
            log.SequenceEqual(["Item[]", "Reset"]) && !collection.IsDeferring,
            "A deferral with net-zero count still resets once, and double dispose is harmless");
        log.Clear();
        using (collection.DeferNotifications())
        {
        }

        check(log.Count == 0, "An empty deferral raises nothing");

        IRangeList untyped = collection;
        untyped.ReplaceAll(new object[] { "p", "q" });
        check(collection.SequenceEqual(["p", "q"]), "The non-generic range interface replaces contents");
        var rejected = false;
        try
        {
            untyped.ReplaceAll(new object[] { 5 });
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        check(rejected && collection.SequenceEqual(["p", "q"]), "Wrong item types are rejected without changes");

        collection.CollectionChanged += (_, _) => collection.AddRange(["boom", "bang"]);
        collection.CollectionChanged += (_, _) => { };
        throwsInvalidOperation(() => collection.AddRange(["r", "s"]), "Modifying during a notification is rejected");
    }

    private static List<string> Observe(RangeObservableCollection<string> collection)
    {
        var log = new List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)collection).PropertyChanged
            += (_, args) => log.Add(args.PropertyName!);
        collection.CollectionChanged += (_, args) => log.Add(
            args.Action switch
            {
                NotifyCollectionChangedAction.Add => "Add@" + args.NewStartingIndex,
                NotifyCollectionChangedAction.Remove => "Remove@" + args.OldStartingIndex,
                _ => args.Action.ToString()
            });
        return log;
    }
}
