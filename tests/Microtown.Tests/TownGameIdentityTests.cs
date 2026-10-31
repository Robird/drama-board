using System.Security.Cryptography;
using Atelia.DurableGraph;
using DramaBoard.ResourceCycle;

namespace DramaBoard.Microtown.Tests;

public sealed class TownGameIdentityTests {
    [Fact]
    public void RealGamesRejectEachOthersRepositoriesWithoutModifyingEitherSave() {
        string parent = Path.Combine(Path.GetTempPath(), "town-game-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string townPath = Path.Combine(parent, "town");
        string forestPath = Path.Combine(parent, "forest");
        try {
            var town = TownGameFactory.Create(2);
            var forest = CycleGameFactory.CreateSurvival(enableWeather: false);
            CommitId townCommit, forestCommit;
            using (var history = TownDurableHistory.Create(townPath, town.World, town.Cursor)) {
                townCommit = history.CompletedStateCommitId;
            }
            using (var history = CycleDurableHistory.Create(forestPath, forest.World, forest.Cursor)) {
                forestCommit = history.CompletedStateCommitId;
            }
            string[] before = Fingerprint(parent);
            Assert.ThrowsAny<InvalidDataException>(() => {
                using var wrong = TownDurableHistory.Open(forestPath);
            });
            Assert.Equal(before, Fingerprint(parent));
            Assert.ThrowsAny<InvalidDataException>(() => {
                using var wrong = CycleDurableHistory.Open(townPath);
            });
            Assert.Equal(before, Fingerprint(parent));
            using (var reopened = TownDurableHistory.Open(townPath)) {
                Assert.Equal(townCommit, reopened.CompletedStateCommitId);
                Assert.Equal(town.Cursor, reopened.Cursor);
                Assert.Equal(3, reopened.State.Business.Characters.Count);
                Assert.Null(reopened.PendingEvent);
            }
            using (var reopened = CycleDurableHistory.Open(forestPath)) {
                Assert.Equal(forestCommit, reopened.CompletedStateCommitId);
                Assert.Equal(forest.Cursor, reopened.Cursor);
                Assert.Single(reopened.State.Business.Characters);
                Assert.Null(reopened.PendingEvent);
            }
            Assert.Equal(before, Fingerprint(parent));
        } finally {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string[] Fingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();
}
