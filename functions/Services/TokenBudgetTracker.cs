using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace NpcSoulEngine.Functions.Services;

public interface ITokenBudgetTracker
{
    TokenBudgetResult Measure(IReadOnlyList<ChatMessage> messages, int maxOutputTokens);
}

public sealed record TokenBudgetResult(
    int EstimatedInputTokens,
    int MaxOutputTokens,
    bool ExceedsBudget,
    int HeadRoom);

public sealed class TokenBudgetTracker : ITokenBudgetTracker
{
    private readonly ILogger<TokenBudgetTracker> _log;

    // ~4 chars/token for English prose (GPT-4 tokenizer averages 3.5–4.5)
    private const float CharsPerToken = 4.0f;
    // Per-message overhead: role label + separator tokens
    private const int MessageOverheadTokens = 5;
    // gpt-4o / gpt-4o-mini both support 128K context
    private const int ContextWindowTokens = 128_000;
    // Warn when fewer than this many tokens remain after prompt + reserved output
    private const int LowHeadRoomThreshold = 2_000;

    public TokenBudgetTracker(ILogger<TokenBudgetTracker> log) => _log = log;

    public TokenBudgetResult Measure(IReadOnlyList<ChatMessage> messages, int maxOutputTokens)
    {
        var inputTokens = messages.Sum(EstimateTokens);
        var totalRequired = inputTokens + maxOutputTokens;
        var headRoom = ContextWindowTokens - totalRequired;
        var exceedsBudget = headRoom < 0;

        if (exceedsBudget)
        {
            _log.LogWarning(
                "Token budget exceeded: input={Input} output={Output} total={Total} limit={Limit}",
                inputTokens, maxOutputTokens, totalRequired, ContextWindowTokens);
        }
        else if (headRoom < LowHeadRoomThreshold)
        {
            _log.LogWarning("Token budget headroom low: {HeadRoom} tokens remaining", headRoom);
        }

        return new TokenBudgetResult(inputTokens, maxOutputTokens, exceedsBudget, headRoom);
    }

    private static int EstimateTokens(ChatMessage message)
    {
        try
        {
            var charCount = 0;
            foreach (var part in message.Content)
            {
                if (part.Text is { } text)
                    charCount += text.Length;
            }
            return (int)MathF.Ceiling(charCount / CharsPerToken) + MessageOverheadTokens;
        }
        catch
        {
            return 100; // safe fallback for unreadable message types
        }
    }
}
