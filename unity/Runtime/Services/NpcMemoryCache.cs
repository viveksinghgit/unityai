using System;
using System.Collections.Generic;
using NpcSoulEngine.Runtime.Models;

namespace NpcSoulEngine.Runtime.Services
{
    /// <summary>
    /// LRU cache for NpcMemoryState. Reduces Cosmos reads for NPCs seen
    /// multiple times per session. Dirty entries are flushed on scene exit.
    /// Thread-safe: all accesses must be on the Unity main thread.
    /// </summary>
    public sealed class NpcMemoryCache
    {
        private readonly int _capacity;

        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lru;

        public NpcMemoryCache(int capacity = 50)
        {
            _capacity = capacity;
            _map = new Dictionary<string, LinkedListNode<CacheEntry>>(capacity);
            _lru = new LinkedList<CacheEntry>();
        }

        public int Count => _map.Count;

        public bool TryGet(string npcId, string playerId, out NpcMemoryState state)
        {
            var key = MakeKey(npcId, playerId);
            if (_map.TryGetValue(key, out var node))
            {
                // Move to front (most recently used)
                _lru.Remove(node);
                _lru.AddFirst(node);
                state = node.Value.State;
                return true;
            }
            state = null;
            return false;
        }

        public void Put(string npcId, string playerId, NpcMemoryState state, bool dirty = false)
        {
            var key = MakeKey(npcId, playerId);
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity)
            {
                EvictLru();
            }

            var entry = new CacheEntry(npcId, playerId, state, dirty);
            var node  = _lru.AddFirst(entry);
            _map[key] = node;
        }

        public void MarkDirty(string npcId, string playerId)
        {
            var key = MakeKey(npcId, playerId);
            if (_map.TryGetValue(key, out var node))
                node.Value.IsDirty = true;
        }

        public IEnumerable<CacheEntry> GetDirtyEntries()
        {
            foreach (var node in _lru)
            {
                if (node.IsDirty) yield return node;
            }
        }

        public void ClearDirtyFlag(string npcId, string playerId)
        {
            var key = MakeKey(npcId, playerId);
            if (_map.TryGetValue(key, out var node))
                node.Value.IsDirty = false;
        }

        private void EvictLru()
        {
            var tail = _lru.Last;
            if (tail is null) return;
            _map.Remove(MakeKey(tail.Value.NpcId, tail.Value.PlayerId));
            _lru.RemoveLast();
        }

        private static string MakeKey(string npcId, string playerId) => $"{npcId}|{playerId}";

        public sealed class CacheEntry
        {
            public readonly string NpcId;
            public readonly string PlayerId;
            public NpcMemoryState State;
            public bool IsDirty;
            public readonly DateTime CachedAt;

            public CacheEntry(string npcId, string playerId, NpcMemoryState state, bool dirty)
            {
                NpcId    = npcId;
                PlayerId = playerId;
                State    = state;
                IsDirty  = dirty;
                CachedAt = DateTime.UtcNow;
            }
        }
    }
}
