using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;
bool hasStore = false;
try {
    var options = Options.Parse(args);
    if (options.Help) {
        Console.WriteLine("""
            山野镇 / Hillside — 小镇与山林中的持续生活
            playground init [--home PATH]  仅创建 playground.json；运行前准备连接配置
            playground [options]
              --home PATH          实例目录，默认调用时 CWD；不搜索父目录
              --store PATH         覆盖存档位置；管理时直接定位并绕过 home
              --ephemeral          世界仅内存运行（仍需 home；Gym 必须 off）
              --branch ID          已有分支；多分支时必须指定
              --content hillside|gentle 新局内容；续局为一致性断言
              --characters 1|2|3  新局人数，默认 3；续局省略沿用存档
              --llm-connections PATH 覆盖连接目录，角色默认由其默认连接控制
              --character-controls PATH 选择逐角色 LLM/Human 控制文件
              --no-character-controls   清除 home 的角色控制文件选择
              --max-calls N       共享模型调用预算（默认 128）
              --max-failures N    连续协议错误上限（默认 3）
              --max-context-chars N Context 字符上限（默认 200000）
              --llm-log PATH      本次独立运行证据文件
              --until-hour N      绝对模拟小时上界；达到后暂停
              --steps N           提交转换上限（默认 10000）
              --gym off|record     轨迹记录模式；反馈独立默认启用
              --branches          列出分支，不运行游戏
              --feedbacks         列出独立反馈 JSONL；可选 --branch / --actor；--ephemeral 查询 home 内存运行报告
              --gym-export FILE   导出到新文件；需 --actor，可选 --branch
              --fork-feedback ID   从反馈的决定前 S 注册分支，不依赖 Gym
              --gym-fork-request ID 从请求的决定前 S 注册分支
              --fork-label LABEL  分叉名称；另用 --branch 续跑
              --actor ID          管理查询角色
              --help              显示帮助，不读取 home

            home 路径相对 home，CLI 路径相对调用时 CWD。管理省略 --store 时读取 home。
            :quit、EOF 或 Ctrl+C 暂停；同一 home 续局可重新选择控制文件。
            :feedback <正文> 提交开发反馈；不推进时间、不确认消息。LLM 使用 feedback 工具。
            """);
        return 0;
    }
    if (options.Init) {
        PlaygroundHome.Initialize(options.HomePath, Console.Out);
        return 0;
    }
    var resolved = options.Resolve();
    hasStore = resolved.Store is not null;
    if (resolved.Management is { } management) {
        GymCommands.Run(resolved.Store ?? resolved.HomePath!, resolved.Branch, management, Console.Out);
        return 0;
    }
    if (resolved.Branch is not null && !Directory.Exists(resolved.Store)) { throw new ArgumentException("指定分支需要已有存档。"); }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += handler;
    try {
        return await PlaygroundHost.RunAsync(resolved.Store, resolved.Host, cancellation.Token, characterCount: resolved.CharacterCount);
    } finally { Console.CancelKeyPress -= handler; }
} catch (OperationCanceledException) {
    Console.Error.WriteLine(hasStore ? "已暂停。已提交进度已保存，使用同一 home 续局。" : "已暂停；内存进度未保存。");
    return 130;
} catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException or FormatException or JsonException) {
    Console.Error.WriteLine($"Hillside: {error.Message}");
    return 1;
}
