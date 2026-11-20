// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal sealed record TestSuite(string Name, Func<Task> Run);

internal static class TestSuiteRunner
{
    internal static TestSuite[] Select(IReadOnlyList<TestSuite> suites, IEnumerable<TestSuite> defaults, string[] args)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--suite")
            {
                continue;
            }

            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("--suite requires an exact suite name. Use --list-suites to discover names.");
            }

            names.Add(args[index]);
        }

        var unknown = names.Where(name => !suites.Any(suite => StringComparer.OrdinalIgnoreCase.Equals(suite.Name, name))).ToArray();
        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"Unknown or disabled suites: {string.Join(", ", unknown)}. Use --list-suites; registry suites require --registry.");
        }

        return (names.Count == 0 ? defaults : suites.Where(suite => names.Contains(suite.Name))).ToArray();
    }

    internal static async Task<int> RunAsync(IEnumerable<TestSuite> suites, TextWriter output)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var suite in suites)
        {
            count++;
            output.WriteLine($"SUITE: {suite.Name}");
            try
            {
                await suite.Run();
                output.WriteLine($"PASS SUITE: {suite.Name}");
            }
            catch (Exception exception)
            {
                failures.Add(suite.Name);
                output.WriteLine($"FAIL SUITE: {suite.Name}{Environment.NewLine}{exception}");
            }
        }

        output.WriteLine($"{count - failures.Count}/{count} suites passed.");
        if (failures.Count == 0)
        {
            return 0;
        }

        output.WriteLine($"Failed suites: {string.Join(", ", failures)}");
        return 1;
    }
}
