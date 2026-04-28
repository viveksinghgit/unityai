using NUnit.Framework;
using NpcSoulEngine.Runtime.Services;

namespace NpcSoulEngine.Tests
{
    [TestFixture]
    public sealed class CircuitBreakerTests
    {
        // ─── Closed state ─────────────────────────────────────────────────────

        [Test]
        public void InitialState_IsClosed()
        {
            var cb = new CircuitBreaker(threshold: 3, resetSeconds: 30f);
            Assert.AreEqual(CircuitState.Closed, cb.State);
            Assert.IsFalse(cb.IsOpen);
        }

        [Test]
        public void BelowThreshold_StaysClosed()
        {
            var cb = new CircuitBreaker(threshold: 3, resetSeconds: 30f);
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        // ─── Opens at threshold ───────────────────────────────────────────────

        [Test]
        public void AtThreshold_Opens()
        {
            var cb = new CircuitBreaker(threshold: 3, resetSeconds: 30f);
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
            Assert.IsTrue(cb.IsOpen);
        }

        // ─── Reset on success ─────────────────────────────────────────────────

        [Test]
        public void RecordSuccess_ResetsClosed()
        {
            var cb = new CircuitBreaker(threshold: 2, resetSeconds: 30f);
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
            cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, cb.State);
            Assert.IsFalse(cb.IsOpen);
            Assert.AreEqual(0, cb.FailureCount);
        }

        // ─── Failure count ────────────────────────────────────────────────────

        [Test]
        public void FailureCount_Increments()
        {
            var cb = new CircuitBreaker(threshold: 10, resetSeconds: 30f);
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(2, cb.FailureCount);
        }

        [Test]
        public void FailureCount_ResetsOnSuccess()
        {
            var cb = new CircuitBreaker(threshold: 10, resetSeconds: 30f);
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordSuccess();
            Assert.AreEqual(0, cb.FailureCount);
        }
    }
}
