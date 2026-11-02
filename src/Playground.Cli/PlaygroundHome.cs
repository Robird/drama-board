using System.Text.Json;

internal enum GymMode { Off, Record, Feedback }

internal sealed record PlaygroundHome(string HomePath, GymMode Gym = GymMode.Off, int MaxCalls = 128,
    int MaxFailures = 3, int MaxContextCharacters = 200000, int Steps = 10000,
    string Store = "store", string LlmConnections = "llm-connections.json", string? CharacterControls = null) {
    public static PlaygroundHome Load(string homePath) {
        string home = Path.GetFullPath(homePath);
        string path = Path.Combine(home, "playground.json");
        if (!File.Exists(path)) { throw new ArgumentException($"缺少启动文件：{path}；请先执行 init --home PATH。"); }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("playground.json 必须为对象。"); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new PlaygroundHome(home);
        bool version = false;
        foreach (var field in root.EnumerateObject()) {
            if (!seen.Add(field.Name)) { throw new ArgumentException($"重复的 home 字段：{field.Name}"); }
            switch (field.Name) {
                case "version":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out int number) || number != 1) {
                        throw new ArgumentException("home version 必须为整数 1。");
                    }
                    version = true; break;
                case "gym": result = result with { Gym = ParseGym(Text(field.Value, field.Name)) }; break;
                case "maxCalls": result = result with { MaxCalls = Positive(field.Value, field.Name) }; break;
                case "maxFailures": result = result with { MaxFailures = Positive(field.Value, field.Name) }; break;
                case "maxContextChars": result = result with { MaxContextCharacters = Positive(field.Value, field.Name) }; break;
                case "steps": result = result with { Steps = Positive(field.Value, field.Name) }; break;
                case "store": result = result with { Store = Text(field.Value, field.Name) }; break;
                case "llmConnections": result = result with { LlmConnections = Text(field.Value, field.Name) }; break;
                case "characterControls": result = result with { CharacterControls = field.Value.ValueKind == JsonValueKind.Null ? null : Text(field.Value, field.Name) }; break;
                default: throw new ArgumentException($"未知的 home 字段：{field.Name}");
            }
        }
        if (!version) { throw new ArgumentException("home 缺少 version。"); }
        return result;
    }

    public static void Initialize(string homePath, TextWriter output) {
        string home = Path.GetFullPath(homePath);
        Directory.CreateDirectory(home);
        using (var stream = new FileStream(Path.Combine(home, "playground.json"), FileMode.CreateNew, FileAccess.Write)) {
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.WriteLine("{ \"version\": 1 }");
        }
        output.WriteLine("已创建启动文件；运行前需准备默认连接配置 llm-connections.json。参见 config/llm-connections.example.json 和 docs/design/playground-home.md。");
    }

    internal static GymMode ParseGym(string value) => value switch {
        "off" => GymMode.Off, "record" => GymMode.Record, "feedback" => GymMode.Feedback,
        _ => throw new ArgumentException("gym 必须为 off、record 或 feedback。")
    };
    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new ArgumentException($"{field} 必须为非空字符串。");
    private static int Positive(JsonElement value, string field) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0
        ? number : throw new ArgumentException($"{field} 必须为正整数。");
}
