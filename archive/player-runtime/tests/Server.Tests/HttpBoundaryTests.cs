using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DramaBoard.Server.Tests;

public sealed class HttpBoundaryTests {
    [Fact]
    public async Task StrictParsingRejectsBadBodiesAndKeepsThePendingRequest() {
        await using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        FreePlaySession session = factory.Services.GetRequiredService<FreePlaySession>();
        await session.WaitForViewAsync(view => view.Status == "waiting", timeout.Token);
        JsonElement before = await ReadViewAsync(client, timeout.Token);
        string activationId = before.GetProperty("request").GetProperty("activationId").GetString()!;
        string valid = JsonSerializer.Serialize(new {
            activationId,
            program = new object[] { new { kind = "move", passage = "ab" } },
            workspace = "plan"
        });
        string[] rejectedBodies = [
            "{", "[]", "{}",
            valid[..^1] + ",\"extra\":true}",
            valid[..^1] + ",\"activationId\":\"ignored-duplicate\"}",
            JsonSerializer.Serialize(new { activationId, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = (object?)null, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = "ab", workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { activationId, program = Array.Empty<object>(), workspace = 123 }),
            JsonSerializer.Serialize(new { activationId = "  ", program = Array.Empty<object>(), workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { 123 }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId = 123, program = Array.Empty<object>(), workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "teleport", passage = "ab" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "move" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "move", passage = "ab", note = "extra" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "move", passage = 123 } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "think" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "think", note = "x", passage = "ab" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "stay", passage = "ab" } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "move", passage = " " } }, workspace = "plan" }),
            JsonSerializer.Serialize(new { activationId, program = new object[] { new { kind = "think", note = "" } }, workspace = "plan" }),
            "{\"activationId\":\"" + activationId + "\",\"program\":[{\"kind\":\"move\",\"passage\":\"ab\",\"passage\":\"cd\"}],\"workspace\":\"plan\"}",
        ];
        foreach (string body in rejectedBodies) {
            using var response = await client.PostAsync("/api/player/programs", new StringContent(body, Encoding.UTF8, "application/json"), timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            JsonElement error = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.False(string.IsNullOrEmpty(error.GetProperty("code").GetString()));
            JsonElement after = await ReadViewAsync(client, timeout.Token);
            Assert.Equal(before.GetProperty("modelTimeMs").GetInt64(), after.GetProperty("modelTimeMs").GetInt64());
            Assert.Equal(activationId, after.GetProperty("request").GetProperty("activationId").GetString());
        }
        // An explicitly empty program is a legal stay response and ends the run in staying.
        string empty = JsonSerializer.Serialize(new { activationId, program = Array.Empty<object>(), workspace = "" });
        using var accepted = await client.PostAsync("/api/player/programs", new StringContent(empty, Encoding.UTF8, "application/json"), timeout.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await session.WaitForViewAsync(view => view.Status == "staying", timeout.Token);
    }

    [Fact]
    public async Task ProgramSubmissionThroughHttpExecutesMoveAndThinkAndShowsNotes() {
        await using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        FreePlaySession session = factory.Services.GetRequiredService<FreePlaySession>();
        await session.WaitForViewAsync(view => view.Status == "waiting" && view.Location == "A", timeout.Token);
        JsonElement before = await ReadViewAsync(client, timeout.Token);
        string activationId = before.GetProperty("request").GetProperty("activationId").GetString()!;
        string body = JsonSerializer.Serialize(new {
            activationId,
            program = new object[] { new { kind = "move", passage = "ab" }, new { kind = "think", note = "note" } },
            workspace = "出发前的想法"
        });
        using var accepted = await client.PostAsync("/api/player/programs", new StringContent(body, Encoding.UTF8, "application/json"), timeout.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await session.WaitForViewAsync(view => view.Status == "waiting" && view.Location == "B", timeout.Token);

        JsonElement view = await ReadViewAsync(client, timeout.Token);
        string workspace = view.GetProperty("workspace").GetString()!;
        Assert.Contains("出发前的想法", workspace);
        Assert.Contains("[1000] note", workspace);
        JsonElement request = view.GetProperty("request");
        Assert.Equal("ThinkCompleted", request.GetProperty("reason").GetString());
        Assert.Empty(request.GetProperty("remainingProgram").EnumerateArray());
        Assert.Contains("[1000] note", request.GetProperty("workspace").GetString());
    }

    [Fact]
    public async Task PlayerAndDevHaveSeparateMaterialsAndCancelledRequestDoesNotStopSession() {
        await using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        FreePlaySession session = factory.Services.GetRequiredService<FreePlaySession>();
        await session.WaitForViewAsync(view => view.Status == "waiting", timeout.Token);
        JsonElement player = await ReadViewAsync(client, timeout.Token);
        foreach (string forbidden in new[] {
            "cursor", "worldVersion", "completedEvents", "causeKey", "fault", "lastRejection",
            "stackTrace", "processMode", "controlRevision", "pendingActivationId"
        }) {
            Assert.DoesNotContain(forbidden, AllPropertyNames(player));
        }
        JsonElement dev = await client.GetFromJsonAsync<JsonElement>("/api/dev/view", timeout.Token);
        Assert.Equal(player.GetProperty("runId").GetString(), dev.GetProperty("runId").GetString());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/player/view", canceled.Token));
        JsonElement fresh = await ReadViewAsync(client, timeout.Token);
        Assert.Equal(player.GetProperty("request").GetProperty("activationId").GetString(),
            fresh.GetProperty("request").GetProperty("activationId").GetString());
        Assert.Equal("waiting", fresh.GetProperty("status").GetString());
        client.Dispose();
        using HttpClient reopened = factory.CreateClient();
        JsonElement afterReopen = await ReadViewAsync(reopened, timeout.Token);
        Assert.Equal(player.GetProperty("request").GetProperty("activationId").GetString(),
            afterReopen.GetProperty("request").GetProperty("activationId").GetString());
        using HttpResponseMessage unknownApi = await reopened.GetAsync("/api/unknown", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
    }

    [Fact]
    public async Task HttpConcurrentSubmissionsAndPriorRunActivationsRespectSingleWaiter() {
        await using var oldFactory = new WebApplicationFactory<Program>();
        using HttpClient oldClient = oldFactory.CreateClient();
        await using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        FreePlaySession oldSession = oldFactory.Services.GetRequiredService<FreePlaySession>();
        PlayerView old = await oldSession.WaitForViewAsync(view => view.Status == "waiting", timeout.Token);
        FreePlaySession session = factory.Services.GetRequiredService<FreePlaySession>();
        PlayerView initial = await session.WaitForViewAsync(view => view.Status == "waiting", timeout.Token);
        object program = new[] { new { kind = "move", passage = "ab" } };
        using HttpResponseMessage stale = await client.PostAsJsonAsync("/api/player/programs",
            new { activationId = old.Request!.ActivationId, program, workspace = "" }, timeout.Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpStatusCode>[] submissions = Enumerable.Range(0, 8).Select(async _ => {
            await start.Task;
            using HttpResponseMessage response = await client.PostAsJsonAsync("/api/player/programs",
                new { activationId = initial.Request!.ActivationId, program, workspace = "" }, timeout.Token);
            return response.StatusCode;
        }).ToArray();
        start.SetResult();
        HttpStatusCode[] results = await Task.WhenAll(submissions).WaitAsync(timeout.Token);
        Assert.Single(results, code => code == HttpStatusCode.Accepted);
        Assert.Equal(7, results.Count(code => code == HttpStatusCode.Conflict));
        await session.WaitForViewAsync(view => view.Status == "waiting" && view.Location == "B", timeout.Token);
        Assert.Equal(3, session.GetDevView().TransitionCount);
        Assert.Equal(0, oldSession.GetDevView().TransitionCount);
    }

    [Fact]
    public async Task FaultIsDiagnosableAndHttpRejectsFurtherSubmissions() {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.ConfigureServices(services => {
                services.RemoveAll<FreePlaySession>();
                services.AddSingleton(new FreePlaySession(new FaultProgrammer()));
            });
        });
        using HttpClient client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        FreePlaySession session = factory.Services.GetRequiredService<FreePlaySession>();
        await session.WaitForViewAsync(view => view.Status == "faulted", timeout.Token);
        JsonElement player = await ReadViewAsync(client, timeout.Token);
        Assert.Equal("faulted", player.GetProperty("status").GetString());
        Assert.DoesNotContain("fault", AllPropertyNames(player));
        JsonElement dev = await client.GetFromJsonAsync<JsonElement>("/api/dev/view", timeout.Token);
        Assert.Contains("HTTP fault probe", dev.GetProperty("fault").GetString());
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/player/programs",
            new { activationId = "old", program = Array.Empty<object>(), workspace = "" }, timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RootRedirectsAndStaticAssetsFollowOutputWwwroot() {
        await using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/player", root.Headers.Location?.ToString());
        string entry = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        bool assetsPresent = File.Exists(entry) && new FileInfo(entry).Length > 0;
        using HttpResponseMessage page = await client.GetAsync("/player");
        Assert.Equal(assetsPresent ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, page.StatusCode);
        using HttpResponseMessage asset = await client.GetAsync("/index.html");
        Assert.Equal(assetsPresent ? HttpStatusCode.OK : HttpStatusCode.NotFound, asset.StatusCode);
        if (assetsPresent) {
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        }
    }

    private sealed class FaultProgrammer : IDynamicProgrammer {
        public ValueTask<ProgrammingResponse> ProgramAsync(ProgrammingRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<ProgrammingResponse>(new InvalidOperationException("HTTP fault probe"));
    }

    private static async Task<JsonElement> ReadViewAsync(HttpClient client, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<JsonElement>("/api/player/view", cancellationToken);

    private static IEnumerable<string> AllPropertyNames(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            foreach (JsonProperty property in element.EnumerateObject()) {
                yield return property.Name;
                foreach (string nested in AllPropertyNames(property.Value)) { yield return nested; }
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in element.EnumerateArray()) {
                foreach (string nested in AllPropertyNames(item)) { yield return nested; }
            }
        }
    }
}
