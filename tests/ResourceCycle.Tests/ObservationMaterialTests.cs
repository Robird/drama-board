using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ObservationMaterialTests {
    [Fact]
    public void Scenario_capability_does_not_accept_handoff_without_required_interaction_state() {
        CycleWorld initial = CycleGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false).World;
        var actor = new CycleCharacter("a", "阿青");
        CycleWorld world = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor]), initial.Time);
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");

        Assert.True(CycleActions.SupportsHandoff(world));
        Assert.NotNull(context.Check(CycleCommand.Handoff("记录")));
        Assert.NotNull(Assert.Single(context.Observation.Commands,
            entry => entry.Command.Kind == CycleCommandKind.Handoff).Rejection);
    }

    [Fact]
    public void Visible_material_has_no_continuity_world_or_executable_queries_in_its_object_graph() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
        Assert.Same(world.GetCharacter("a").Continuity, context.Continuity);
        Assert.Equal(Serialize(context.Observation), Serialize(CycleDecisionContext.Observe(world, "a")));
        AssertPureVisibleGraph(context.Observation, new(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public void Gathering_history_does_not_enable_handoff_or_disclose_other_actors_private_material() {
        var a = new CycleCharacter("a", "Alice", continuity: new(draftHandoff: "A的计划"));
        var b = new CycleCharacter("b", "Bob", backpack: new ItemInventory(new Dictionary<string, int> { ["private.gem"] = 7 }),
            continuity: new(draftHandoff: "B的计划"));
        CycleWorld world = CycleWorld.CreatePlayable(Hour(8), [a, b]);
        CycleDecisionContext context = CycleDecisionContext.Create(world, "a");
        string visible = Serialize(context.Observation);
        Assert.DoesNotContain("A的计划", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("B的计划", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("private.gem", visible, StringComparison.Ordinal);
        Assert.Equal("A的计划", context.Continuity!.DraftHandoff);
        Assert.DoesNotContain(context.Observation.Commands, entry => entry.Command.Kind == CycleCommandKind.Handoff);
        Assert.False(CycleActions.SupportsHandoff(world));
        Assert.NotNull(context.Check(CycleCommand.Handoff("disabled")));
        Assert.Same(b, world.GetCharacter("b"));
    }

    [Fact]
    public async Task Captured_visible_snapshot_remains_unchanged_after_successful_instant_operation() {
        CycleWorld world = CycleGameFactory.CreateSurvival().World;
        CycleObservation before = CycleDecisionContext.Observe(world, "a");
        string frozen = Serialize(before);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: Commands(CycleCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        CycleObservation after = CycleDecisionContext.Observe(kernel.World, "a");
        Assert.Equal(frozen, Serialize(before));
        Assert.Equal(1, before.Actor.Backpack.Count("item.water"));
        Assert.Equal(0, after.Actor.Backpack.Count("item.water"));
        Assert.Contains(before.Commands, entry => entry.Command.Kind == CycleCommandKind.Handoff);
        AssertPureVisibleGraph(after, new(ReferenceEqualityComparer.Instance));
    }

    private static string Serialize(CycleObservation observation) => JsonSerializer.Serialize(observation,
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // Follow the actual public material graph, including collection values. This guards the isolation
    // boundary without fixing display order, field count, or the choice of future durable containers.
    private static void AssertPureVisibleGraph(object? value, HashSet<object> seen) {
        if (value is null || value is string) { return; }
        Assert.False(value is Delegate or CycleWorld or CycleBusinessState or CycleCharacter or
            CycleDecisionContext or CycleDecisionContinuity or CycleExpedition or GraphSpatialState,
            $"Unexpected object reachable from visible material: {value.GetType().Name}");
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || !seen.Add(value)) { return; }
        if (value is IEnumerable entries) {
            foreach (object? entry in entries) { AssertPureVisibleGraph(entry, seen); }
            return;
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) {
                AssertPureVisibleGraph(property.GetValue(value), seen);
            }
        }
    }
}
