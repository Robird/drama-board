namespace DramaBoard.Spatial;

internal static class EffectiveGraph
{
    internal static PassageEntryAccess EntryAccess(
        GraphDefinition definition,
        GraphSpatialState state,
        PassageDefinition passage) =>
        state.FindOverride(passage.Id)?.Access ?? passage.InitialEntryAccess;

    internal static bool TryResolveDirection(
        PassageDefinition passage,
        PlaceId fromPlaceId,
        out PlaceId toPlaceId,
        out bool entryAllowed)
    {
        if (fromPlaceId == passage.EndpointA)
        {
            toPlaceId = passage.EndpointB;
            entryAllowed = false;
            return true;
        }

        if (fromPlaceId == passage.EndpointB)
        {
            toPlaceId = passage.EndpointA;
            entryAllowed = false;
            return true;
        }

        toPlaceId = default;
        entryAllowed = false;
        return false;
    }

    internal static bool TryResolveDirection(
        GraphDefinition definition,
        GraphSpatialState state,
        PassageDefinition passage,
        PlaceId fromPlaceId,
        out PlaceId toPlaceId,
        out bool entryAllowed)
    {
        if (!TryResolveDirection(passage, fromPlaceId, out toPlaceId, out _))
        {
            entryAllowed = false;
            return false;
        }

        PassageEntryAccess access = EntryAccess(definition, state, passage);
        entryAllowed = fromPlaceId == passage.EndpointA
            ? access.EnterableFromA
            : access.EnterableFromB;
        return true;
    }
}
