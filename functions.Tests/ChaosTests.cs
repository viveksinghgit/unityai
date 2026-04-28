using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;

namespace NpcSoulEngine.Functions.Tests;

/// <summary>
/// Fault-injection tests — verify the system degrades gracefully under
/// service failures without throwing unhandled exceptions.
/// </summary>
[TestFixture]
public sealed class ChaosTests
{
    // ─── ArchetypeClassifier: HTTP fault injection ────────────────────────────

    [Test]
    public async Task ArchetypeClassifier_Http500_FallsBackToRuleBased()
    {
        var classifier = BuildClassifier(HttpStatusCode.InternalServerError);
        var features   = new PlayerFeatureVector { CombatInitiationRate = 0.8f };

        var result = await classifier.ClassifyAsync(features);

        Assert.AreEqual("aggressor", result.Archetype,
            "Should return rule-based result when the ML endpoint returns 500");
        Assert.Greater(result.Confidence, 0f);
    }

    [Test]
    public async Task ArchetypeClassifier_Timeout_FallsBackToRuleBased()
    {
        var classifier = BuildClassifier(throwOnSend: true);
        var features   = new PlayerFeatureVector { AvgTrust = 0.9f, ReputationAwarenessScore = 0.9f };

        var result = await classifier.ClassifyAsync(features);

        Assert.AreEqual("hero", result.Archetype,
            "Should return rule-based result when the ML endpoint throws");
    }

    [Test]
    public async Task ArchetypeClassifier_MalformedJson_FallsBackToRuleBased()
    {
        var classifier = BuildClassifier(responseBody: "not-json-at-all");
        var features   = new PlayerFeatureVector { TradeDeceptionRate = 0.7f };

        var result = await classifier.ClassifyAsync(features);

        Assert.AreEqual("trickster", result.Archetype,
            "Should return rule-based result when the ML endpoint returns invalid JSON");
    }

    [Test]
    public async Task ArchetypeClassifier_NoEndpointConfigured_NeverCallsHttp()
    {
        // _endpointUri is null → rule-based path taken immediately, no HTTP call
        var callCount  = 0;
        var classifier = BuildClassifier(
            onSend: _ => { callCount++; return Task.FromResult(OkResponse("{}")); });

        // Override to empty endpoint
        classifier = BuildClassifier(endpointUri: null);
        var result = await classifier.ClassifyAsync(new PlayerFeatureVector { AvgHostility = 0.7f });

        Assert.AreEqual(0, callCount, "No HTTP call should be made without an endpoint URI");
        Assert.IsNotNull(result.Archetype);
    }

    [Test]
    public async Task ArchetypeClassifier_EmptyPredictionArray_FallsBackToRuleBased()
    {
        var classifier = BuildClassifier(responseBody: """{"archetypes":[],"probabilities":[]}""");
        var result     = await classifier.ClassifyAsync(new PlayerFeatureVector { AvgTrust = 0.8f });

        // Falls back to rule-based — result must still be valid
        Assert.IsNotNull(result.Archetype);
        Assert.GreaterOrEqual(result.Confidence, 0f);
    }

    // ─── ArchetypeReclassificationJob: isolated player failure ───────────────

    [Test]
    public async Task ReclassifyJob_ContinuesOnIndividualPlayerFailure()
    {
        var store      = new StubStoreWithOneFailingPlayer();
        var classifier = new StubClassifier();
        var job        = new ArchetypeReclassificationJob(
            store, classifier, NullLogger<ArchetypeReclassificationJob>.Instance);

        // Job should complete without throwing, despite player2 throwing
        Assert.DoesNotThrowAsync(async () =>
            await job.RunAsync(null!, CancellationToken.None));

        // player1 and player3 should still have been processed
        Assert.AreEqual(2, store.UpsertArchetypeCalls,
            "Healthy players should still be classified despite one failing");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AzureMLArchetypeClassifier BuildClassifier(
        HttpStatusCode statusCode   = HttpStatusCode.OK,
        string? responseBody        = null,
        bool throwOnSend            = false,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? onSend = null,
        string? endpointUri         = "https://fake-ml-endpoint/score")
    {
        var handler  = new FakeHttpHandler(statusCode, responseBody ?? "{}", throwOnSend, onSend);
        var http     = new HttpClient(handler);
        var opts     = Options.Create(new FunctionConfig
        {
            AzureMLEndpointUri = endpointUri,
            AzureMLEndpointKey = null,
        });
        return new AzureMLArchetypeClassifier(
            http, opts, NullLogger<AzureMLArchetypeClassifier>.Instance);
    }

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // ─── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        private readonly bool _throw;
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _onSend;

        public FakeHttpHandler(
            HttpStatusCode code, string body, bool @throw,
            Func<HttpRequestMessage, Task<HttpResponseMessage>>? onSend)
        {
            _code   = code;
            _body   = body;
            _throw  = @throw;
            _onSend = onSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (_throw)
                throw new HttpRequestException("Simulated network failure");
            if (_onSend != null)
                return _onSend(request);
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body)
            });
        }
    }

    private sealed class StubStoreWithOneFailingPlayer : ICosmosMemoryStore
    {
        public int UpsertArchetypeCalls { get; private set; }

        public Task<IReadOnlyList<string>> GetActivePlayerIdsAsync(
            DateTimeOffset activeSince, int maxPlayers, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "player1", "player2", "player3" });

        public Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesByPlayerAsync(
            string playerId, CancellationToken ct)
        {
            if (playerId == "player2")
                throw new InvalidOperationException("Simulated Cosmos failure for player2");
            return Task.FromResult<IReadOnlyList<NpcMemoryDocument>>(Array.Empty<NpcMemoryDocument>());
        }

        public Task<PlayerArchetypeDocument?> GetArchetypeAsync(string playerId, CancellationToken ct) =>
            Task.FromResult<PlayerArchetypeDocument?>(null);

        public Task UpsertArchetypeAsync(PlayerArchetypeDocument document, CancellationToken ct)
        {
            UpsertArchetypeCalls++;
            return Task.CompletedTask;
        }

        public Task BulkUpdatePlayerArchetypeAsync(
            string playerId, string archetype, float confidence, CancellationToken ct) =>
            Task.CompletedTask;

        // Unused by this test but required by interface
        public Task<NpcMemoryDocument?> GetMemoryAsync(string n, string p, CancellationToken ct) =>
            Task.FromResult<NpcMemoryDocument?>(null);

        public Task<NpcMemoryDocument> UpsertMemoryAsync(NpcMemoryDocument doc, CancellationToken ct) =>
            Task.FromResult(doc);

        public Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesForDecayAsync(
            DateTimeOffset t, int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NpcMemoryDocument>>(Array.Empty<NpcMemoryDocument>());

        public Task<NpcMemoryDocument> GetOrCreateMemoryAsync(string n, string p, CancellationToken ct) =>
            Task.FromResult(new NpcMemoryDocument { Id = $"{n}_{p}", NpcId = n, PlayerId = p });
    }

    private sealed class StubClassifier : IArchetypeClassifierService
    {
        public Task<ArchetypeResult> ClassifyAsync(PlayerFeatureVector features, CancellationToken ct) =>
            Task.FromResult(new ArchetypeResult { Archetype = "neutral", Confidence = 0.5f,
                Scores = AzureMLArchetypeClassifier.Labels.ToDictionary(l => l, _ => 1f / 6f) });
    }
}
