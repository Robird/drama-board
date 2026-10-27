using System.Text.Json;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial.Tests.TestSupport;

internal static class ContactOracle {
    // Independent discovery: global entity pairs, no dirty set or stored output reads.
    public static ContactPrediction[] Enumerate(GraphDefinition definition, GraphSpatialState world) {
        var result = new List<ContactPrediction>();
        for (int i = 0; i < world.Entities.Count; i++) {
            var left = world.Entities[i];
            if (left.Location is not TraversingLocation a) { continue; }
            for (int j = i + 1; j < world.Entities.Count; j++) {
                var right = world.Entities[j];
                if (right.Location is not TraversingLocation b || a.PassageId != b.PassageId) { continue; }
                var key = new PassageContactKey(a.PassageId, left.Id, left.MovementGeneration, right.Id, right.MovementGeneration);
                if (!world.ConsumedContacts.Contains(key) && PassageContactCalculator.TryCalculate(definition, world, key, out var calculation)) {
                    result.Add(new(key, calculation.Due, calculation.Kind));
                }
            }
        }
        return result.OrderBy(p => p.ContactKey).ToArray();
    }

    public static void AssertMatches(GraphDefinition definition, SpatialSimulationState world) {
        var expected = Enumerate(definition, world.Business);
        Assert.Equal(expected, world.Contacts.Entries);
        var candidates = new SpatialContactOccurrenceRule(definition).Forecast(world, new SimulationRules(0, 100));
        Assert.Equal(expected.Length, candidates.Count);
        for (int i = 0; i < expected.Length; i++) {
            var key = expected[i].ContactKey;
            // Encode independently to catch accidental key drift along with missing candidates.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new object[] {
                "graph-spatial/contact", key.PassageId.Value, key.EntityA.Value, key.MovementGenerationA, key.EntityB.Value, key.MovementGenerationB });
            Assert.Equal(CandidateKey.FromBytes(bytes), candidates[i].Key);
            Assert.Equal(expected[i].Due, candidates[i].Due.ModelTime);
            Assert.Equal(new PassageContactOccurrenceData(key, expected[i].Kind), candidates[i].Data);
        }
    }
}
