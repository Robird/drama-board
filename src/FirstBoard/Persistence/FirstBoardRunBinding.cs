using System.Security.Cryptography;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.FirstBoard.Persistence;

/// <summary>The immutable content, scheduling configuration and strategy identity of one run.
/// Successive States share this object; the dynamic world is never encoded as JSON.</summary>
[DurableType("DramaBoard.FirstBoard.RunBinding", 1)]
public sealed partial class FirstBoardRunBinding : DurableBase
{
    [DurableField(1)] private readonly byte[] _definitionJson;
    [DurableField(2)] private readonly string _definitionSha256;
    [DurableField(3)] private readonly string _rulesetId;
    [DurableField(4)] private readonly int _maxTransitionsPerModelTime;
    [DurableField(5)] private readonly string[] _driverActorIds;
    [DurableField(6)] private readonly string _driverPolicyId;

    internal FirstBoardRunBinding(ScenarioInstance scenario, SimulationRules rules, FirstBoardDriverBinding drivers)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(drivers);
        if (scenario.WorldSeed != rules.WorldSeed)
        {
            throw new ArgumentException("Scenario and scheduling rules must use the same world seed.", nameof(rules));
        }
        _definitionJson = scenario.Definition.ToCanonicalJsonUtf8();
        _definitionSha256 = scenario.DefinitionSha256;
        _rulesetId = scenario.Definition.RulesetId;
        _maxTransitionsPerModelTime = rules.MaxTransitionsPerModelTime;
        _driverActorIds = drivers.ActorIds.ToArray();
        _driverPolicyId = drivers.PolicyId;
    }

    public string DefinitionSha256 => _definitionSha256;
    public string RulesetId => _rulesetId;
    public int MaxTransitionsPerModelTime => _maxTransitionsPerModelTime;
    public FirstBoardDriverBinding DriverBinding => new(_driverActorIds, _driverPolicyId);
    public byte[] CopyDefinitionContent() => _definitionJson.ToArray();

    internal ScenarioInstance RestoreScenario(ulong worldSeed)
    {
        if (_definitionJson is null || _definitionJson.Length == 0 ||
            string.IsNullOrWhiteSpace(_definitionSha256) ||
            string.IsNullOrWhiteSpace(_rulesetId) || _maxTransitionsPerModelTime <= 0 ||
            _driverActorIds is null || string.IsNullOrWhiteSpace(_driverPolicyId))
        {
            throw new InvalidDataException("The saved run binding is incomplete.");
        }
        string actualHash = Convert.ToHexString(SHA256.HashData(_definitionJson)).ToLowerInvariant();
        if (!string.Equals(actualHash, _definitionSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The saved scenario content does not match its hash.");
        }
        FirstBoardDriverBinding drivers = DriverBinding;
        if (!_driverActorIds.SequenceEqual(drivers.ActorIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Saved driver actor IDs must be in canonical order.");
        }
        ScenarioDefinition definition = StoredScenarioContent.Read(_definitionJson);
        if (!string.Equals(definition.RulesetId, _rulesetId, StringComparison.Ordinal) ||
            !_definitionJson.AsSpan().SequenceEqual(definition.ToCanonicalJsonUtf8()))
        {
            throw new InvalidDataException("The saved scenario is not the exact supported canonical content.");
        }
        return new ScenarioInstance(definition, worldSeed);
    }

    internal SimulationRules RestoreRules(ulong worldSeed) => new(worldSeed, _maxTransitionsPerModelTime);
}
