namespace DramaBoard.Kernel.Tests.ToyModels;

internal readonly record struct RandomSampleCoordinates(
    ulong StreamId,
    ulong Generation,
    ulong SampleIndex);