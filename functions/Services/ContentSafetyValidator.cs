using Azure;
using Azure.AI.ContentSafety;
using Microsoft.Extensions.Logging;

namespace NpcSoulEngine.Functions.Services;

public interface IContentSafetyValidator
{
    Task<ContentSafetyResult> ValidateAsync(string text, CancellationToken ct = default);
    bool IsConfigured { get; }
}

public sealed record ContentSafetyResult(bool IsSafe, string? ViolationCategory, int MaxSeverity)
{
    public static readonly ContentSafetyResult PassThrough = new(true, null, 0);
}

public sealed class ContentSafetyValidator : IContentSafetyValidator
{
    private readonly ContentSafetyClient? _client;
    private readonly ILogger<ContentSafetyValidator> _log;

    // Severity scale: 0=safe, 2=low, 4=medium, 6=high. Block at medium+.
    private const int SeverityThreshold = 4;

    public bool IsConfigured => _client is not null;

    public ContentSafetyValidator(ILogger<ContentSafetyValidator> log,
                                   string? endpoint, string? key)
    {
        _log = log;
        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key))
        {
            _client = new ContentSafetyClient(new Uri(endpoint), new AzureKeyCredential(key));
            _log.LogInformation("Content Safety validator configured at {Endpoint}", endpoint);
        }
        else
        {
            _log.LogWarning("ContentSafetyEndpoint/Key not configured — content validation disabled");
        }
    }

    public async Task<ContentSafetyResult> ValidateAsync(string text, CancellationToken ct = default)
    {
        if (_client is null) return ContentSafetyResult.PassThrough;

        try
        {
            var request = new AnalyzeTextOptions(text);
            var response = await _client.AnalyzeTextAsync(request, ct);

            var maxSeverity = 0;
            string? violationCategory = null;

            foreach (var category in response.Value.CategoriesAnalysis)
            {
                var severity = category.Severity ?? 0;
                if (severity > maxSeverity)
                {
                    maxSeverity = severity;
                    violationCategory = category.Category.ToString();
                }
            }

            if (maxSeverity >= SeverityThreshold)
            {
                _log.LogWarning(
                    "Content Safety flagged NPC response: category={Category} severity={Severity}",
                    violationCategory, maxSeverity);
                return new ContentSafetyResult(false, violationCategory, maxSeverity);
            }

            return new ContentSafetyResult(true, null, maxSeverity);
        }
        catch (Exception ex)
        {
            // Fail open — never block dialogue because Content Safety is unavailable
            _log.LogError(ex, "Content Safety validation failed, failing open");
            return ContentSafetyResult.PassThrough;
        }
    }
}
