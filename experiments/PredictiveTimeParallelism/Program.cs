using System.Text.Json;
using PredictiveTimeParallelism;

Options? options = null;
async Task WriteAsync(object result) {
    var json = JsonSerializer.Serialize(result, JsonFormat.Options);
    if (options?.Output is { } path) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, json);
    }
    Console.WriteLine(json);
}
try {
    options = Options.Parse(args);
    object result = options.ModelTimeCheck ? await DramaBoard.Experiments.PredictiveTimeParallelism.Causal.ModelTimeWindows.RunAsync()
        : options.InfluenceCheck ? DramaBoard.Experiments.PredictiveTimeParallelism.Influence.InfluenceExperiments.Run() : options.SelfCheck ? await Checks.RunAsync() : options.Matrix
        ? await Experiments.MatrixAsync(options) : await Experiments.RunAsync(options);
    await WriteAsync(result);
    return 0;
} catch (RunFailedException ex) {
    try { await WriteAsync(ex.Snapshot); }
    catch (Exception writeError) { Console.Error.WriteLine($"Snapshot write failed: {writeError.GetType().Name}: {writeError.Message}"); }
    Console.Error.WriteLine($"{ex.Snapshot.ErrorType}: {ex.Message}");
    return 1;
} catch (Exception ex) {
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
