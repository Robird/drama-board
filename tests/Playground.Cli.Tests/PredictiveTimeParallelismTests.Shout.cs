namespace DramaBoard.Playground.Cli.Tests;

public sealed partial class PredictiveTimeParallelismTests {
    [Fact]
    public async Task One_hop_remote_shout_invalidates_candidate_before_the_receiver_relays_at_the_same_tick() {
        using var serial = new Run(TemporalInitial(TemporalHour(10), ["camp", "stream"]));
        await serial.Execute(new ImmediateClient(Act("shout original-one-hop")),
            new ImmediateClient(Act("shout relay-only-after-receiving")));

        using var parallel = new Run(TemporalInitial(TemporalHour(10), ["camp", "stream"]));
        PlaygroundDecisionContext opening = Opening(parallel.History.State, "b");
        Assert.Empty(opening.Observation.OtherCharacters);
        var provider = new HeldClient(Act("shout stale-uninformed-relay"));
        await using var candidate = new Candidate(opening, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fallback = new ImmediateClient(Act("shout relay-only-after-receiving"));
        var gate = new AdoptionClient(candidate, fallback);
        await parallel.Execute(new ImmediateClient(Act("shout original-one-hop")), gate);

        Assert.Equal(1, gate.Discarded);
        Assert.Equal(0, gate.Adopted);
        string fresh = RequestValue(Assert.Single(fallback.Requests));
        Assert.Contains("original-one-hop", fresh);
        Assert.DoesNotContain("original-one-hop", RequestValue(candidate.Request!));
        Assert.Equal(serial.Inputs, parallel.Inputs);
        Assert.Equal(serial.Transitions, parallel.Transitions);
        Assert.Equal(serial.FinalMaterial(), parallel.FinalMaterial());
        Assert.Contains("relay-only-after-receiving", parallel.FinalMaterial());
        Assert.DoesNotContain("stale-uninformed-relay", parallel.FinalMaterial());
        Assert.Equal(TemporalHour(10), parallel.History.State.Time);
        Assert.Equal(1, parallel.History.State.GetCharacter("a").Communication.SentCount);
        Assert.Equal(1, parallel.History.State.GetCharacter("b").Communication.SentCount);
    }
}
