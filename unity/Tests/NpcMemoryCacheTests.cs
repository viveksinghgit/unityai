using NUnit.Framework;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using System.Linq;

namespace NpcSoulEngine.Tests
{
    [TestFixture]
    public sealed class NpcMemoryCacheTests
    {
        private NpcMemoryCache _cache;

        [SetUp] public void SetUp() => _cache = new NpcMemoryCache(capacity: 3);

        private static NpcMemoryState MakeState(string npcId, float trust = 50f) =>
            new() { npcId = npcId, playerId = "p1", trustScore = trust,
                currentEmotion = EmotionVector.Neutral(), behaviorOverrides = new BehaviorOverrides() };

        // ─── Basic put/get ────────────────────────────────────────────────────

        [Test]
        public void PutAndGet_ReturnsSameState()
        {
            var s = MakeState("npc_a", 75f);
            _cache.Put("npc_a", "p1", s);
            Assert.IsTrue(_cache.TryGet("npc_a", "p1", out var result));
            Assert.AreEqual(75f, result.trustScore);
        }

        [Test]
        public void Get_MissingKey_ReturnsFalse()
        {
            Assert.IsFalse(_cache.TryGet("npc_ghost", "p1", out _));
        }

        // ─── LRU eviction ─────────────────────────────────────────────────────

        [Test]
        public void LruEviction_DropsLeastRecentlyUsed()
        {
            _cache.Put("a", "p1", MakeState("a"));
            _cache.Put("b", "p1", MakeState("b"));
            _cache.Put("c", "p1", MakeState("c"));
            // Access "a" to make it most recent
            _cache.TryGet("a", "p1", out _);
            // Insert "d" — should evict "b" (least recently used)
            _cache.Put("d", "p1", MakeState("d"));
            Assert.IsFalse(_cache.TryGet("b", "p1", out _), "b should have been evicted");
            Assert.IsTrue(_cache.TryGet("a", "p1", out _), "a should still be present");
            Assert.IsTrue(_cache.TryGet("d", "p1", out _), "d should be present");
        }

        [Test]
        public void LruEviction_CapacityOneAlwaysEvicts()
        {
            var tiny = new NpcMemoryCache(capacity: 1);
            tiny.Put("a", "p1", MakeState("a"));
            tiny.Put("b", "p1", MakeState("b"));
            Assert.IsFalse(tiny.TryGet("a", "p1", out _));
            Assert.IsTrue(tiny.TryGet("b", "p1", out _));
        }

        // ─── Dirty flags ──────────────────────────────────────────────────────

        [Test]
        public void DirtyFlag_SetOnPutDirty_ClearedAfterClear()
        {
            _cache.Put("a", "p1", MakeState("a"), dirty: true);
            var dirty = _cache.GetDirtyEntries().ToList();
            Assert.AreEqual(1, dirty.Count);
            _cache.ClearDirtyFlag("a", "p1");
            Assert.AreEqual(0, _cache.GetDirtyEntries().Count());
        }

        [Test]
        public void MarkDirty_MakesCleanEntryDirty()
        {
            _cache.Put("a", "p1", MakeState("a"), dirty: false);
            Assert.AreEqual(0, _cache.GetDirtyEntries().Count());
            _cache.MarkDirty("a", "p1");
            Assert.AreEqual(1, _cache.GetDirtyEntries().Count());
        }

        [Test]
        public void PutClean_OverwritesDirtyEntry()
        {
            _cache.Put("a", "p1", MakeState("a"), dirty: true);
            _cache.Put("a", "p1", MakeState("a", 80f), dirty: false);
            Assert.AreEqual(0, _cache.GetDirtyEntries().Count());
            _cache.TryGet("a", "p1", out var s);
            Assert.AreEqual(80f, s.trustScore);
        }

        // ─── Overwrite existing key ───────────────────────────────────────────

        [Test]
        public void PutTwice_OverwritesValue()
        {
            _cache.Put("a", "p1", MakeState("a", 50f));
            _cache.Put("a", "p1", MakeState("a", 90f));
            _cache.TryGet("a", "p1", out var s);
            Assert.AreEqual(90f, s.trustScore);
            Assert.AreEqual(1, _cache.Count, "Should not duplicate entries");
        }
    }
}
