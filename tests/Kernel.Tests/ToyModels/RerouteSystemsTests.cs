using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Tests.ToyModels;

public sealed class RerouteSystemsTests
{
    [Fact]
    public void Run_RerouteBeforeArrival_InvalidatesOriginalForecast()
    {
        ISimSystem<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>[] systems =
        [
            new TravelSystem(AtSecond(10), AtSecond(17)),
            new ScheduledRerouteSystem(),
        ];
        var loop = new SimulationLoop<RerouteWorld, RerouteCandidatePayload, RerouteEventPayload>(
            systems,
            new RerouteReducer());
        var journal = new InMemoryJournal<RerouteEventPayload>();

        SimulationRunResult<RerouteWorld, RerouteEventPayload> result = loop.Run(
            RerouteWorld.Start("B"),
            SimulationCursor.CreateInitial(lineageId: 1, ModelTime.Zero),
            AtSecond(20),
            journal,
            [
                new UncommittedDomainEvent<RerouteEventPayload>(
                    RerouteEventKinds.RerouteScheduled,
                    new RerouteScheduledEventPayload(AtSecond(5), "C")),
            ]);

        Assert.Collection(
            journal.Events,
            domainEvent =>
            {
                Assert.Equal(ModelTime.Zero, domainEvent.Timestamp.ModelTime);
                Assert.Equal(new RerouteScheduledEventPayload(AtSecond(5), "C"), domainEvent.Payload);
            },
            domainEvent =>
            {
                Assert.Equal(AtSecond(5), domainEvent.Timestamp.ModelTime);
                Assert.Equal(new ReroutedEventPayload("C"), domainEvent.Payload);
            },
            domainEvent =>
            {
                Assert.Equal(AtSecond(17), domainEvent.Timestamp.ModelTime);
                Assert.Equal(new ArrivedEventPayload("C"), domainEvent.Payload);
            });
        Assert.DoesNotContain(
            journal.Events,
            domainEvent => domainEvent.Payload is ArrivedEventPayload { Destination: "B" });
        Assert.True(journal.Events.Zip(journal.Events.Skip(1)).All(pair => pair.First.Timestamp <= pair.Second.Timestamp));
        Assert.Equal(
            RerouteWorld.Start("C") with { HasRedirected = true, HasArrived = true },
            result.World);
    }

    private static ModelTime AtSecond(long seconds) => ModelTime.Zero + ModelDuration.FromSeconds(seconds);
}
