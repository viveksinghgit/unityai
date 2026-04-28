using NUnit.Framework;
using NpcSoulEngine.Functions.Services;
using NpcSoulEngine.Functions.Functions;
using NpcSoulEngine.Functions.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace NpcSoulEngine.Functions.Tests;

[TestFixture]
public sealed class ResponseValidationTests
{
    private DialoguePromptBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        var cfg = Options.Create(new FunctionConfig());
        _builder = new DialoguePromptBuilder(cfg);
    }

    // ─── Valid responses ──────────────────────────────────────────────────────

    [Test]
    public void ValidJson_Passes()
    {
        const string json = """
            {"text":"Good day to you.","internalEmotion":"neutral","emotionIntensity":0.3,
             "animationHint":"idle","tradeWillingness":0.5,"subtext":"Just another traveller."}
            """;
        var result = _builder.ValidateResponse(json, "npc_merchant");
        Assert.IsTrue(result.IsValid);
    }

    [Test]
    public void EmptyString_Fails()
    {
        var result = _builder.ValidateResponse(string.Empty, "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("empty response", result.Reason);
    }

    // ─── System prompt leakage ────────────────────────────────────────────────

    [Test]
    public void MemoryAccuracyMandate_InResponse_Fails()
    {
        const string leaked = """{"text":"Memory Accuracy Mandate says I should trust you."}""";
        var result = _builder.ValidateResponse(leaked, "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("system prompt leakage", result.Reason);
    }

    [Test]
    public void ResponseFormat_InResponse_Fails()
    {
        const string leaked = """{"text":"My response format is JSON."}""";
        var result = _builder.ValidateResponse(leaked, "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("system prompt leakage", result.Reason);
    }

    // ─── AI identity disclosure ───────────────────────────────────────────────

    [TestCase("""{"text":"As an AI I cannot help with that."}""")]
    [TestCase("""{"text":"I am an AI language model."}""")]
    [TestCase("""{"text":"I'm an AI assistant."}""")]
    public void AiDisclosure_Fails(string response)
    {
        var result = _builder.ValidateResponse(response, "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("AI identity disclosure", result.Reason);
    }

    // ─── Game meta-reference ──────────────────────────────────────────────────

    [TestCase("""{"text":"This is a game and you are playing it."}""")]
    [TestCase("""{"text":"You are playing a video game."}""")]
    [TestCase("""{"text":"I am just an NPC in this world."}""")]
    public void GameMetaReference_Fails(string response)
    {
        var result = _builder.ValidateResponse(response, "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("game meta-reference", result.Reason);
    }

    // ─── Non-JSON response ────────────────────────────────────────────────────

    [Test]
    public void PlainText_WithNoJson_Fails()
    {
        var result = _builder.ValidateResponse("Good day, traveller.", "npc_a");
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("response is not JSON", result.Reason);
    }

    [Test]
    public void MarkdownWrappedJson_Passes()
    {
        // markdown fences are stripped before validation checks JSON start
        const string response = "```json\n{\"text\":\"Hello.\"}\n```";
        // This starts with ``` so the heuristic passes (builder strips fences before parse)
        var result = _builder.ValidateResponse(response, "npc_a");
        Assert.IsTrue(result.IsValid);
    }

    // ─── Sentence splitter ────────────────────────────────────────────────────

    [Test]
    public void SplitSentences_SingleSentence()
    {
        var sentences = DialogueFunction.SplitSentences("Hello there.");
        Assert.AreEqual(1, sentences.Count);
        Assert.AreEqual("Hello there.", sentences[0]);
    }

    [Test]
    public void SplitSentences_MultipleSentences()
    {
        var sentences = DialogueFunction.SplitSentences("Hello there. How are you? I am fine!");
        Assert.AreEqual(3, sentences.Count);
        Assert.AreEqual("Hello there.", sentences[0]);
        Assert.AreEqual("How are you?", sentences[1]);
        Assert.AreEqual("I am fine!",   sentences[2]);
    }

    [Test]
    public void SplitSentences_TrailingTextWithNoPunctuation()
    {
        var sentences = DialogueFunction.SplitSentences("First sentence. Trailing fragment");
        Assert.AreEqual(2, sentences.Count);
        Assert.AreEqual("Trailing fragment", sentences[1]);
    }

    [Test]
    public void SplitSentences_EmptyString_ReturnsEmpty()
    {
        var sentences = DialogueFunction.SplitSentences(string.Empty);
        Assert.AreEqual(0, sentences.Count);
    }

    [Test]
    public void SplitSentences_SingleWordNoPunctuation()
    {
        var sentences = DialogueFunction.SplitSentences("Hmm");
        Assert.AreEqual(1, sentences.Count);
        Assert.AreEqual("Hmm", sentences[0]);
    }
}
