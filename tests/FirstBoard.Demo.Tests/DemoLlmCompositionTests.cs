using DramaBoard.FirstBoard.Demo;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class DemoLlmCompositionTests
{
    [Fact]
    public void AllAiRosterOwnsBothActorDrivers()
    {
        DemoOptions options = DemoOptions.Parse([]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        Assert.Equal([BoardIds.Alice, BoardIds.Bob], plan.AiActors.Select(actor => actor.ActorId));
        Assert.All(plan.AiActors, actor => Assert.Equal(options.AliceBackend, actor.DecisionBackend));
        Assert.Single(plan.RequiredBackends);
        Assert.Equal(options.AliceBackend, plan.RequiredBackends[0]);
    }

    [Fact]
    public void HumanAliceOwnsNoAliceLlmResources()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "alice",
                "--alice-backend", "openai",
                "--alice-model", "human-only",
                "--bob-backend", "codex",
                "--bob-model", "bob-ai",
            ]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        DemoAiActorPlan bob = Assert.Single(plan.AiActors);
        Assert.Equal(BoardIds.Bob, bob.ActorId);
        Assert.Equal(options.BobBackend, bob.DecisionBackend);
        Assert.DoesNotContain(options.AliceBackend, plan.RequiredBackends);
        Assert.Equal([options.BobBackend], plan.RequiredBackends);
    }

    [Fact]
    public void HumanBobOwnsNoBobLlmResources()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "bob",
                "--alice-backend", "codex",
                "--alice-model", "alice-ai",
                "--bob-backend", "openai",
                "--bob-model", "human-only",
            ]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        DemoAiActorPlan alice = Assert.Single(plan.AiActors);
        Assert.Equal(BoardIds.Alice, alice.ActorId);
        Assert.Equal(options.AliceBackend, alice.DecisionBackend);
        Assert.DoesNotContain(options.BobBackend, plan.RequiredBackends);
        Assert.Equal([options.AliceBackend], plan.RequiredBackends);
    }

    [Fact]
    public void StructurallyEqualDecisionAndMemoryBackendsAreDeduplicated()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--alice-backend", "codex",
                "--alice-model", "shared",
                "--bob-backend", "codex",
                "--bob-model", "shared",
                "--memory-backend", "codex",
                "--memory-model", "shared",
            ]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        Assert.Single(plan.RequiredBackends);
        Assert.Equal(new DemoBackendOptions("codex", "shared"), plan.RequiredBackends[0]);
    }

    [Fact]
    public void DistinctDecisionAndMemoryConfigurationsHaveOneOwnedBackendEach()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--alice-backend", "codex",
                "--alice-model", "alice-model",
                "--bob-backend", "codex",
                "--bob-model", "bob-model",
                "--memory-backend", "deepseek",
                "--memory-model", "memory-model",
            ]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        Assert.Equal(
            [
                options.AliceBackend,
                options.MemoryBackend,
                options.BobBackend,
            ],
            plan.RequiredBackends);
    }

    [Fact]
    public void HumanBackendMayAppearOnlyWhenAnAiRequirementIsStructurallyEqual()
    {
        DemoOptions options = DemoOptions.Parse(
            [
                "--human", "alice",
                "--alice-backend", "codex",
                "--alice-model", "shared",
                "--bob-backend", "codex",
                "--bob-model", "shared",
                "--memory-backend", "codex",
                "--memory-model", "shared",
            ]);

        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(options);

        Assert.Single(plan.AiActors);
        Assert.Single(plan.RequiredBackends);
        Assert.Equal(options.AliceBackend, plan.RequiredBackends[0]);
        Assert.Equal(options.BobBackend, plan.RequiredBackends[0]);
    }

    [Fact]
    public void RosterCollectionsCannotBeMutatedThroughExposedContracts()
    {
        DemoLlmRosterPlan plan = DemoLlmRosterPlan.Create(DemoOptions.Parse([]));

        Assert.Throws<NotSupportedException>(() =>
            ((IList<DemoAiActorPlan>)plan.AiActors).Add(
                new DemoAiActorPlan("intruder", new DemoBackendOptions("codex", "model"))));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DemoBackendOptions>)plan.RequiredBackends).Clear());
    }

    [Fact]
    public async Task ActualCompositionExposesOwnedDriversAndAggregatesForcedEndsWithoutCallingLlm()
    {
        string output = CreateTempOutputDirectory();
        try
        {
            DemoOptions options = DemoOptions.Parse(["--output", output]) with
            {
                MaxTurnsPerActor = 0,
            };
            var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
            var profiler = new DemoLlmProfiler(output);
            var traces = new DemoTraceSink(output);
            await using DemoLlmComposition composition =
                await DemoLlmComposition.CreateAsync(options, scenario, profiler, traces);

            Assert.Equal([BoardIds.Alice, BoardIds.Bob], composition.AiDrivers.Keys);
            foreach ((string actorId, IPlayerDriver driver) in composition.AiDrivers)
            {
                PlayerDecision decision = await driver.DecideAsync(
                    Request(actorId),
                    CancellationToken.None);
                Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
            }

            Assert.Equal(2, composition.ForcedSceneEndCount);
            await composition.FlushMemoryAsync();
            await composition.DisposeAsync();
            await composition.DisposeAsync();
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task ActualHumanRosterNeverCreatesHumanOnlyBackend()
    {
        string output = CreateTempOutputDirectory();
        try
        {
            DemoOptions options = DemoOptions.Parse(
                [
                    "--output", output,
                    "--human", "alice",
                    "--alice-backend", "openai",
                    "--alice-model", "would-require-credentials-if-created",
                    "--bob-backend", "codex",
                    "--bob-model", "bob-ai",
                ]);
            var scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
            var profiler = new DemoLlmProfiler(output);
            var traces = new DemoTraceSink(output);
            await using DemoLlmComposition composition =
                await DemoLlmComposition.CreateAsync(options, scenario, profiler, traces);

            Assert.Equal([BoardIds.Bob], composition.AiDrivers.Keys);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static DecisionRequest Request(string actorId) =>
        new(
            new DecisionId($"test.{actorId}"),
            actorId,
            ModelTimeMs: 0,
            new Observation(actorId, BoardIds.Tavern, 0, [], [], [], []),
            AvailableActions: []);

    private static string CreateTempOutputDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dramaboard-demo-composition-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
