using Atelia.StateJournal;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Player.Llm;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

public sealed class StateJournalNativePlayerClosureProbeTests
{
    private const long LineageId = 88_001;
    private const long ForkLineageId = 88_101;
    private const ulong WorldSeed = 42;

    [Fact]
    public async Task Observe_CoCommitsObjectiveAndPlayer_ThenReopensExactNextPrompt()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, "source");
        PlayerClosureCommitReceipt receipt;

        Assert.Equal(2, fixture.BasisRequest.Observation.KnownFacts.Count);
        Assert.Collection(
            fixture.BasisRequest.Observation.KnownFacts,
            fact => AssertKnownFact(
                fact,
                BoardIds.ObjectHeld,
                BoardIds.SilverCoinOne,
                $"You are carrying {BoardIds.SilverCoinOne}."),
            fact => AssertKnownFact(
                fact,
                BoardIds.ObjectHeld,
                BoardIds.SilverCoinTwo,
                $"You are carrying {BoardIds.SilverCoinTwo}."));
        GameBoardFact game = Assert.IsType<GameBoardFact>(
            Assert.Single(fixture.Transaction.Batch.Facts));
        ActorObservedEvent observed = Assert.IsType<ActorObservedEvent>(game.Value);
        Assert.Equal(2, observed.LearnedFacts.Count);
        Assert.All(
            observed.LearnedFacts,
            fact => Assert.Equal("object.visible", fact.Kind));
        Assert.Collection(
            fixture.Transaction.ExpectedPostActor.KnownFacts,
            fact => AssertClosureFact(
                fact,
                BoardIds.LastActionOutcome,
                relatedId: null,
                "You successfully observed the current place; the event reported 2 visible facts."),
            fact => AssertClosureFact(
                fact,
                "object.visible",
                BoardIds.SilverCoinOne,
                $"{BoardIds.SilverCoinOne} is visible at {BoardIds.Tavern}."),
            fact => AssertClosureFact(
                fact,
                "object.visible",
                BoardIds.SilverCoinTwo,
                $"{BoardIds.SilverCoinTwo} is visible at {BoardIds.Tavern}."));
        Assert.Collection(
            fixture.NextRequest.Observation.KnownFacts,
            fact => AssertKnownFact(
                fact,
                BoardIds.LastActionOutcome,
                relatedId: null,
                "You successfully observed the current place; the event reported 2 visible facts."),
            fact => AssertKnownFact(
                fact,
                "object.visible",
                BoardIds.SilverCoinOne,
                $"{BoardIds.SilverCoinOne} is visible at {BoardIds.Tavern}."),
            fact => AssertKnownFact(
                fact,
                "object.visible",
                BoardIds.SilverCoinTwo,
                $"{BoardIds.SilverCoinTwo} is visible at {BoardIds.Tavern}."),
            fact => AssertKnownFact(
                fact,
                BoardIds.ObjectHeld,
                BoardIds.SilverCoinOne,
                $"You are carrying {BoardIds.SilverCoinOne}."),
            fact => AssertKnownFact(
                fact,
                BoardIds.ObjectHeld,
                BoardIds.SilverCoinTwo,
                $"You are carrying {BoardIds.SilverCoinTwo}."));

        using (PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
                   repositoryPath,
                   fixture.Instance,
                   fixture.Composition,
                   LineageId))
        {
            receipt = session.Commit(fixture.Transaction);
            PlayerClosureSemanticSnapshot committed = session.Snapshot();
            Assert.Equal(new WorldVersion(LineageId, 1), committed.Version);
            Assert.Equal(1, committed.ObjectiveActor.Generation);
            Assert.Equal(1, committed.ObjectiveActor.DecisionSequence);
            Assert.Equal(1, committed.LastAppliedDecisionSequence);
            Assert.Equal(fixture.Transaction.ExpectedPostActor.KnownFacts,
                committed.ObjectiveActor.KnownFacts);
            Assert.Equal(
                fixture.Transaction.PlayerEffect.PreviousKnownFacts,
                committed.PreviousKnownFacts);
            Assert.True(fixture.Transaction.PlayerEffect.MemoryReplacements.SequenceEqual(
                committed.MemoryContents));
            Assert.Equal(4, committed.MemoryContents.Count);
            Assert.Contains(
                committed.MemoryContents,
                item => item.Key == "working_context" &&
                    item.Content == "我检查了酒馆；目前没有看到鲍勃或黄铜钥匙。");
            Assert.DoesNotContain(
                "黄铜钥匙。",
                committed.CommitSummary ?? string.Empty,
                StringComparison.Ordinal);
        }

        using PlayerClosureProbeSession reopened = PlayerClosureProbeSession.Open(
            repositoryPath,
            fixture.Instance);
        Assert.Equal(receipt.Head, reopened.HeadAddress);
        PlayerClosurePromptState promptState = reopened.AttachPromptState(fixture.Composition);
        LlmChatRequest actualPrompt = promptState.Render(fixture.NextRequest);
        Assert.Equal(fixture.ExpectedNextPrompt, actualPrompt);
        Assert.Equal(1, promptState.DecisionSequence);
        Assert.Equal(
            fixture.Transaction.PlayerEffect.PreviousKnownFacts,
            promptState.PreviousKnownFacts.Select(
                PlayerClosureObserveFixture.FromKnownFact));

        string recent = Section(actualPrompt.User, "[新近变化]", "[决策请求]");
        // Production Observe sees Alice's two held coins as visible facts. The exact post-pre
        // delta is therefore two object.visible facts plus LastOutcome, not only LastOutcome.
        Assert.Equal(3, CountOccurrences(recent, "- 新增事实:"));
        Assert.Contains("kind=object.visible, related=silver-coin-1", recent);
        Assert.Contains("kind=object.visible, related=silver-coin-2", recent);
        Assert.Contains($"kind={BoardIds.LastActionOutcome}", recent);
        Assert.DoesNotContain($"kind={BoardIds.ObjectHeld}", recent);
        Assert.Contains(
            "我检查了酒馆；目前没有看到鲍勃或黄铜钥匙。",
            actualPrompt.User);
    }

    [Fact]
    public async Task PreviousKnownFactsMutants_ProduceWrongRecentChanges()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, "source");
        using (PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
                   repositoryPath,
                   fixture.Instance,
                   fixture.Composition,
                   LineageId))
        {
            _ = session.Commit(fixture.Transaction);
        }

        using PlayerClosureProbeSession reopened = PlayerClosureProbeSession.Open(
            repositoryPath,
            fixture.Instance);
        PlayerClosurePromptState restored = reopened.AttachPromptState(fixture.Composition);
        LlmChatRequest exact = restored.Render(fixture.NextRequest);
        ScenarioActorDefinition actor = fixture.Instance.Definition.Actor(BoardIds.Alice);

        LlmChatRequest emptyPrevious = PlayerClosureObserveFixture.RenderPrompt(
            actor,
            restored.Memory,
            fixture.NextRequest,
            []);
        LlmChatRequest postCurrentPrevious = PlayerClosureObserveFixture.RenderPrompt(
            actor,
            restored.Memory,
            fixture.NextRequest,
            fixture.NextRequest.Observation.KnownFacts);

        string exactRecent = Section(exact.User, "[新近变化]", "[决策请求]");
        string emptyRecent = Section(emptyPrevious.User, "[新近变化]", "[决策请求]");
        string postCurrentRecent = Section(
            postCurrentPrevious.User,
            "[新近变化]",
            "[决策请求]");
        Assert.NotEqual(exactRecent, emptyRecent);
        Assert.NotEqual(exactRecent, postCurrentRecent);
        Assert.DoesNotContain($"kind={BoardIds.ObjectHeld}", exactRecent);
        Assert.Contains($"kind={BoardIds.ObjectHeld}", emptyRecent);
        Assert.Contains("- 无新增事实", postCurrentRecent);
    }

    [Theory]
    [InlineData(PlayerClosureProbeFault.AfterPlayerMemory)]
    [InlineData(PlayerClosureProbeFault.AfterObjectiveActor)]
    [InlineData(PlayerClosureProbeFault.AtCompleteValidation)]
    public async Task WorkingMutationFailure_PoisonsSession_AndReopensExactParent(
        PlayerClosureProbeFault fault)
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, fault.ToString());
        PlayerClosureSemanticSnapshot parent;
        PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
            repositoryPath,
            fixture.Instance,
            fixture.Composition,
            LineageId);
        try
        {
            parent = session.Snapshot();
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => session.Commit(fixture.Transaction, fault));
            Assert.Contains("poisoned", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<InvalidOperationException>(() => session.Snapshot());
        }
        finally
        {
            session.Dispose();
        }

        using PlayerClosureProbeSession reopened = PlayerClosureProbeSession.Open(
            repositoryPath,
            fixture.Instance);
        PlayerClosureSemanticSnapshot actual = reopened.Snapshot();
        Assert.True(actual.AuthorityMatches(parent));
        Assert.Equal(new WorldVersion(LineageId, 0), actual.Version);
        Assert.Equal(0, actual.ObjectiveActor.DecisionSequence);
        Assert.Equal(0, actual.LastAppliedDecisionSequence);
    }

    [Fact]
    public async Task WrongRuntimeComposition_IsRejectedBeforePromptAttach()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, "source");
        using PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
            repositoryPath,
            fixture.Instance,
            fixture.Composition,
            LineageId);
        PlayerClosureCompositionV1 wrong = fixture.Composition with
        {
            DecisionModel = "different-model/1",
        };

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => session.AttachPromptState(wrong));
        Assert.Contains("composition", failure.Message, StringComparison.OrdinalIgnoreCase);
        _ = session.AttachPromptState(fixture.Composition);
    }

    [Fact]
    public async Task SecretBearingEndpoint_IsRejectedBeforeRepositoryCreation()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, "secret-config");
        PlayerClosureCompositionV1 secretBearing = fixture.Composition with
        {
            DecisionEndpointIdentity =
                "https://user:password@example.invalid/v1?apiKey=must-not-persist",
        };

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            PlayerClosureProbeSession.Create(
                repositoryPath,
                fixture.Instance,
                secretBearing,
                LineageId));
        Assert.Contains("safe identity", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(repositoryPath));
    }

    [Theory]
    [InlineData(PlayerClosureMalformedState.SlotBinding)]
    [InlineData(PlayerClosureMalformedState.ExtraMemoryShard)]
    [InlineData(PlayerClosureMalformedState.EmptyRelatedId)]
    public async Task MalformedDurablePlayerBinding_IsRejectedBeforePromptAttach(
        PlayerClosureMalformedState malformed)
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, malformed.ToString());
        using (PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
                   repositoryPath,
                   fixture.Instance,
                   fixture.Composition,
                   LineageId))
        {
            if (malformed == PlayerClosureMalformedState.EmptyRelatedId)
            {
                _ = session.Commit(fixture.Transaction);
            }

            session.CommitMalformedForTest(malformed);
        }

        Assert.Throws<InvalidDataException>(() =>
            PlayerClosureProbeSession.Open(repositoryPath, fixture.Instance));
    }

    [Fact]
    public async Task HistoricalPostObserveFork_PreservesThenDivergesNestedClosure_WithoutMovingMain()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        PlayerClosureObserveFixture mainFollowup =
            await PlayerClosureObserveFixture.CreateFollowupAsync(
                fixture,
                new WorldVersion(LineageId, 1),
                "main lineage 的第二次观察记忆。");
        PlayerClosureObserveFixture forkFollowup =
            await PlayerClosureObserveFixture.CreateFollowupAsync(
                fixture,
                new WorldVersion(ForkLineageId, 1),
                "fork lineage 的第二次观察记忆。");
        string repositoryPath = Path.Combine(directory.Path, "source");
        CommitAddress selectedCommit;
        CommitAddress mainHead;
        PlayerClosureSemanticSnapshot selectedState;
        PlayerClosureSemanticSnapshot mainState;
        using (PlayerClosureProbeSession main = PlayerClosureProbeSession.Create(
                   repositoryPath,
                   fixture.Instance,
                   fixture.Composition,
                   LineageId))
        {
            PlayerClosureCommitReceipt receipt = main.Commit(fixture.Transaction);
            selectedCommit = receipt.Head;
            selectedState = main.Snapshot();
            _ = main.Commit(mainFollowup.Transaction);
            mainHead = main.HeadAddress;
            mainState = main.Snapshot();
            Assert.Equal(new WorldVersion(LineageId, 2), mainState.Version);
        }

        using (PlayerClosureProbeSession fork = PlayerClosureProbeSession.ForkAtCommit(
                   repositoryPath,
                   fixture.Instance,
                   selectedCommit,
                   "fork/player-closure",
                   ForkLineageId))
        {
            PlayerClosureSemanticSnapshot forkState = fork.Snapshot();
            Assert.Equal(new WorldVersion(ForkLineageId, 1), forkState.Version);
            Assert.Equal(new WorldVersion(LineageId, 1), forkState.ParentWorldVersion);
            Assert.Equal(
                selectedState.ObjectiveActor.Generation,
                forkState.ObjectiveActor.Generation);
            Assert.Equal(
                selectedState.ObjectiveActor.DecisionSequence,
                forkState.ObjectiveActor.DecisionSequence);
            Assert.True(selectedState.ObjectiveActor.KnownFacts.SequenceEqual(
                forkState.ObjectiveActor.KnownFacts));
            Assert.True(selectedState.MemoryContents.SequenceEqual(forkState.MemoryContents));
            Assert.True(selectedState.PreviousKnownFacts.SequenceEqual(
                forkState.PreviousKnownFacts));
            Assert.Equal(
                fixture.ExpectedNextPrompt,
                fork.AttachPromptState(fixture.Composition).Render(fixture.NextRequest));

            _ = fork.Commit(forkFollowup.Transaction);
            PlayerClosureSemanticSnapshot divergentFork = fork.Snapshot();
            Assert.Equal(new WorldVersion(ForkLineageId, 2), divergentFork.Version);
            Assert.Contains(
                divergentFork.MemoryContents,
                item => item.Key == "working_context" &&
                    item.Content == "fork lineage 的第二次观察记忆。");
            Assert.Equal(
                forkFollowup.ExpectedNextPrompt,
                fork.AttachPromptState(fixture.Composition).Render(forkFollowup.NextRequest));
        }

        using PlayerClosureProbeSession reopenedMain = PlayerClosureProbeSession.Open(
            repositoryPath,
            fixture.Instance);
        Assert.Equal(mainHead, reopenedMain.HeadAddress);
        Assert.True(reopenedMain.Snapshot().AuthorityMatches(mainState));
        Assert.Contains(
            reopenedMain.Snapshot().MemoryContents,
            item => item.Key == "working_context" &&
                item.Content == "main lineage 的第二次观察记忆。");
        Assert.DoesNotContain(
            reopenedMain.Snapshot().MemoryContents,
            item => item.Content == "fork lineage 的第二次观察记忆。");
        Assert.Equal(
            mainFollowup.ExpectedNextPrompt,
            reopenedMain.AttachPromptState(fixture.Composition).Render(
                mainFollowup.NextRequest));
    }

    [Fact]
    public async Task ReflogFailure_AfterPublication_ResolvesExactObjectiveAndPlayerChild()
    {
        using var directory = new TemporaryJournalDirectory();
        PlayerClosureObserveFixture fixture = await PlayerClosureObserveFixture.CreateAsync(
            WorldSeed,
            LineageId);
        string repositoryPath = Path.Combine(directory.Path, "source");
        PlayerClosureCommitFailedException failure;
        using (PlayerClosureProbeSession session = PlayerClosureProbeSession.Create(
                   repositoryPath,
                   fixture.Instance,
                   fixture.Composition,
                   LineageId))
        {
            failure = Assert.Throws<PlayerClosureCommitFailedException>(
                () => session.Commit(
                    fixture.Transaction,
                    PlayerClosureProbeFault.AtRepositoryReflog));
        }

        PlayerClosureResolution resolution = PlayerClosureProbeSession.ResolveUnknownOutcome(
            repositoryPath,
            fixture.Instance,
            failure);
        Assert.Equal(PlayerClosureProbeOutcome.Committed, resolution.Outcome);
        Assert.Equal(RepositoryCommitFailurePhase.AppendReflog, failure.FailurePhase);
        Assert.Equal(
            RepositoryCommitPublicationState.Published,
            failure.PublicationState);
        Assert.True(failure.RequiresRepositoryReopen);
        Assert.False(failure.CanRetryTransparently);
        Assert.True(failure.MayHavePublished);
        Assert.True(resolution.Snapshot.AuthorityMatches(failure.ChildAuthority));
        Assert.Equal(1, resolution.Snapshot.ObjectiveActor.DecisionSequence);
        Assert.Equal(1, resolution.Snapshot.LastAppliedDecisionSequence);
        Assert.Equal(
            fixture.Transaction.PlayerEffect.PreviousKnownFacts,
            resolution.Snapshot.PreviousKnownFacts);
    }

    private static string Section(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Section start '{start}' was not found.");
        Assert.True(endIndex >= 0, $"Section end '{end}' was not found.");
        return text[(startIndex + start.Length)..endIndex];
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertKnownFact(
        KnownFact actual,
        string kind,
        string? relatedId,
        string text)
    {
        Assert.Equal(kind, actual.FactKind.Id);
        Assert.Equal(relatedId, actual.RelatedId);
        Assert.Equal(text, actual.Text);
    }

    private static void AssertClosureFact(
        PlayerClosureFactValue actual,
        string kind,
        string? relatedId,
        string text)
    {
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(relatedId, actual.RelatedId);
        Assert.Equal(text, actual.Text);
    }
}
