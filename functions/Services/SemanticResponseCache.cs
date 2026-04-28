using System.Collections.Concurrent;
using NpcSoulEngine.Functions.Models;

namespace NpcSoulEngine.Functions.Services;

public interface ISemanticResponseCache
{
    bool TryGet(string npcId, string playerId, NpcMemoryDocument memory,
                string utterance, out DialogueResponse? response);
    void Set(string npcId, string playerId, NpcMemoryDocument memory,
             string utterance, DialogueResponse response);
    (int Total, int Expired) Stats();
}

/// <summary>
/// In-memory cache keyed by (npc, player, emotional-state-bucket, utterance-stem).
/// Bucketing trust/fear/hostility to the nearest 5 points means NPCs with similar
/// emotional states share cache entries, targeting a 20–30% hit rate.
/// TTL of 5 minutes prevents serving stale responses after memory updates.
/// </summary>
public sealed class SemanticResponseCache : ISemanticResponseCache
{
    private sealed record Entry(DialogueResponse Response, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public SemanticResponseCache(TimeSpan? ttl = null, int maxEntries = 200)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _maxEntries = maxEntries;
    }

    public bool TryGet(string npcId, string playerId, NpcMemoryDocument memory,
                       string utterance, out DialogueResponse? response)
    {
        var key = BuildKey(npcId, playerId, memory, utterance);
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            response = entry.Response;
            return true;
        }
        response = null;
        return false;
    }

    public void Set(string npcId, string playerId, NpcMemoryDocument memory,
                    string utterance, DialogueResponse response)
    {
        if (_cache.Count >= _maxEntries)
            Evict();

        var key = BuildKey(npcId, playerId, memory, utterance);
        _cache[key] = new Entry(response, DateTime.UtcNow + _ttl);
    }

    public (int Total, int Expired) Stats()
    {
        var now = DateTime.UtcNow;
        var expired = _cache.Values.Count(e => e.ExpiresAt <= now);
        return (_cache.Count, expired);
    }

    private static string BuildKey(string npcId, string playerId,
                                    NpcMemoryDocument memory, string utterance)
    {
        // Bucket scores to nearest 5 to widen cache hits across similar emotional states
        var t = (int)(memory.TrustScore     / 5) * 5;
        var f = (int)(memory.FearScore      / 5) * 5;
        var h = (int)(memory.HostilityScore / 5) * 5;
        var o = BehaviorBitmask(memory.BehaviorOverrides);
        var stem = UtteranceStem(utterance);
        return $"{npcId}:{playerId}:t{t}f{f}h{h}o{o}:{stem}";
    }

    private static int BehaviorBitmask(BehaviorOverrides ov) =>
        (ov.RefuseTrade  ? 1 : 0)
      | (ov.AlertGuards  ? 2 : 0)
      | (ov.GiveDiscount ? 4 : 0)
      | (ov.AvoidPlayer  ? 8 : 0)
      | (ov.SeekHelp     ? 16 : 0);

    private static string UtteranceStem(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return string.Empty;
        var sb = new System.Text.StringBuilder(64);
        foreach (var ch in utterance.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ')
                sb.Append(ch);
            if (sb.Length == 60) break;
        }
        return sb.ToString().Trim();
    }

    private void Evict()
    {
        var now = DateTime.UtcNow;
        // Remove expired entries first
        foreach (var key in _cache.Where(kv => kv.Value.ExpiresAt <= now)
                                   .Select(kv => kv.Key).ToList())
            _cache.TryRemove(key, out _);

        // Still over limit? Remove 10 oldest
        if (_cache.Count >= _maxEntries)
        {
            foreach (var key in _cache.OrderBy(kv => kv.Value.ExpiresAt)
                                       .Take(10).Select(kv => kv.Key).ToList())
                _cache.TryRemove(key, out _);
        }
    }
}
