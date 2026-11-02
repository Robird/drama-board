using System.Globalization;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

internal sealed record ResolvedOptions(string? Store, PlaygroundHostOptions Host, int? CharacterCount,
    string? Branch, GymManagementOptions? Management, string? HomePath);

internal sealed class Options {
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
    public string LaunchDirectory { get; private init; } = "";
    public bool Help => values.ContainsKey("--help");
    public bool Init => values.ContainsKey("init");
    public string HomePath => PathValue("--home") ?? LaunchDirectory;
    public static Options Parse(string[] args, string? launchDirectory = null) {
        var result = new Options { LaunchDirectory = Path.GetFullPath(launchDirectory ?? Environment.CurrentDirectory) };
        string[] flags = ["--help", "--ephemeral", "--no-character-controls", "--branches", "--feedbacks"];
        string[] valued = ["--home", "--store", "--branch", "--content", "--characters", "--until-hour", "--steps", "--gym",
            "--llm-connections", "--character-controls", "--llm-log", "--max-calls", "--max-failures", "--max-context-chars",
            "--actor", "--gym-export", "--fork-feedback", "--gym-fork-request", "--fork-label"];
        for (int i = 0; i < args.Length; i++) {
            string name = args[i];
            string? value = null;
            if (name == "init" && i == 0 || flags.Contains(name, StringComparer.Ordinal)) { }
            else if (valued.Contains(name, StringComparer.Ordinal)) {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal)) {
                    throw new ArgumentException($"参数缺少值：{name}");
                }
                value = args[i];
            } else { throw new ArgumentException($"未知参数：{name}；使用 --help 查看用法。"); }
            if (!result.values.TryAdd(name, value)) { throw new ArgumentException($"重复参数：{name}"); }
        }
        result.Validate();
        return result;
    }
    public ResolvedOptions Resolve() {
        if (Help || Init) { throw new InvalidOperationException("help/init 不解析运行配置。"); }
        GymManagementOptions? management = Management();
        if (management is not null && PathValue("--store") is { } directStore) {
            return new(directStore, new(Branch: Branch()), null, Branch(), management, null);
        }
        var home = PlaygroundHome.Load(HomePath);
        string HomeFile(string path) => Path.GetFullPath(path, home.HomePath);
        string? store = Has("--ephemeral") ? null : PathValue("--store") ?? HomeFile(home.Store);
        string? branch = Branch();
        if (management is not null) { return new(store, new(Branch: branch), null, branch, management, home.HomePath); }
        GymMode? configuredGym = Get("--gym") is { } selectedGym ? PlaygroundHome.ParseGym(selectedGym) : home.Gym;
        if (store is null && (configuredGym == GymMode.Record || branch is not null)) {
            throw new ArgumentException("--ephemeral 不能配合 branch 或显式 gym record；可省略 gym 或使用 --gym off。");
        }
        GymMode gym = configuredGym ?? (store is null ? GymMode.Off : GymMode.Record);
        ModelTime? until = Get("--until-hour") is { } hour ? new ModelTime(checked(long.Parse(hour, CultureInfo.InvariantCulture) * 3600000)) : null;
        string? controls = Has("--no-character-controls") ? null : PathValue("--character-controls")
            ?? (home.CharacterControls is { } file ? HomeFile(file) : null);
        var host = new PlaygroundHostOptions(MaxCalls: Number("--max-calls") ?? home.MaxCalls,
            MaxFailures: Number("--max-failures") ?? home.MaxFailures,
            MaxContextCharacters: Number("--max-context-chars") ?? home.MaxContextCharacters,
            LogPath: PathValue("--llm-log"), Gym: gym == GymMode.Record,
            Branch: branch, Until: until, MaxSteps: Number("--steps") ?? home.Steps, Content: Get("--content"),
            ConnectionsPath: PathValue("--llm-connections") ?? HomeFile(home.LlmConnections), CharacterControlsPath: controls, HomePath: home.HomePath);
        return new(store, host, Number("--characters"), branch, null, home.HomePath);
    }
    private void Validate() {
        if (Has("--store") && Has("--ephemeral")) { throw new ArgumentException("--store 与 --ephemeral 互斥。"); }
        if (Has("--character-controls") && Has("--no-character-controls")) { throw new ArgumentException("角色控制文件选择与清除互斥。"); }
        foreach (string name in new[] { "--steps", "--max-calls", "--max-failures", "--max-context-chars", "--characters" }) {
            if (Get(name) is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)) {
                throw new ArgumentException($"{name} 必须为正整数。");
            }
        }
        if (Number("--characters") > 3) { throw new ArgumentException("--characters 必须为 1、2 或 3。"); }
        if (Get("--content") is { } content && content is not ("hillside" or "gentle")) { throw new ArgumentException("--content 必须为 hillside 或 gentle。"); }
        if (Get("--gym") is { } gym) { PlaygroundHome.ParseGym(gym); }
        if (Get("--until-hour") is { } hour && (!long.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed > long.MaxValue / 3600000)) { throw new ArgumentException("--until-hour 必须为有效非负整数小时。"); }
        if (Help) { return; }
        if (Init) {
            if (values.Keys.Any(name => name is not ("init" or "--home"))) { throw new ArgumentException("init 仅接受 --home。"); }
            return;
        }
        var management = Management();
        if (management is not null) {
            string[] allowed = management.Kind switch {
                GymManagementKind.Branches => ["--home", "--store", "--branches"],
                GymManagementKind.Feedbacks => ["--home", "--store", "--ephemeral", "--feedbacks", "--branch", "--actor"],
                GymManagementKind.Export => ["--home", "--store", "--gym-export", "--branch", "--actor"],
                _ => ["--home", "--store", "--fork-feedback", "--gym-fork-request", "--fork-label"]
            };
            if (values.Keys.Any(name => !allowed.Contains(name, StringComparer.Ordinal))) { throw new ArgumentException("管理模式不能混用游戏、模型或其他管理配置。"); }
            if (Has("--ephemeral") && Has("--branch")) { throw new ArgumentException("内存运行反馈没有持久分支，不能使用 --branch 筛选。"); }
            if (management.Kind == GymManagementKind.Export && Get("--actor") is null) { throw new ArgumentException("--gym-export 必须指定 --actor。"); }
            if (management.Kind is GymManagementKind.ForkFeedback or GymManagementKind.ForkRequest && Get("--fork-label") is null) { throw new ArgumentException("分叉必须指定 --fork-label。"); }
        } else if (Has("--actor") || Has("--fork-label")) { throw new ArgumentException("--actor / --fork-label 仅用于对应管理模式。"); }
        _ = Branch();
    }
    private GymManagementOptions? Management() {
        string[] modes = ["--branches", "--feedbacks", "--gym-export", "--fork-feedback", "--gym-fork-request"];
        var selected = modes.Where(Has).ToArray();
        if (selected.Length > 1) { throw new ArgumentException("管理模式只能选择一种。"); }
        if (selected.Length == 0) { return null; }
        var kind = selected[0] switch { "--branches" => GymManagementKind.Branches, "--feedbacks" => GymManagementKind.Feedbacks,
            "--gym-export" => GymManagementKind.Export, "--fork-feedback" => GymManagementKind.ForkFeedback, _ => GymManagementKind.ForkRequest };
        return new(kind, Get("--actor"), PathValue("--gym-export"), Get("--fork-feedback") ?? Get("--gym-fork-request"), Get("--fork-label"));
    }
    private string? Branch() {
        if (Get("--branch") is not { } branch) { return null; }
        try { return PlaygroundDurableHistory.FormatBranchId(PlaygroundDurableHistory.ParseBranchId(branch)); }
        catch (FormatException error) { throw new ArgumentException("--branch 必须为有效分支 ID。", error); }
    }
    private bool Has(string name) => values.ContainsKey(name);
    private string? Get(string name) => values.GetValueOrDefault(name);
    private int? Number(string name) => Get(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
    private string? PathValue(string name) => Get(name) is { } value ? Path.GetFullPath(value, LaunchDirectory) : null;
}
