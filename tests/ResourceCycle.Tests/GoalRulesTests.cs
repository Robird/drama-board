using System.Text.Json;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class GoalRulesTests {
    private static CycleGoalOperationResult Apply(CycleGoalSet before, string raw, long sequence = 0, long context = 1) {
        Assert.True(CycleGoalCommandParser.TryParse(raw, out CycleGoalCommand? command, out string? error), error);
        if (sequence == 0) {
            (long Context, long Operation) latest = before.Goals.SelectMany(goal => goal.Revisions
                .Select(revision => (revision.ContextSequence, revision.OperationSequence))
                .Append((goal.FinishedContextSequence, goal.FinishedOperationSequence))).Append((context, 0L)).Max();
            context = latest.Context; sequence = latest.Operation + 1;
        }
        return CycleGoalRules.Apply(before, command!, context, sequence);
    }

    private static CycleGoalSet Success(CycleGoalSet before, string raw, long sequence = 0, long context = 1) {
        CycleGoalOperationResult result = Apply(before, raw, sequence, context);
        Assert.True(result.Success, result.Error);
        result.After.Validate(); result.Receipt.Validate();
        return result.After;
    }

    private static CycleGoalSet ThreeGoals() {
        CycleGoalSet state = CycleGoalSet.Empty;
        for (int i = 0; i < 3; i++) { state = Success(state, "create-goal {\"title\":\"获取补给\",\"criteria\":\"本次获得两份\"}", i + 1); }
        return state;
    }

    [Fact]
    public void CreateWithParentAndFocusIsAtomicAndDoesNotMergeIdenticalText() {
        CycleGoalSet state = ThreeGoals();
        CycleGoalOperationResult failure = Apply(state, "create-goal {\"title\":\"获取补给\",\"criteria\":\"本次获得两份\",\"parent\":\"G99\",\"linkReason\":\"补给\",\"focus\":true}");
        Assert.False(failure.Success); Assert.Same(state, failure.After); Assert.Equal(4, state.NextGoalId);
        CycleGoalSet created = Success(state, "create-goal {\"title\":\"获取补给\",\"criteria\":\"本次获得两份\",\"parent\":\"G1\",\"linkReason\":\"补给\",\"focus\":true}", 4);
        Assert.Equal(4, created.Goals.Count); Assert.Equal(new[] { "G1", "G4" }, created.Focus);
        Assert.Equal("L1", Assert.Single(created.Links).Id); Assert.Empty(state.Links); Assert.Empty(state.Focus);
    }

    [Fact]
    public void SharedGoalPausingUsesSelectedSourceWithoutCascading() {
        CycleGoalSet state = ThreeGoals();
        state = Success(state, "link-goal {\"parent\":\"G1\",\"child\":\"G3\",\"reason\":\"补水\"}");
        state = Success(state, "link-goal {\"parent\":\"G2\",\"child\":\"G3\",\"reason\":\"会合\"}");
        state = Success(state, "focus-goal {\"id\":\"G3\",\"via\":[\"G1\",\"G3\"]}");
        state = Success(state, "pause-goal {\"id\":\"G1\",\"reason\":\"先等\"}");
        Assert.Equal(new[] { "G1", "G3" }, state.Focus); Assert.Equal(CycleGoalStatus.Open, state.Goals[2].Status);
        state = Success(state, "pause-goal {\"id\":\"G3\",\"reason\":\"缺工具\"}");
        Assert.Empty(state.Focus); Assert.Equal(CycleGoalStatus.Open, state.Goals[1].Status);
        Assert.Equal(2, state.Links.Count); Assert.Equal(3, state.UnfinishedCount);
        Assert.False(Apply(state, "pause-goal {\"id\":\"G3\",\"reason\":\"重复\"}").Success);
    }

    [Fact]
    public void FocusRetreatsOverPausedAncestorAndUnlinkKeepsLongestSuffix() {
        CycleGoalSet state = ThreeGoals();
        state = Success(state, "link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"目的\"}");
        state = Success(state, "link-goal {\"parent\":\"G2\",\"child\":\"G3\",\"reason\":\"方法\"}");
        state = Success(state, "focus-goal {\"id\":\"G3\",\"via\":[\"G1\",\"G2\",\"G3\"]}");
        CycleGoalSet suffix = Success(state, "unlink-goal {\"id\":\"L1\"}");
        Assert.Equal(new[] { "G2", "G3" }, suffix.Focus);
        Assert.Equal(new[] { "G3" }, Success(state, "unlink-goal {\"id\":\"L2\"}").Focus);
        state = Success(state, "pause-goal {\"id\":\"G2\",\"reason\":\"等待\"}");
        state = Success(state, "finish-goal {\"id\":\"G3\",\"outcome\":\"completed\",\"note\":\"已确认\"}", 9);
        Assert.Equal(new[] { "G1" }, state.Focus);
        Assert.Equal(new[] { "G1" }, Success(state, "reopen-goal {\"id\":\"G3\",\"reason\":\"误判\"}", 10).Focus);
    }

    [Fact]
    public void DuplicateCycleAndInvalidFocusRejectWholeEdit() {
        CycleGoalSet state = ThreeGoals();
        state = Success(state, "link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"目的\"}");
        string[] invalid = [
            "link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"重复\"}",
            "link-goal {\"parent\":\"G2\",\"child\":\"G1\",\"reason\":\"环\"}",
            "link-goal {\"parent\":\"G1\",\"child\":\"G1\",\"reason\":\"自身\"}",
            "focus-goal {\"id\":\"G3\",\"via\":[\"G1\",\"G3\"]}",
            "focus-goal {\"id\":\"G1\",\"via\":[\"G1\",\"G2\"]}"
        ];
        foreach (string raw in invalid) { CycleGoalOperationResult result = Apply(state, raw); Assert.False(result.Success); Assert.Same(state, result.After); }
        Assert.Equal(new[] { "G2" }, Success(state, "focus-goal {\"id\":\"G2\"}").Focus);
    }

    [Fact]
    public void CorrectionPreservesBoundedPriorJudgmentsAndFinishingTime() {
        CycleGoalSet state = Success(CycleGoalSet.Empty, "create-goal {\"title\":\"赠水\",\"criteria\":\"同伴收到水\",\"progress\":\"已备好\"}");
        state = Success(state, "finish-goal {\"id\":\"G1\",\"outcome\":\"completed\",\"note\":\"以为给过\"}", 2);
        Assert.False(Apply(state, "update-goal {\"id\":\"G1\",\"criteria\":\"自己有水\",\"reason\":\"换标准\"}", 3).Success);
        state = Success(state, "update-goal {\"id\":\"G1\",\"endNote\":\"说明勘误\",\"reason\":\"笔误\"}", 3);
        Assert.Equal(2, state.Goals[0].FinishedOperationSequence);
        state = Success(state, "reopen-goal {\"id\":\"G1\",\"reason\":\"原判断错误\"}", 1, 2);
        Assert.Null(state.Goals[0].EndNote); Assert.Equal(0, state.Goals[0].FinishedContextSequence);
        Assert.Equal("已备好", state.Goals[0].Progress);
        state = Success(state, "update-goal {\"id\":\"G1\",\"title\":\"交付饮水\",\"criteria\":\"明确交付两份\",\"reason\":\"澄清承诺\"}", 2, 2);
        CycleGoalOperationResult last = Apply(state, "update-goal {\"id\":\"G1\",\"criteria\":\"明确交付三份\",\"reason\":\"协商增加\"}", 3, 2);
        Assert.True(last.Success); Assert.Contains("最近 3 条", last.Receipt.Message);
        Assert.Equal(3, last.After.Goals[0].Revisions.Count);
        Assert.Equal("说明勘误", last.After.Goals[0].Revisions[0].OldEndNote);
        Assert.Equal("赠水", last.After.Goals[0].Revisions[1].OldTitle);
    }

    [Fact]
    public void NoOpAndShowPreserveSnapshotButInvalidStatesRemainRejected() {
        CycleGoalSet state = ThreeGoals();
        foreach (string raw in new[] { "update-goal {\"id\":\"G1\",\"title\":\"获取补给\"}", "focus-goal {\"id\":null}", "show-goal {}", "show-goal {\"id\":\"G1\"}" }) {
            CycleGoalOperationResult result = Apply(state, raw);
            Assert.True(result.Success); Assert.False(result.Receipt.Changed); Assert.Same(state, result.After);
        }
        Assert.False(Apply(state, "update-goal {\"id\":\"G1\",\"endNote\":\"不该设置\"}").Success);
        Assert.False(Apply(state, "resume-goal {\"id\":\"G1\"}").Success);
        Assert.False(Apply(state, "show-goal {\"id\":\"G99\"}").Success);
    }

    [Fact]
    public void CapacityCanBeRecoveredWithoutDroppingSharedResultsOrReusingIds() {
        CycleGoalSet state = CycleGoalSet.Empty;
        for (int i = 0; i < CycleGoalLimits.MaximumUnfinished; i++) { state = Success(state, "create-goal {\"title\":\"事项\",\"criteria\":\"标准\"}"); }
        state = Success(state, "pause-goal {\"id\":\"G1\",\"reason\":\"等待\"}");
        Assert.False(Apply(state, "create-goal {\"title\":\"新事项\",\"criteria\":\"标准\"}").Success);
        state = Success(state, "finish-goal {\"id\":\"G1\",\"outcome\":\"completed\",\"note\":\"调查结论\"}", 5);
        state = Success(state, "link-goal {\"parent\":\"G2\",\"child\":\"G1\",\"reason\":\"共享结论\"}");
        Assert.Contains("L1", Apply(state, "forget-goal {\"id\":\"G1\"}").Error!);
        state = Success(state, "create-goal {\"title\":\"新事项\",\"criteria\":\"标准\"}");
        Assert.False(Apply(state, "reopen-goal {\"id\":\"G1\",\"reason\":\"误判\"}", 6).Success);
        state = Success(state, "unlink-goal {\"id\":\"L1\"}");
        state = Success(state, "forget-goal {\"id\":\"G1\"}");
        Assert.Equal(14, state.NextGoalId); Assert.Equal(2, state.NextLinkId); Assert.DoesNotContain(state.Goals, goal => goal.Id == "G1");
    }

    [Fact]
    public void AllOperationKindsAndOptionalClearingAreSupported() {
        CycleGoalSet state = ThreeGoals();
        state = Success(state, "link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"旧理由\"}");
        state = Success(state, "update-goal-link {\"id\":\"L1\",\"reason\":\"新理由\"}");
        Assert.Equal("新理由", state.Links[0].Reason);
        Assert.False(Apply(state, "update-goal-link {\"id\":\"L1\",\"reason\":\"新理由\"}").Receipt.Changed);
        state = Success(state, "pause-goal {\"id\":\"G2\",\"reason\":\"等工具\",\"revisit\":\"借到工具\"}");
        state = Success(state, "update-goal {\"id\":\"G2\",\"pauseReason\":\"工具坏了\",\"progress\":\"已问过\"}");
        state = Success(state, "resume-goal {\"id\":\"G2\"}");
        Assert.Null(state.Goals[1].PauseReason); Assert.Equal("借到工具", state.Goals[1].Revisit);
        state = Success(state, "update-goal {\"id\":\"G2\",\"revisit\":null,\"progress\":\"   \"}");
        Assert.Null(state.Goals[1].Revisit); Assert.Null(state.Goals[1].Progress);
    }

    [Fact]
    public void TotalAndLinkCapacityStillPermitExplicitCleanup() {
        CycleGoal[] goals = Enumerable.Range(1, CycleGoalLimits.MaximumGoals).Select(i =>
            i <= CycleGoalLimits.MaximumUnfinished ? new CycleGoal($"G{i}", "事项", "标准") :
            new CycleGoal($"G{i}", "已结束", "标准", CycleGoalStatus.Completed, endNote: "旧结论", finishedContextSequence: 1, finishedOperationSequence: i)).ToArray();
        var full = new CycleGoalSet(goals, nextGoalId: 33);
        full.Validate();
        Assert.False(Apply(full, "create-goal {\"title\":\"x\",\"criteria\":\"y\"}").Success);
        CycleGoalSet room = Success(full, "finish-goal {\"id\":\"G1\",\"outcome\":\"abandoned\",\"note\":\"明确放弃\"}", 100);
        room = Success(room, "forget-goal {\"id\":\"G1\"}");
        room = Success(room, "create-goal {\"title\":\"x\",\"criteria\":\"y\"}");
        Assert.Equal("G33", room.Goals[^1].Id); Assert.Equal(32, room.Goals.Count);
        var links = new List<CycleGoalLink>();
        for (int parent = 1; parent <= 12; parent++) {
            for (int child = parent + 1; child <= 12 && links.Count < CycleGoalLimits.MaximumLinks; child++) {
                links.Add(new($"L{links.Count + 1}", $"G{parent}", $"G{child}", "来源"));
            }
        }
        var linked = new CycleGoalSet(goals, links, nextGoalId: 33, nextLinkId: 49);
        linked.Validate();
        Assert.False(Apply(linked, "link-goal {\"parent\":\"G11\",\"child\":\"G12\",\"reason\":\"来源\"}").Success);
        linked = Success(linked, "unlink-goal {\"id\":\"L1\"}");
        linked = Success(linked, "link-goal {\"parent\":\"G11\",\"child\":\"G12\",\"reason\":\"来源\"}");
        Assert.Equal("L49", linked.Links[^1].Id); Assert.Equal(48, linked.Links.Count);
    }

    [Theory]
    [InlineData("create-goal []")]
    [InlineData("create-goal {\"title\":\"x\",\"title\":\"y\",\"criteria\":\"z\"}")]
    [InlineData("create-goal {\"title\":\"x\",\"criteria\":\"z\",\"actor\":\"b\"}")]
    [InlineData("create-goal {\"title\":\"x\",\"criteria\":\"z\",\"parent\":null}")]
    [InlineData("create-goal {\"title\":\"x\",\"criteria\":\"z\",\"focus\":\"true\"}")]
    [InlineData("create-goal {\"title\":\"x\",\"criteria\":\"z\",\"parent\":\"G1\"}")]
    [InlineData("show-goal {\"id\":null}")]
    [InlineData("show-goal {} trailing")]
    [InlineData("show-goal {\"id\":\"G01\"}")]
    [InlineData("show-goal {\"id\":\"other/G1\"}")]
    [InlineData("focus-goal {\"id\":null,\"via\":[]}")]
    [InlineData("focus-goal {\"id\":\"G1\",\"via\":[\"G1\",\"G1\"]}")]
    [InlineData("update-goal {\"id\":\"G1\"}")]
    [InlineData("update-goal {\"id\":\"G1\",\"title\":null}")]
    [InlineData("update-goal {\"id\":\"G1\",\"reason\":\"r\"}")]
    [InlineData("finish-goal {\"id\":\"G1\",\"outcome\":\"paused\",\"note\":\"x\"}")]
    [InlineData("create-goal {\"title\":\"\\uD800\",\"criteria\":\"x\"}")]
    [InlineData("create-goal {\"title\":\"\\uDC00\",\"criteria\":\"x\"}")]
    public void StrictParserRejectsMalformedOrAmbiguousArguments(string raw) {
        Assert.False(CycleGoalCommandParser.TryParse(raw, out _, out string? error)); Assert.NotNull(error);
    }

    [Fact]
    public void UnicodeScalarLimitsAndCommandBoundaryAreDistinct() {
        string title = string.Concat(Enumerable.Repeat("🌿", CycleGoalLimits.TitleScalars));
        string raw = "create-goal " + JsonSerializer.Serialize(new { title, criteria = "x" });
        Assert.True(CycleGoalCommandParser.TryParse(raw, out CycleGoalCommand? command, out _));
        Assert.True(CycleGoalRules.Apply(CycleGoalSet.Empty, command!, 1, 1).Success);
        Assert.False(CycleGoalCommandParser.TryParse("create-goal " + JsonSerializer.Serialize(new { title = title + "🌿", criteria = "x" }), out _, out _));
        Assert.False(CycleGoalCommandParser.TryParse("show-goal {}" + new string(' ', CycleGoalLimits.MaximumCommandCodeUnits), out _, out _));
        Assert.False(CycleGoalCommandParser.TryParse("create-goal {\"title\":\"" + '\uD800' + "\",\"criteria\":\"x\"}", out _, out _));
        Assert.True(CycleGoalCommandParser.TryParse("create-goal {\"title\":\"\\uD83C\\uDF3F\",\"criteria\":\"x\"}", out _, out _));
    }

    [Fact]
    public void ConstructingCommandDirectlyCannotBypassValidationOrIdentityExhaustion() {
        CycleGoalOperationResult invalid = CycleGoalRules.Apply(CycleGoalSet.Empty,
            new(CycleGoalOperationKind.Create, "{\"title\":null,\"criteria\":\"x\"}"), 1, 1);
        Assert.False(invalid.Success); Assert.Same(CycleGoalSet.Empty, invalid.After);
        var exhausted = new CycleGoalSet(nextGoalId: long.MaxValue);
        Assert.Same(exhausted, Apply(exhausted, "create-goal {\"title\":\"x\",\"criteria\":\"x\"}").After);
        var parent = new CycleGoalSet([new("G1", "parent", "criteria")], nextGoalId: 2, nextLinkId: long.MaxValue);
        Assert.Same(parent, Apply(parent, "create-goal {\"title\":\"x\",\"criteria\":\"x\",\"parent\":\"G1\",\"linkReason\":\"r\",\"focus\":true}").After);
        Assert.Equal(2, parent.NextGoalId);
        string tooLongReason = new('r', CycleGoalLimits.ReasonScalars + 1);
        var noOp = new CycleGoalCommand(CycleGoalOperationKind.Update, JsonSerializer.Serialize(new { id = "G1", title = "parent", reason = tooLongReason }));
        Assert.False(CycleGoalRules.Apply(parent, noOp, 1, 1).Success);
    }

    [Fact]
    public void ImmutableSnapshotsOwnCopiedCollectionsAndValidateStoredShape() {
        var goals = new List<CycleGoal> { new("G1", "x", "y") };
        var focus = new List<string> { "G1" };
        var set = new CycleGoalSet(goals, focus: focus, nextGoalId: 2);
        goals.Clear(); focus.Clear();
        Assert.Single(set.Goals); Assert.Single(set.Focus); set.Validate();
        Assert.Throws<InvalidOperationException>(() => new CycleGoalSet([new("G1", "x", "y", CycleGoalStatus.Paused)], nextGoalId: 2).Validate());
        Assert.Throws<InvalidOperationException>(() => new CycleGoalSet([new("G1", "x", "y")], [new("L1", "G1", "G1", "r")], nextGoalId: 2, nextLinkId: 2).Validate());
    }

    [Fact]
    public void SnapshotBoundariesRejectFutureMarkersAndRulesRejectBackwardOperations() {
        var finished = new CycleGoalSet([new("G1", "x", "y", CycleGoalStatus.Completed, endNote: "判断",
            finishedContextSequence: 2, finishedOperationSequence: 3)], nextGoalId: 2);
        finished.ValidateThrough(2, 3);
        Assert.Throws<InvalidOperationException>(() => finished.ValidateThrough(2, 2));
        Assert.Throws<InvalidOperationException>(() => finished.ValidateThrough(2, 0));
        Assert.Throws<InvalidOperationException>(() => finished.ValidateThrough(0, 0));
        var revised = new CycleGoalSet([new("G1", "新目的", "新标准", revisions:
            [new(CycleGoalStatus.Open, "旧目的", "旧标准", null, "调整", 3, 4)])], nextGoalId: 2);
        revised.ValidateThrough(3, 4);
        revised.ValidateThrough(4, 0);
        Assert.Throws<InvalidOperationException>(() => revised.ValidateThrough(3, 3));
        CycleGoalOperationResult backwardFinish = Apply(revised,
            "finish-goal {\"id\":\"G1\",\"outcome\":\"completed\",\"note\":\"错误逆序\"}", 3, 3);
        Assert.False(backwardFinish.Success); Assert.Same(revised, backwardFinish.After);
        Assert.False(Apply(revised, "show-goal {}", 4, 3).Success);
        Assert.False(Apply(finished, "reopen-goal {\"id\":\"G1\",\"reason\":\"错误逆序\"}", 3, 2).Success);
        CycleGoalSet.Empty.ValidateThrough(0, 0);
        new CycleGoalSet([new("G1", "预置目的", "标准")], nextGoalId: 2).ValidateThrough(0, 0);
    }
}
