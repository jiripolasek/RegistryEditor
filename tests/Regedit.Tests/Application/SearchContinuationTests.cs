// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class SearchContinuationTests
{
    internal static void Run(RegistryService registry, RegistryLocation parent, Action<bool, string> check)
    {
        var root = parent.Child("SearchContinuation");
        registry.CreateKey(parent, root.Name);
        try
        {
            using var key = registry.Open(root, true);
            foreach (var name in new[] { string.Empty, "A", "C" })
            {
                key.SetValue(name, "match");
            }

            var child = root.Child("Match");
            registry.CreateKey(root, child.Name);
            registry.CreateKey(child, "MatchChild");
            registry.CreateKey(root, "Match!");
            var options = new SearchOptions("match");

            SearchHit? Next(SearchHit? after)
            {
                return registry.FindNext(root, options, after, default).Hit;
            }

            var first = Next(null);
            check(first == new SearchHit(root, string.Empty), "Find starts with the unnamed matching value");
            key.DeleteValue(string.Empty);
            check(Next(first) == new SearchHit(root, "A"), "F3 resumes after an externally deleted unnamed value");
            registry.RenameValue(root, registry.ReadValue(root, "A")!, "0");
            check(
                Next(new SearchHit(root, "a")) == new SearchHit(root, "C"),
                "F3 skips a renamed value that moved before the cursor");
            registry.RenameValue(root, registry.ReadValue(root, "C")!, "D");
            check(
                Next(new SearchHit(root, "C")) == new SearchHit(root, "D"),
                "F3 includes a renamed value that moved after the cursor");
            check(
                Next(new SearchHit(root, "D")) == new SearchHit(child, null),
                "F3 visits child keys after their parent's values");
            check(
                Next(new SearchHit(child, null)) == new SearchHit(child.Child("MatchChild"), null),
                "F3 visits a subtree before a sibling with a common prefix");
            key.DeleteSubKeyTree("Match");
            check(
                Next(new SearchHit(child.Child("MatchChild"), null)) == new SearchHit(root.Child("Match!"), null),
                "F3 resumes at the next sibling when another process removes the cursor's entire subtree");
            registry.RenameKey(root.Child("Match!"), "MatchLater");
            var last = Next(new SearchHit(root.Child("Match!"), null));
            check(
                last == new SearchHit(root.Child("MatchLater"), null) && Next(last) is null,
                "F3 continues after a renamed key and finishes at end of scope");
        }
        finally
        {
            registry.DeleteKey(root);
        }

        var missing = registry.FindNext(root, new SearchOptions("match"), new SearchHit(root, null), default);
        check(missing.Hit is null && missing.SkippedKeys == 1, "A removed search root finishes with one skipped key");
    }
}
