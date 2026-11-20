// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Tests.Infrastructure;

internal static class TestSuiteRunnerTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var activeFixtures = 0;
        var cleanedFixtures = 0;
        var attempts = 0;
        var dirtyStarts = 0;
        var followOnMutations = 0;
        var independentRan = false;

        async Task FailingSuite(bool setupFailure)
        {
            attempts++;
            dirtyStarts += activeFixtures;
            activeFixtures++;
            try
            {
                await Task.Yield();
                if (setupFailure)
                {
                    throw new IOException("Fake fixture setup failed.");
                }

                Assert(false);
                followOnMutations++;
            }
            finally
            {
                activeFixtures--;
                cleanedFixtures++;
            }
        }

        static void Assert(bool condition)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Fake assertion failed.");
            }
        }

        TestSuite[] suites =
        [
            new("setup-failure", () => FailingSuite(true)),
            new("assertion-failure", () => FailingSuite(false)),
            new(
                "independent", () =>
                {
                    independentRan = true;
                    return Task.CompletedTask;
                })
        ];
        using var output = new StringWriter();
        var status = await TestSuiteRunner.RunAsync(suites, output);
        check(status == 1 && attempts == 2 && independentRan, "Runner continues independent suites and returns failure for two failures");
        check(
            activeFixtures == 0 && cleanedFixtures == 2 && dirtyStarts == 0 && followOnMutations == 0,
            "Failed setup and assertions unwind fixtures without follow-on mutations");
        check(output.ToString().Contains("Failed suites: setup-failure, assertion-failure"), "Runner summarizes both failing suite names");
        check(await TestSuiteRunner.RunAsync([suites[2]], TextWriter.Null) == 0, "Successful suites return success");
        check(
            TestSuiteRunner.Select(suites, suites, ["--suite", "INDEPENDENT", "--suite", "setup-failure", "--suite", "independent"])
                .Select(suite => suite.Name).SequenceEqual(["setup-failure", "independent"]),
            "Exact suite selection is case insensitive, deduplicated and follows registration order");
        check(TestSuiteRunner.Select(suites, [suites[1]], []).SequenceEqual([suites[1]]), "Runner preserves default suite selection");
        foreach (var args in new[] { new[] { "--suite" }, new[] { "--suite", "missing" }, new[] { "--suite", "--registry" } })
        {
            var rejected = false;
            try
            {
                TestSuiteRunner.Select(suites, suites, args);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            check(rejected, "Invalid suite selection fails before running suites: " + string.Join(" ", args));
        }
    }
}
