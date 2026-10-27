using DramaBoard.Server.FreePlay;

namespace DramaBoard.Server.Web;

internal sealed class SessionService(FreePlaySession session) : BackgroundService {
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => session.RunAsync(stoppingToken);
}
