using NUnit.Framework;
using NpcSoulEngine.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;

namespace NpcSoulEngine.Functions.Tests;

[TestFixture]
public sealed class TokenBudgetTests
{
    private TokenBudgetTracker _tracker = null!;

    [SetUp] public void SetUp() =>
        _tracker = new TokenBudgetTracker(NullLogger<TokenBudgetTracker>.Instance);

    // ─── Basic measurements ───────────────────────────────────────────────────

    [Test]
    public void EmptyMessages_ReturnsZeroInput()
    {
        var result = _tracker.Measure(Array.Empty<ChatMessage>(), 400);
        Assert.AreEqual(0, result.EstimatedInputTokens);
        Assert.IsFalse(result.ExceedsBudget);
    }

    [Test]
    public void ShortMessages_DoNotExceedBudget()
    {
        var messages = new ChatMessage[]
        {
            ChatMessage.CreateSystemMessage("You are an NPC."),
            ChatMessage.CreateUserMessage("Hello there.")
        };
        var result = _tracker.Measure(messages, 400);
        Assert.IsFalse(result.ExceedsBudget);
        Assert.Greater(result.HeadRoom, 0);
    }

    [Test]
    public void LargeSystemPrompt_EstimatesTokensProperly()
    {
        // ~2000 chars ÷ 4 chars/token ≈ 500 tokens + overhead
        var bigPrompt = new string('x', 2000);
        var messages = new ChatMessage[] { ChatMessage.CreateSystemMessage(bigPrompt) };
        var result = _tracker.Measure(messages, 400);
        Assert.Greater(result.EstimatedInputTokens, 400);
        Assert.Less(result.EstimatedInputTokens, 800);
    }

    [Test]
    public void ExceedsBudget_WhenPromptTooLarge()
    {
        // Fill context window: 128K tokens * 4 chars/token = 512K chars
        var massive = new string('x', 520_000);
        var messages = new ChatMessage[] { ChatMessage.CreateSystemMessage(massive) };
        var result = _tracker.Measure(messages, 400);
        Assert.IsTrue(result.ExceedsBudget);
        Assert.Less(result.HeadRoom, 0);
    }

    // ─── HeadRoom calculation ─────────────────────────────────────────────────

    [Test]
    public void HeadRoom_IsContextWindow_Minus_InputPlusOutput()
    {
        var messages = new ChatMessage[] { ChatMessage.CreateSystemMessage("Hi.") };
        var result = _tracker.Measure(messages, 400);
        var expected = 128_000 - result.EstimatedInputTokens - 400;
        Assert.AreEqual(expected, result.HeadRoom);
    }

    // ─── MaxOutputTokens preserved ───────────────────────────────────────────

    [Test]
    public void MaxOutputTokens_Preserved_InResult()
    {
        var messages = new ChatMessage[] { ChatMessage.CreateUserMessage("Hi.") };
        var result = _tracker.Measure(messages, 999);
        Assert.AreEqual(999, result.MaxOutputTokens);
    }
}
