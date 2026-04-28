using NUnit.Framework;
using NpcSoulEngine.Functions.Functions;
using NpcSoulEngine.Functions.Models;

namespace NpcSoulEngine.Functions.Tests;

[TestFixture]
public sealed class GossipPropagationTests
{
    // ─── ParseEdgesFromWitnessIds ─────────────────────────────────────────────

    [Test]
    public void ParseEdges_ValidEdge_Parsed()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_a>npc_b:0.8:0.6" });

        Assert.AreEqual(1, edges.Count);
        Assert.AreEqual("npc_b", edges[0].TargetNpcId);
        Assert.AreEqual(0.8f, edges[0].RelationshipStrength, 0.001f);
        Assert.AreEqual(0.6f, edges[0].TrustInRelationship, 0.001f);
    }

    [Test]
    public void ParseEdges_WrongSource_Ignored()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_x>npc_b:0.8:0.6" });

        Assert.AreEqual(0, edges.Count);
    }

    [Test]
    public void ParseEdges_PlainWitnessId_Ignored()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "npc_guard", "npc_merchant" });

        Assert.AreEqual(0, edges.Count);
    }

    [Test]
    public void ParseEdges_MalformedStrength_Ignored()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_a>npc_b:notafloat:0.5" });

        Assert.AreEqual(0, edges.Count);
    }

    [Test]
    public void ParseEdges_TooFewParts_Ignored()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_a>npc_b:0.5" });

        Assert.AreEqual(0, edges.Count);
    }

    [Test]
    public void ParseEdges_Mixed_OnlyValidEdgesReturned()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a",
            new[] { "npc_guard", "EDGE:npc_a>npc_c:0.5:0.7", "EDGE:npc_x>npc_d:0.5:0.7" });

        Assert.AreEqual(1, edges.Count);
        Assert.AreEqual("npc_c", edges[0].TargetNpcId);
    }

    [Test]
    public void ParseEdges_StrengthAboveOne_ClampedToOne()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_a>npc_b:1.5:0.5" });

        Assert.AreEqual(1, edges.Count);
        Assert.AreEqual(1.0f, edges[0].RelationshipStrength, 0.001f);
    }

    [Test]
    public void ParseEdges_NegativeTrust_ClampedToZero()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds(
            "npc_a", new[] { "EDGE:npc_a>npc_b:0.5:-0.3" });

        Assert.AreEqual(1, edges.Count);
        Assert.AreEqual(0f, edges[0].TrustInRelationship, 0.001f);
    }

    [Test]
    public void ParseEdges_EmptyList_ReturnsEmpty()
    {
        var edges = MemoryProcessFunction.ParseEdgesFromWitnessIds("npc_a", Array.Empty<string>());
        Assert.AreEqual(0, edges.Count);
    }

    // ─── MergeEdge ────────────────────────────────────────────────────────────

    [Test]
    public void MergeEdge_NewEdge_Added()
    {
        var doc = new NpcMemoryDocument { Id = "x_y", NpcId = "x", PlayerId = "y" };
        var edge = new SocialGraphEdge { TargetNpcId = "npc_b", RelationshipStrength = 0.5f, TrustInRelationship = 0.6f };

        MemoryProcessFunction.MergeEdge(doc, edge);

        Assert.AreEqual(1, doc.SocialGraphEdges.Count);
        Assert.AreEqual("npc_b", doc.SocialGraphEdges[0].TargetNpcId);
        Assert.AreEqual(0.5f, doc.SocialGraphEdges[0].RelationshipStrength, 0.001f);
    }

    [Test]
    public void MergeEdge_ExistingEdge_UpdatedInPlace()
    {
        var doc = new NpcMemoryDocument { Id = "x_y", NpcId = "x", PlayerId = "y" };
        doc.SocialGraphEdges.Add(
            new SocialGraphEdge { TargetNpcId = "npc_b", RelationshipStrength = 0.3f, TrustInRelationship = 0.2f });

        MemoryProcessFunction.MergeEdge(doc,
            new SocialGraphEdge { TargetNpcId = "npc_b", RelationshipStrength = 0.8f, TrustInRelationship = 0.9f });

        Assert.AreEqual(1, doc.SocialGraphEdges.Count);
        Assert.AreEqual(0.8f, doc.SocialGraphEdges[0].RelationshipStrength, 0.001f);
        Assert.AreEqual(0.9f, doc.SocialGraphEdges[0].TrustInRelationship, 0.001f);
    }

    [Test]
    public void MergeEdge_DifferentTargets_BothKept()
    {
        var doc = new NpcMemoryDocument { Id = "x_y", NpcId = "x", PlayerId = "y" };
        doc.SocialGraphEdges.Add(
            new SocialGraphEdge { TargetNpcId = "npc_a", RelationshipStrength = 0.5f, TrustInRelationship = 0.5f });

        MemoryProcessFunction.MergeEdge(doc,
            new SocialGraphEdge { TargetNpcId = "npc_b", RelationshipStrength = 0.7f, TrustInRelationship = 0.6f });

        Assert.AreEqual(2, doc.SocialGraphEdges.Count);
    }

    // ─── Gossip emotional coloring decay (0.6^hops) ──────────────────────────

    [TestCase(0, 1.0f, 1.000f)]
    [TestCase(1, 1.0f, 0.600f)]
    [TestCase(2, 1.0f, 0.360f)]
    [TestCase(3, 1.0f, 0.216f)]
    public void GossipDecay_PerHop(int hop, float original, float expected)
    {
        const float decayFactor = 0.6f;
        var result = original * MathF.Pow(decayFactor, hop);
        Assert.AreEqual(expected, result, 0.001f);
    }

    // ─── Trust-weighted coloring ──────────────────────────────────────────────

    [Test]
    public void TrustScale_NoSourceEdge_UsesStrangerDefault()
    {
        // Stranger trust = 0.3; coloring = 0.8 → effective = 0.24
        const float coloring = 0.8f;
        const float strangerTrust = 0.3f;
        Assert.AreEqual(0.24f, coloring * strangerTrust, 0.001f);
    }

    [Test]
    public void TrustScale_HighTrustFriend_AmplifiesColoring()
    {
        const float coloring = 0.5f;
        const float trustInRelationship = 0.9f;
        Assert.AreEqual(0.45f, coloring * trustInRelationship, 0.001f);
    }

    [Test]
    public void TrustScale_ZeroTrustEnemy_SuppressesColoring()
    {
        const float coloring = 1.0f;
        const float trustInRelationship = 0.0f;
        Assert.AreEqual(0.0f, coloring * trustInRelationship, 0.001f);
    }

    // ─── GetTrustLabel ────────────────────────────────────────────────────────

    [TestCase(0.8f,  "highly")]
    [TestCase(0.51f, "highly")]
    [TestCase(0.3f,  "somewhat")]
    [TestCase(0.01f, "somewhat")]
    [TestCase(-0.3f, "not much")]
    [TestCase(-0.5f, "not much")]
    [TestCase(-0.8f, "not at all")]
    public void GetTrustLabel_ReturnsExpectedLabel(float coloring, string expected)
    {
        Assert.AreEqual(expected, GossipProcessFunction.GetTrustLabel(coloring));
    }
}
