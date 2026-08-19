namespace DramaBoard.Spatial;

/// <summary>Declares the spatial interpretation rules supported by this runtime.</summary>
public static class SpatialRules
{
    /// <summary>Gets the only spatial rules version executable by this runtime.</summary>
    public const ushort CurrentVersion = 1;

    internal static void EnsureSupported(SpatialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.RulesVersion != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Spatial rules version {definition.RulesVersion} is not supported by this runtime; " +
                $"supported version is {CurrentVersion}.");
        }
    }
}
