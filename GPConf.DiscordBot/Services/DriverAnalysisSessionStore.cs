using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// Which analysis wizard a session belongs to — determines the dropdown sequence and the facts
/// builder used on Generate.
/// </summary>
public enum AnalysisKind
{
    Driver,
    Player,
    Season,
    Recap,
    Team,
    SeasonStats,
    Leaderboard,
    Compare,
    H2H,
    Projected,
    Standings,
    Results,
    Quali,
    Practice,
    Pick,
    Rules,
}

/// <summary>
/// In-progress state for an analysis dropdown wizard, keyed by an opaque token embedded in each
/// select menu / button's CustomId — mirrors PickSessionStore's shape and TTL. A token (not the raw
/// season/driver/league GUIDs) is what travels in every CustomId because three hex-encoded GUIDs
/// plus a command prefix would blow past Discord's 100-character CustomId limit. The optional
/// selection fields start null and fill in as the user picks; SeasonId is always known (defaulted
/// to the newest season when the wizard starts). CallerId binds the session to whoever ran the
/// command — every handler rejects interactions from any other user id.
/// </summary>
public sealed record AnalysisSession(
    AnalysisKind Kind,
    ByteString SeasonId,
    ByteString? DriverId,
    ByteString? LeagueId,
    string? PlayerName,
    ByteString? RaceId,
    ByteString? TeamId,
    ByteString? Driver2Id,
    string? Player2Name,
    ByteString? Team2Id,
    int? SessionNumber,
    ulong CallerId,
    DateTime CreatedAt);

public sealed class AnalysisSessionStore
{
    // Matches Discord's own interaction follow-up token validity window, same rationale as
    // PickSessionStore.Ttl.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, AnalysisSession> _sessions = new();

    public string Start(AnalysisKind kind, ByteString seasonId, ulong callerId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _sessions[token] = new AnalysisSession(kind, seasonId, null, null, null, null, null, null, null, null, null, callerId, DateTime.UtcNow);
        return token;
    }

    public AnalysisSession? Get(string token)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (DateTime.UtcNow - session.CreatedAt > Ttl) { _sessions.TryRemove(token, out _); return null; }
        return session;
    }

    public void Update(string token, AnalysisSession session) => _sessions[token] = session;

    public void Remove(string token) => _sessions.TryRemove(token, out _);
}
