using System.Collections.Generic;
using NpcSoulEngine.Runtime.BehaviorTree;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;
using UnityEngine.Events;

namespace NpcSoulEngine.Runtime.Integration
{
    /// <summary>
    /// Bridge between your dialogue tree system and the NPC Soul Engine.
    ///
    /// Handles two distinct flows:
    ///   A) Traditional dialogue trees: the player picks from a list of options.
    ///      Call NotifyDialogueChoice() after each choice to record the action.
    ///
    ///   B) Generative dialogue (GPT-4o): the player types or speaks freely.
    ///      Call BeginGenerativeDialogue() — this drives InitiateDialogueAction
    ///      and returns the NPC's response via callback.
    ///
    /// In both flows, conversation history is tracked on NpcBlackboard so that
    /// each exchange is contextually coherent within the session.
    /// </summary>
    public sealed class DialogueChoiceHook : MonoBehaviour
    {
        [SerializeField] private NpcSoulComponent npcSoulComponent;
        [SerializeField] private NpcBlackboard blackboard;

        [Header("UI Events")]
        public UnityEvent<string> onNpcResponseReady;
        public UnityEvent<string> onTokenStreamed;
        public UnityEvent         onDialogueFailed;

        private void Awake()
        {
            npcSoulComponent ??= GetComponentInParent<NpcSoulComponent>();
            blackboard       ??= GetComponentInParent<NpcBlackboard>();
        }

        // ─── Flow A: Dialogue-tree choices ────────────────────────────────────

        /// <summary>
        /// Call when the player selects a dialogue option with a moral/trust implication.
        /// Not every choice needs recording — only ones with emotional weight.
        /// </summary>
        public void NotifyDialogueChoice(
            DialogueChoiceType choiceType,
            string playerText,
            string zone = "zone_default")
        {
            var (actionType, stakes, summary) = MapChoiceToAction(choiceType, playerText,
                npcSoulComponent?.NpcId ?? "unknown");

            SoulEventBus.Record(
                npcSoulComponent?.NpcId ?? "unknown",
                actionType,
                new ActionContext
                {
                    summary    = summary,
                    stakes     = stakes,
                    publicness = 0.1f,
                    sceneName  = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                },
                zone: zone);

            blackboard?.PushConversationTurn("player", playerText);
        }

        // ─── Flow B: Generative dialogue ──────────────────────────────────────

        /// <summary>
        /// Begins a GPT-4o dialogue turn. Drives the NPC's InitiateDialogueAction.
        /// Response arrives via onNpcResponseReady (or token-by-token via onTokenStreamed).
        /// </summary>
        public void BeginGenerativeDialogue(string playerUtterance)
        {
            if (npcSoulComponent == null)
            {
                Debug.LogWarning("[DialogueChoiceHook] No NpcSoulComponent found");
                return;
            }

            blackboard?.PushConversationTurn("player", playerUtterance);

            npcSoulComponent.StartDialogue(
                playerUtterance,
                onResponse: response =>
                {
                    if (response == null) { onDialogueFailed?.Invoke(); return; }
                    blackboard?.PushConversationTurn("npc", response.text);
                    blackboard.LastDialogueResponse = response;
                    onNpcResponseReady?.Invoke(response.text);
                },
                onToken: token => onTokenStreamed?.Invoke(token));
        }

        public void EndDialogue()
        {
            blackboard?.ClearConversation();
        }

        // ─── Choice type mapping ──────────────────────────────────────────────

        private static (ActionType action, string stakes, string summary)
            MapChoiceToAction(DialogueChoiceType choice, string playerText, string npcId)
        {
            return choice switch
            {
                DialogueChoiceType.Threatening =>
                    (ActionType.Threat, "Medium", $"Player threatened {npcId}: \"{playerText}\""),
                DialogueChoiceType.Deceptive =>
                    (ActionType.TradeDeceptive, "Medium", $"Player deceived {npcId}: \"{playerText}\""),
                DialogueChoiceType.Kind =>
                    (ActionType.Kindness, "Low", $"Player was kind to {npcId}: \"{playerText}\""),
                DialogueChoiceType.Insulting =>
                    (ActionType.Insult, "Low", $"Player insulted {npcId}: \"{playerText}\""),
                DialogueChoiceType.PromiseMade =>
                    (ActionType.PromiseKept, "Medium", $"Player made promise to {npcId}: \"{playerText}\""),
                DialogueChoiceType.HelpOffered =>
                    (ActionType.QuestHelped, "Medium", $"Player offered to help {npcId}: \"{playerText}\""),
                DialogueChoiceType.Compliment =>
                    (ActionType.Compliment, "Low", $"Player complimented {npcId}: \"{playerText}\""),
                _ =>
                    (ActionType.NeutralInteraction, "Low", $"Player spoke to {npcId}")
            };
        }
    }

    public enum DialogueChoiceType
    {
        Neutral,
        Kind,
        Threatening,
        Deceptive,
        Insulting,
        PromiseMade,
        HelpOffered,
        Compliment,
        Accusation,
        Bribe
    }
}
