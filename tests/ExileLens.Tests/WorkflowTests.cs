using System.Text.RegularExpressions;

namespace ExileLens.Tests;

/// <summary>
/// Guards the GitHub Actions pipelines: a manual CI run must pass the whole test suite before
/// anything is built, and a release must pass tests before anything is published.
/// </summary>
[TestClass]
public sealed partial class WorkflowTests
{
    private const string TestCommand = "dotnet test tests/ExileLens.Tests/ExileLens.Tests.csproj";

    private static readonly Lazy<Workflow> Ci = new(() => Workflow.Load("ci.yml"));
    private static readonly Lazy<Workflow> Release = new(() => Workflow.Load("release.yml"));

    [TestMethod]
    public void CiIsTriggeredManuallyOnly()
    {
        var triggers = Ci.Value.Triggers;
        CollectionAssert.AreEqual(new[] { "workflow_dispatch" }, triggers.ToArray());
    }

    [TestMethod]
    public void CiTestJobRunsFullTestSuite()
    {
        var test = Ci.Value.Job("test");
        StringAssert.Contains(test.Body, TestCommand);
        Assert.IsFalse(test.Body.Contains("--filter", StringComparison.Ordinal), "the gate must run every test");
    }

    [TestMethod]
    public void CiTestJobStartsImmediately()
    {
        Assert.AreEqual(0, Ci.Value.Job("test").Needs.Count, "tests must not wait on any other job");
    }

    [TestMethod]
    public void CiBuildJobWaitsForTests()
    {
        var build = Ci.Value.Job("build");
        CollectionAssert.Contains(build.Needs.ToArray(), "test");
        StringAssert.Contains(build.Body, "dotnet build ExileLens.slnx");
    }

    [TestMethod]
    public void CiTestMatrixCancelsOnFirstFailure()
    {
        StringAssert.Matches(Ci.Value.Job("test").Body, FailFastTrue());
    }

    [TestMethod]
    public void EveryCiBuildOrPublishStepIsGatedByTests() => AssertGated(Ci.Value);

    [TestMethod]
    public void EveryReleaseBuildOrPublishStepIsGatedByTests()
    {
        var release = Release.Value;
        StringAssert.Contains(release.Job("test").Body, TestCommand);
        AssertGated(release);
    }

    [TestMethod]
    public void ParserReadsInlineAndBlockNeeds()
    {
        var workflow = Workflow.Parse("""
            on:
              workflow_dispatch:
            jobs:
              a:
                runs-on: x
              b:
                needs: a
              c:
                needs: [a, b]
              d:
                needs:
                  - c
            """);

        CollectionAssert.AreEqual(new[] { "workflow_dispatch" }, workflow.Triggers.ToArray());
        Assert.AreEqual(0, workflow.Job("a").Needs.Count);
        CollectionAssert.AreEqual(new[] { "a" }, workflow.Job("b").Needs.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, workflow.Job("c").Needs.ToArray());
        CollectionAssert.AreEqual(new[] { "c" }, workflow.Job("d").Needs.ToArray());
        Assert.IsTrue(workflow.DependsOn("d", "a"), "dependencies are transitive");
        Assert.IsFalse(workflow.DependsOn("a", "d"));
    }

    private static void AssertGated(Workflow workflow)
    {
        var testJob = workflow.Jobs.Values.Single(j => j.Body.Contains(TestCommand, StringComparison.Ordinal)).Id;
        var gated = 0;
        foreach (var job in workflow.Jobs.Values)
        {
            if (job.Id == testJob || !BuildOrPublish().IsMatch(job.Body))
            {
                continue;
            }

            Assert.IsTrue(workflow.DependsOn(job.Id, testJob), $"job '{job.Id}' builds/publishes without waiting for '{testJob}'");
            gated++;
        }

        Assert.IsTrue(gated > 0, "expected at least one build/publish job behind the test gate");
    }

    [GeneratedRegex(@"fail-fast:\s*true")]
    private static partial Regex FailFastTrue();

    [GeneratedRegex(@"dotnet (build|publish)\b")]
    private static partial Regex BuildOrPublish();

    [GeneratedRegex(@"^  (?<id>[A-Za-z0-9_-]+):\s*$")]
    private static partial Regex JobHeader();

    [GeneratedRegex(@"^    needs:\s*(?<value>.*?)\s*$")]
    private static partial Regex NeedsLine();

    [GeneratedRegex(@"^\s+-\s*(?<value>[A-Za-z0-9_-]+)\s*$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^  (?<id>[A-Za-z0-9_]+):")]
    private static partial Regex TriggerHeader();

    private sealed record JobInfo(string Id, IReadOnlyList<string> Needs, string Body);

    /// <summary>Minimal reader for the subset of workflow YAML these tests care about (no YAML dependency).</summary>
    private sealed class Workflow
    {
        private Workflow(IReadOnlyList<string> triggers, IReadOnlyDictionary<string, JobInfo> jobs)
        {
            Triggers = triggers;
            Jobs = jobs;
        }

        public IReadOnlyList<string> Triggers { get; }
        public IReadOnlyDictionary<string, JobInfo> Jobs { get; }

        public static Workflow Load(string name) =>
            Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Workflows", name)));

        public static Workflow Parse(string yaml)
        {
            var lines = yaml.ReplaceLineEndings("\n").Split('\n');
            var triggers = new List<string>();
            var jobs = new Dictionary<string, JobInfo>(StringComparer.Ordinal);
            string? section = null;
            string? jobId = null;
            List<string>? needs = null;
            var body = new List<string>();
            var inNeedsList = false;

            void Flush()
            {
                if (jobId is not null)
                {
                    jobs[jobId] = new JobInfo(jobId, needs!, string.Join('\n', body));
                }

                jobId = null;
            }

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                if (!char.IsWhiteSpace(line[0]))
                {
                    Flush();
                    section = line.TrimEnd(':');
                    continue;
                }

                if (section == "on")
                {
                    var trigger = TriggerHeader().Match(line);
                    if (trigger.Success)
                    {
                        triggers.Add(trigger.Groups["id"].Value);
                    }

                    continue;
                }

                if (section != "jobs")
                {
                    continue;
                }

                var header = JobHeader().Match(line);
                if (header.Success)
                {
                    Flush();
                    jobId = header.Groups["id"].Value;
                    needs = [];
                    body.Clear();
                    inNeedsList = false;
                    continue;
                }

                if (jobId is null)
                {
                    continue;
                }

                body.Add(line);
                var needsLine = NeedsLine().Match(line);
                if (needsLine.Success)
                {
                    var value = needsLine.Groups["value"].Value.Trim('[', ']');
                    inNeedsList = value.Length == 0;
                    needs!.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    continue;
                }

                if (inNeedsList)
                {
                    var item = ListItem().Match(line);
                    if (item.Success)
                    {
                        needs!.Add(item.Groups["value"].Value);
                        continue;
                    }

                    inNeedsList = false;
                }
            }

            Flush();
            return new Workflow(triggers, jobs);
        }

        public JobInfo Job(string id)
        {
            Assert.IsTrue(Jobs.TryGetValue(id, out var job), $"workflow has no '{id}' job");
            return job;
        }

        public bool DependsOn(string job, string target)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(Job(job).Needs);
            while (pending.TryPop(out var next))
            {
                if (next == target)
                {
                    return true;
                }

                if (seen.Add(next) && Jobs.TryGetValue(next, out var dep))
                {
                    foreach (var n in dep.Needs)
                    {
                        pending.Push(n);
                    }
                }
            }

            return false;
        }
    }
}
