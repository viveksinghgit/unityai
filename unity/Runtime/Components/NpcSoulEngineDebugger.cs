using System.Text;
using NpcSoulEngine.Runtime.BehaviorTree;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Runtime debug overlay. Toggle with Backquote (`) key.
    /// Shows per-NPC memory state as world-space labels and a screen HUD
    /// listing all registered NPCs with their trust/emotion data.
    ///
    /// Only active in Editor and Development builds — stripped from release.
    /// Add to the same GameObject as NpcSoulEngineManager.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class NpcSoulEngineDebugger : MonoBehaviour
    {
        [SerializeField] private KeyCode toggleKey = KeyCode.BackQuote;
        [SerializeField] private bool showWorldLabels = true;
        [SerializeField] private bool showHud = true;
        [SerializeField] private float labelUpdateInterval = 0.5f;

        private bool _visible;
        private GUIStyle _labelStyle;
        private GUIStyle _hudStyle;
        private GUIStyle _headerStyle;
        private float _lastLabelUpdate;
        private readonly StringBuilder _sb = new(1024);

        private Camera _cam;

        private void Awake()
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            enabled = false;  // zero cost in release builds
            return;
#endif
            _cam = Camera.main;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            EnsureStyles();

            var manager = NpcSoulEngineManager.Instance;
            if (manager == null) return;

            if (showHud) DrawHud(manager);
            if (showWorldLabels && Time.realtimeSinceStartup - _lastLabelUpdate > labelUpdateInterval)
            {
                _lastLabelUpdate = Time.realtimeSinceStartup;
                DrawWorldLabels(manager);
            }
        }

        private void DrawHud(NpcSoulEngineManager manager)
        {
            var hudRect = new Rect(10, 10, 340, Screen.height - 20);
            GUI.Box(hudRect, GUIContent.none);

            _sb.Clear();
            _sb.AppendLine("<b>NPC SOUL ENGINE DEBUG</b>");

            var svc = AzureMemoryService.Instance;
            if (svc == null)
            {
                _sb.AppendLine("AzureMemoryService: NOT INITIALISED");
            }
            else
            {
                var breaker = GetCircuitBreakerState();
                _sb.AppendLine($"Circuit: <color={(breaker == "Closed" ? "lime" : "red")}>{breaker}</color>");
                _sb.AppendLine($"PlayerId: {PlayerIdentityService.PlayerId[..12]}...");
                _sb.AppendLine();
            }

            // Per-NPC entries
            var npcs = FindObjectsByType<NpcSoulComponent>(FindObjectsSortMode.None);
            _sb.AppendLine($"NPCs in scene: {npcs.Length}");
            _sb.AppendLine("─────────────────────────────");

            foreach (var npc in npcs)
            {
                if (!npc.MemoryLoaded)
                {
                    _sb.AppendLine($"<color=grey>{npc.NpcId}: loading...</color>");
                    continue;
                }

                var mem = npc.CurrentMemory;
                if (mem == null) continue;

                var trustColor = mem.trustScore switch
                {
                    >= 70 => "lime",
                    >= 40 => "yellow",
                    _     => "red"
                };

                _sb.AppendLine($"<b>{npc.NpcId}</b>");
                _sb.AppendLine($"  Trust: <color={trustColor}>{mem.trustScore:F0}</color>  " +
                               $"Fear: {mem.fearScore:F0}  Hostile: {mem.hostilityScore:F0}");
                _sb.AppendLine($"  Emotion: {mem.currentEmotion?.primary ?? "—"}  " +
                               $"({mem.currentEmotion?.intensity:F2})");
                _sb.AppendLine($"  Archetype: {mem.playerArchetype}  ({mem.archetypeConfidence:P0})");

                var ov = mem.behaviorOverrides;
                if (ov != null)
                {
                    var flags = new System.Collections.Generic.List<string>();
                    if (ov.refuseTrade)  flags.Add("<color=red>REFUSE_TRADE</color>");
                    if (ov.alertGuards)  flags.Add("<color=orange>ALERT_GUARDS</color>");
                    if (ov.avoidPlayer)  flags.Add("<color=yellow>AVOID</color>");
                    if (ov.giveDiscount) flags.Add("<color=lime>DISCOUNT</color>");
                    if (flags.Count > 0) _sb.AppendLine($"  [{string.Join(", ", flags)}]");
                }

                // Last salient event
                if (mem.salientEvents?.Count > 0)
                {
                    var last = mem.salientEvents[^1];
                    _sb.AppendLine($"  Last: [{last.actionType}] {TruncateStr(last.description, 35)}");
                }
                _sb.AppendLine();
            }

            GUI.Label(new Rect(hudRect.x + 8, hudRect.y + 8, hudRect.width - 16, hudRect.height - 16),
                _sb.ToString(), _hudStyle);
        }

        private void DrawWorldLabels(NpcSoulEngineManager manager)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            var npcs = FindObjectsByType<NpcSoulComponent>(FindObjectsSortMode.None);
            foreach (var npc in npcs)
            {
                if (!npc.MemoryLoaded || npc.CurrentMemory == null) continue;

                var screenPos = _cam.WorldToScreenPoint(npc.transform.position + Vector3.up * 2.2f);
                if (screenPos.z < 0) continue;  // behind camera

                var guiPos = new Rect(screenPos.x - 60, Screen.height - screenPos.y - 15, 120, 30);
                var trust = npc.CurrentMemory.trustScore;
                var color = trust >= 70 ? "lime" : trust >= 40 ? "yellow" : "red";
                GUI.Label(guiPos,
                    $"<color={color}>{trust:F0}</color> {npc.CurrentMemory.currentEmotion?.primary ?? ""}",
                    _labelStyle);
            }
        }

        private void EnsureStyles()
        {
            if (_hudStyle != null) return;

            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 11,
                richText  = true,
                wordWrap  = false,
                normal    = { textColor = Color.white }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 10,
                richText  = true,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white }
            };
        }

        private static string GetCircuitBreakerState()
        {
            var state = AzureMemoryService.Instance?.Breaker?.State;
            return state?.ToString() ?? "Unknown";
        }

        private static string TruncateStr(string s, int max) =>
            s?.Length > max ? s[..max] + "…" : s ?? "";
    }
}
