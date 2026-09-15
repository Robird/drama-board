using System.Text.Json;
using DramaBoard.Server.FreePlay;
using DramaBoard.Server.Web;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
if (string.IsNullOrEmpty(builder.Configuration["urls"])) {
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}
builder.Services.AddSingleton<FreePlaySession>();
builder.Services.AddHostedService<SessionService>();
var app = builder.Build();
app.MapGet("/api/player/view", (FreePlaySession session) => session.GetPlayerView());
app.MapGet("/api/dev/view", (FreePlaySession session) => session.GetDevView());
app.MapPost("/api/player/decisions", async (HttpRequest request, FreePlaySession session) => {
    try {
        using JsonDocument body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        if (!DecisionInput.TryRead(body.RootElement, out DecisionInput? input)) {
            return Results.Json(new { code = "invalid_input", message = "需要 decisionId、actionKind、exitId 三个字符串字段。" }, statusCode: 400);
        }
        var result = session.Submit(input!.DecisionId, input.ActionKind, input.ExitId);
        return Results.Json(new { code = result.Code, message = result.Message }, statusCode: result.StatusCode);
    } catch (JsonException) {
        return Results.Json(new { code = "invalid_json", message = "请求不是有效 JSON。" }, statusCode: 400);
    }
});
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/player"));
foreach (string route in new[] { "/player", "/dev" }) {
    app.MapGet(route, () => {
        string entry = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        return File.Exists(entry) && new FileInfo(entry).Length > 0
            ? Results.File(entry, "text/html; charset=utf-8")
            : Results.Problem("网页资源缺失；请在仓库执行 pwsh -File scripts/Publish-Server.ps1，再启动发布目录中的服务。", statusCode: 503);
    });
}
app.Run();

public partial class Program { }
