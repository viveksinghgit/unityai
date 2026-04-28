using System;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Services
{
    public enum CircuitState { Closed, Open, HalfOpen }

    public sealed class CircuitBreaker
    {
        private CircuitState _state = CircuitState.Closed;
        private int _failureCount;
        private float _openedAt;

        private readonly int _failureThreshold;
        private readonly float _resetSeconds;

        public CircuitState State => _state;
        public bool IsOpen => _state == CircuitState.Open && !ShouldAttemptProbe();
        public int FailureCount => _failureCount;

        public CircuitBreaker(int failureThreshold = 10, float resetSeconds = 30f)
        {
            _failureThreshold = failureThreshold;
            _resetSeconds     = resetSeconds;
        }

        public void RecordSuccess()
        {
            _failureCount = 0;
            _state = CircuitState.Closed;
        }

        public void RecordFailure()
        {
            _failureCount++;
            if (_failureCount >= _failureThreshold && _state == CircuitState.Closed)
            {
                _state    = CircuitState.Open;
                _openedAt = Time.realtimeSinceStartup;
                Debug.LogWarning("[NpcSoulEngine] Circuit opened after consecutive Azure failures");
            }
        }

        private bool ShouldAttemptProbe()
        {
            if (_state != CircuitState.Open) return false;
            if (Time.realtimeSinceStartup - _openedAt >= _resetSeconds)
            {
                _state = CircuitState.HalfOpen;
                return true;
            }
            return false;
        }
    }
}
