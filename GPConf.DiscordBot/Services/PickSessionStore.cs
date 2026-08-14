using System.Collections.Concurrent;
using System.Security.Cryptography;
using Discord;
using Google.Protobuf;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// In-progress state for a /pick-submit session, keyed by an opaque token embedded in each pick
/// slot's select-menu CustomId. Carries the caller's Discord display name, not a resolved
/// PlayerId — player resolution (including auto-registration for a brand-new player) is deferred
/// to finalize time (DataService.SubmitPicks), inside the same write as the pick save. Holds IDs
/// only, never a loaded MainData or protobuf object references — every render/finalize step
/// re-loads fresh data, so nothing here can go stale except the token map itself.
/// </summary>
// DropdownMessage is set once the dropdown message is sent, but only when it's a *separate*
// message from the lock-in button (a 5-pick league leaves no free row for the button — see
// PickCommands.CanFitButton). Stored as the actual IUserMessage (not just an id): every dropdown
// message is now created via FollowupAsync, so this reference carries its own edit route
// (the interaction token that created it) independent of whichever interaction is currently
// running — an ephemeral message can only be edited through the interaction that sent it, and
// PickLockIn fires as a *different* interaction (the button click) than the one that sent the
// dropdowns, so Context.Channel-style editing by id doesn't work for ephemeral messages. Null
// when the button shares the dropdown message — that case updates itself as part of its own click.
// CallerId binds the session to the Discord user who ran /pick-submit — PickSlotSelected and
// PickLockIn both reject interactions from any other user id. Ephemeral responses already make
// this session invisible to everyone else, but this is a server-side guarantee that doesn't
// depend on that (e.g. if pick-submit is ever used somewhere the response can't be ephemeral).
public sealed record PickSession(
    ByteString SeasonId, ByteString RaceId, ByteString LeagueId, string CallerDisplayName, ulong CallerId,
    ByteString[] EligibleDriverIds, ByteString?[] Selections, DateTime CreatedAt, IUserMessage? DropdownMessage = null);

public sealed class PickSessionStore
{
    // Matches Discord's own interaction follow-up token validity window, so an expired session
    // and an expired Discord token fail at roughly the same time.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, PickSession> _sessions = new();

    public string Start(ByteString seasonId, ByteString raceId, ByteString leagueId,
        string callerDisplayName, ulong callerId, ByteString[] eligibleDriverIds, int numPicks)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _sessions[token] = new PickSession(seasonId, raceId, leagueId, callerDisplayName, callerId,
            eligibleDriverIds, new ByteString?[numPicks], DateTime.UtcNow);
        return token;
    }

    public PickSession? Get(string token)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (DateTime.UtcNow - session.CreatedAt > Ttl) { _sessions.TryRemove(token, out _); return null; }
        return session;
    }

    public void Update(string token, PickSession session) => _sessions[token] = session;

    public void Remove(string token) => _sessions.TryRemove(token, out _);
}
