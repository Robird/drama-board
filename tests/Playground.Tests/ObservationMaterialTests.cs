using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class ObservationMaterialTests {
    [Fact]
    public void Scenario_has_an_unstarted_interaction_slot_and_advertises_handoff_before_first_delivery() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false).World;
        var actor = new PlaygroundCharacter("a", "阿青");
        PlaygroundWorld world = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor]), initial.Time);
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(world, "a");

        Assert.True(PlaygroundActions.SupportsHandoff(world));
        Assert.Null(world.Business.InteractionStates["a"].CurrentContext);
        Assert.Throws<InvalidOperationException>(() => PlaygroundDecisionContext.Create(world, "a"));
        Assert.Null(PlaygroundActions.Check(world, "a", PlaygroundCommand.Handoff("记录")));
        Assert.Null(Assert.Single(observation.Commands,
            entry => entry.Command.Kind == PlaygroundCommandKind.Handoff).Rejection);
    }

    [Fact]
    public async Task Visible_material_has_no_context_world_or_executable_queries_in_its_object_graph() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival().World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        PlaygroundWorld world = kernel.World;
        PlaygroundDecisionContext context = PlaygroundDecisionContext.Create(world, "a");
        Assert.Same(world.Business.InteractionStates["a"].CurrentContext, context.Context);
        Assert.Equal(Serialize(context.Observation), Serialize(PlaygroundDecisionContext.Observe(world, "a")));
        AssertPureVisibleGraph(context.Observation, new(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public async Task Gathering_context_does_not_enable_handoff_or_disclose_other_actors_private_material() {
        var a = new PlaygroundCharacter("a", "Alice");
        var b = new PlaygroundCharacter("b", "Bob", backpack: new ItemInventory(new Dictionary<string, int> { ["private.gem"] = 7 }));
        PlaygroundWorld initial = PlaygroundWorld.CreatePlayable(Hour(8), [a, b]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands());
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        PlaygroundWorld world = kernel.World;
        PlaygroundDecisionContext context = PlaygroundDecisionContext.Create(world, "a");
        string visible = Serialize(context.Observation);
        Assert.DoesNotContain("private.gem", visible, StringComparison.Ordinal);
        Assert.Equal(CharacterContextStatus.Open, context.Context!.Status);
        Assert.DoesNotContain(context.Observation.Commands, entry => entry.Command.Kind == PlaygroundCommandKind.Handoff);
        Assert.False(PlaygroundActions.SupportsHandoff(world));
        Assert.NotNull(context.Check(PlaygroundCommand.Handoff("disabled")));
        Assert.Same(b, world.GetCharacter("b"));
    }

    [Fact]
    public async Task Captured_visible_snapshot_remains_unchanged_after_successful_instant_operation() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival().World;
        PlaygroundObservation before = PlaygroundDecisionContext.Observe(world, "a");
        string frozen = Serialize(before);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.Use("item.water")));
        await StepWorldEvent(kernel);
        PlaygroundObservation after = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Equal(frozen, Serialize(before));
        Assert.Equal(1, before.Actor.Backpack.Count("item.water"));
        Assert.Equal(0, after.Actor.Backpack.Count("item.water"));
        Assert.Contains(before.Commands, entry => entry.Command.Kind == PlaygroundCommandKind.Handoff);
        AssertPureVisibleGraph(after, new(ReferenceEqualityComparer.Instance));
    }

    private static string Serialize(PlaygroundObservation observation) => JsonSerializer.Serialize(observation,
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // Follow the actual public material graph, including collection values. This guards the isolation
    // boundary without fixing display order, field count, or the choice of future durable containers.
    private static void AssertPureVisibleGraph(object? value, HashSet<object> seen) {
        if (value is null || value is string) { return; }
        Assert.False(value is Delegate or PlaygroundWorld or PlaygroundBusinessState or PlaygroundCharacter or
            PlaygroundDecisionContext or CharacterInteractionState or CharacterContext or PlaygroundExpedition or GraphSpatialState,
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
