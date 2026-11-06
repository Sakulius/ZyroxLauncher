using DiscordRPC;

namespace Zyrox.Services;

public sealed class DiscordService : IDisposable
{
    private const string ApplicationId = "1567630663249563821";

    private DiscordRpcClient? _client;

    public void Configure(bool enabled)
    {
        if (enabled == (_client != null))
            return;

        _client?.Dispose();
        _client = null;
        if (!enabled)
            return;

        _client = new DiscordRpcClient(ApplicationId);
        _client.Initialize();
    }

    public void ShowPlaying(string details, string state)
    {
        _client?.SetPresence(new RichPresence
        {
            Details = details,
            State = state,
            Timestamps = Timestamps.Now
        });
    }

    public void Clear() => _client?.ClearPresence();

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
