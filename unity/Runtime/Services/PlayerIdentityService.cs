using System;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Services
{
    /// <summary>
    /// Manages the player's persistent GUID across sessions.
    ///
    /// Priority order:
    ///   1. Injected programmatically (call SetPlayerId from your auth/save system)
    ///   2. PlayerPrefs (survives app restarts, same device)
    ///   3. Auto-generated GUID saved to PlayerPrefs
    ///
    /// In production, override with a platform-specific user ID (Steam, Xbox, etc.)
    /// by calling SetPlayerId() before NpcSoulEngineManager.Start() runs.
    /// </summary>
    public static class PlayerIdentityService
    {
        private const string PrefsKey = "NpcSoulEngine_PlayerId";

        private static string _cachedId;

        public static string PlayerId
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedId)) return _cachedId;
                _cachedId = LoadOrCreate();
                return _cachedId;
            }
        }

        /// <summary>
        /// Override with a platform account ID (Steam64, XboxUserId, etc.).
        /// Call before NpcSoulEngineManager initialises.
        /// </summary>
        public static void SetPlayerId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PlayerId cannot be empty");

            _cachedId = id;
            PlayerPrefs.SetString(PrefsKey, id);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Clear the stored ID (e.g. on sign-out in a shared-device scenario).
        /// </summary>
        public static void ClearPlayerId()
        {
            _cachedId = null;
            PlayerPrefs.DeleteKey(PrefsKey);
            PlayerPrefs.Save();
        }

        private static string LoadOrCreate()
        {
            var stored = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(stored)) return stored;

            var newId = $"player_{Guid.NewGuid():N}";
            PlayerPrefs.SetString(PrefsKey, newId);
            PlayerPrefs.Save();
            Debug.Log($"[NpcSoulEngine] Generated new PlayerId: {newId}");
            return newId;
        }
    }
}
