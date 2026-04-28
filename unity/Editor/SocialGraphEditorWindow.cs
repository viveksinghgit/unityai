#if UNITY_EDITOR
using System.Collections.Generic;
using NpcSoulEngine.Runtime.Data;
using UnityEditor;
using UnityEngine;

namespace NpcSoulEngine.Editor
{
    /// <summary>
    /// Custom editor window for authoring NPC social graphs.
    ///
    /// Open: Tools > NPC Soul Engine > Social Graph Editor
    ///
    /// Features:
    ///   - Canvas with draggable NPC nodes
    ///   - Click-drag between nodes to add edges
    ///   - Colour coding by NpcRole
    ///   - Inspector panel for selected edge properties
    ///   - Export stats (edge count, isolated nodes)
    /// </summary>
    public sealed class SocialGraphEditorWindow : EditorWindow
    {
        private SocialGraphAsset _asset;
        private SerializedObject _serializedAsset;

        // Canvas state
        private Vector2 _scroll;
        private Vector2 _canvasOffset;
        private bool _isDraggingCanvas;
        private Vector2 _lastMousePos;

        // Node interaction
        private NpcNode _draggingNode;
        private NpcNode _selectedNode;
        private SocialEdge _selectedEdge;

        // Edge creation
        private NpcNode _edgeStartNode;

        private const float NodeWidth  = 110f;
        private const float NodeHeight = 40f;
        private const float GridSize   = 20f;

        private static readonly Color[] RoleColors =
        {
            new Color(0.4f, 0.6f, 1f),   // Civilian — blue
            new Color(1f, 0.8f, 0.2f),   // Merchant — gold
            new Color(0.9f, 0.3f, 0.3f), // Guard — red
            new Color(0.8f, 0.5f, 1f),   // Noble — purple
            new Color(0.4f, 0.8f, 0.5f), // Innkeeper — green
            new Color(0.7f, 0.4f, 0.2f), // Criminal — brown
            new Color(0.9f, 0.9f, 0.9f)  // Clergy — white
        };

        [MenuItem("Tools/NPC Soul Engine/Social Graph Editor")]
        public static void Open() => GetWindow<SocialGraphEditorWindow>("Social Graph Editor");

        private void OnGUI()
        {
            DrawToolbar();

            if (_asset == null)
            {
                EditorGUILayout.HelpBox(
                    "Select or create a SocialGraphAsset to begin.\n" +
                    "Right-click > Create > NPC Soul Engine > Social Graph", MessageType.Info);
                _asset = (SocialGraphAsset)EditorGUILayout.ObjectField(
                    "Social Graph", _asset, typeof(SocialGraphAsset), false);
                return;
            }

            _serializedAsset ??= new SerializedObject(_asset);
            _serializedAsset.Update();

            var totalRect = new Rect(0, EditorStyles.toolbar.fixedHeight, position.width, position.height);
            var canvasRect = new Rect(totalRect.x, totalRect.y,
                totalRect.width - 260f, totalRect.height);
            var inspectorRect = new Rect(canvasRect.xMax, totalRect.y, 260f, totalRect.height);

            DrawCanvas(canvasRect);
            DrawInspector(inspectorRect);
            HandleInput(canvasRect);

            _serializedAsset.ApplyModifiedProperties();

            if (GUI.changed) EditorUtility.SetDirty(_asset);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _asset = (SocialGraphAsset)EditorGUILayout.ObjectField(
                _asset, typeof(SocialGraphAsset), false, GUILayout.Width(200f));

            if (GUILayout.Button("+ Node", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                AddNode();

            if (_asset != null && GUILayout.Button("Clear All", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                if (EditorUtility.DisplayDialog("Clear Graph",
                    "Delete all nodes and edges?", "Clear", "Cancel"))
                {
                    Undo.RecordObject(_asset, "Clear Social Graph");
                    _asset.nodes.Clear();
                    _asset.edges.Clear();
                }
            }

            GUILayout.FlexibleSpace();

            if (_asset != null)
                GUILayout.Label($"{_asset.nodes.Count} nodes  {_asset.edges.Count} edges",
                    EditorStyles.toolbarLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCanvas(Rect canvasRect)
        {
            GUI.Box(canvasRect, GUIContent.none, GUI.skin.box);

            // Draw grid
            Handles.color = new Color(0.3f, 0.3f, 0.3f, 0.3f);
            for (float x = canvasRect.x; x < canvasRect.xMax; x += GridSize)
                Handles.DrawLine(new Vector2(x, canvasRect.y), new Vector2(x, canvasRect.yMax));
            for (float y = canvasRect.y; y < canvasRect.yMax; y += GridSize)
                Handles.DrawLine(new Vector2(canvasRect.x, y), new Vector2(canvasRect.xMax, y));

            // Draw edges
            foreach (var edge in _asset.edges)
            {
                var src = FindNode(edge.sourceNpcId);
                var tgt = FindNode(edge.targetNpcId);
                if (src == null || tgt == null) continue;

                var srcCenter = NodeCenter(src) + _canvasOffset + canvasRect.min;
                var tgtCenter = NodeCenter(tgt) + _canvasOffset + canvasRect.min;

                var isSelected = edge == _selectedEdge;
                Handles.color = isSelected ? Color.yellow : Color.Lerp(Color.grey, Color.white, edge.relationshipStrength);
                Handles.DrawLine(srcCenter, tgtCenter);

                // Arrow head
                var dir = (tgtCenter - srcCenter).normalized;
                var arrowBase = tgtCenter - dir * (NodeWidth * 0.5f);
                var perp = new Vector2(-dir.y, dir.x) * 6f;
                Handles.DrawLine(arrowBase, arrowBase - dir * 12f + perp);
                Handles.DrawLine(arrowBase, arrowBase - dir * 12f - perp);

                if (edge.bidirectional)
                {
                    var revBase = srcCenter + dir * (NodeWidth * 0.5f);
                    Handles.DrawLine(revBase, revBase + dir * 12f + perp);
                    Handles.DrawLine(revBase, revBase + dir * 12f - perp);
                }

                // Edge label
                var mid = (srcCenter + tgtCenter) * 0.5f;
                GUI.Label(new Rect(mid.x - 20, mid.y - 8, 40, 16),
                    $"{edge.relationshipStrength:F1}", EditorStyles.miniLabel);
            }

            // Draw in-progress edge
            if (_edgeStartNode != null)
            {
                var start = NodeCenter(_edgeStartNode) + _canvasOffset + canvasRect.min;
                Handles.color = Color.cyan;
                Handles.DrawLine(start, Event.current.mousePosition);
                Repaint();
            }

            // Draw nodes (on top of edges)
            foreach (var node in _asset.nodes)
            {
                var nodeRect = new Rect(
                    node.editorPosition.x + _canvasOffset.x + canvasRect.x,
                    node.editorPosition.y + _canvasOffset.y + canvasRect.y,
                    NodeWidth, NodeHeight);

                var roleColor = RoleColors[(int)node.role % RoleColors.Length];
                var isSelected = node == _selectedNode || node == _edgeStartNode;

                var style = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = MakeTex(2, 2,
                        isSelected ? Color.Lerp(roleColor, Color.white, 0.4f) : roleColor) },
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    fontSize  = 10
                };

                GUI.Box(nodeRect, $"{node.displayName ?? node.npcId}\n<size=8>{node.role}</size>", style);
            }
        }

        private void DrawInspector(Rect rect)
        {
            GUILayout.BeginArea(rect);
            EditorGUILayout.LabelField("Inspector", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (_selectedNode != null)
            {
                EditorGUILayout.LabelField("Node", EditorStyles.boldLabel);
                _selectedNode.npcId       = EditorGUILayout.TextField("NPC ID", _selectedNode.npcId);
                _selectedNode.displayName = EditorGUILayout.TextField("Display Name", _selectedNode.displayName);
                _selectedNode.role        = (NpcRole)EditorGUILayout.EnumPopup("Role", _selectedNode.role);

                EditorGUILayout.Space(8);
                if (GUILayout.Button("Delete Node"))
                {
                    Undo.RecordObject(_asset, "Delete NPC Node");
                    RemoveNode(_selectedNode);
                    _selectedNode = null;
                }
            }
            else if (_selectedEdge != null)
            {
                EditorGUILayout.LabelField("Edge", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{_selectedEdge.sourceNpcId} → {_selectedEdge.targetNpcId}",
                    EditorStyles.miniLabel);
                _selectedEdge.relationshipStrength = EditorGUILayout.Slider(
                    "Strength", _selectedEdge.relationshipStrength, 0f, 1f);
                _selectedEdge.trustInRelationship = EditorGUILayout.Slider(
                    "Trust", _selectedEdge.trustInRelationship, 0f, 1f);
                _selectedEdge.bidirectional = EditorGUILayout.Toggle("Bidirectional",
                    _selectedEdge.bidirectional);
                _selectedEdge.relationshipType = (RelationshipType)EditorGUILayout.EnumPopup(
                    "Type", _selectedEdge.relationshipType);

                EditorGUILayout.Space(8);
                if (GUILayout.Button("Delete Edge"))
                {
                    Undo.RecordObject(_asset, "Delete Edge");
                    _asset.edges.Remove(_selectedEdge);
                    _selectedEdge = null;
                }
            }
            else
            {
                EditorGUILayout.LabelField("Nothing selected", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Click node to select.", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Alt+drag between nodes to add edge.", EditorStyles.miniLabel);
            }

            GUILayout.EndArea();
        }

        private void HandleInput(Rect canvasRect)
        {
            var e = Event.current;
            if (!canvasRect.Contains(e.mousePosition)) return;

            var localMouse = e.mousePosition - canvasRect.min - _canvasOffset;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                var clickedNode = HitTestNode(localMouse);
                if (clickedNode != null)
                {
                    if (e.alt)
                    {
                        // Start edge drag
                        _edgeStartNode = clickedNode;
                    }
                    else
                    {
                        _draggingNode = clickedNode;
                        _selectedNode = clickedNode;
                        _selectedEdge = null;
                    }
                }
                else
                {
                    _selectedNode = null;
                    // Check edge hit
                    _selectedEdge = HitTestEdge(localMouse);
                    if (_selectedEdge == null) _isDraggingCanvas = true;
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag)
            {
                if (_draggingNode != null)
                {
                    Undo.RecordObject(_asset, "Move Node");
                    _draggingNode.editorPosition += e.delta;
                }
                else if (_isDraggingCanvas)
                {
                    _canvasOffset += e.delta;
                }
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (_edgeStartNode != null)
                {
                    var targetNode = HitTestNode(localMouse);
                    if (targetNode != null && targetNode != _edgeStartNode)
                    {
                        Undo.RecordObject(_asset, "Add Edge");
                        _asset.edges.Add(new SocialEdge
                        {
                            sourceNpcId         = _edgeStartNode.npcId,
                            targetNpcId         = targetNode.npcId,
                            relationshipStrength = 0.5f,
                            trustInRelationship  = 0.5f,
                            bidirectional        = true
                        });
                    }
                    _edgeStartNode = null;
                }
                _draggingNode  = null;
                _isDraggingCanvas = false;
                e.Use();
            }
            else if (e.type == EventType.ContextClick)
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Add Node Here"), false, () =>
                    AddNodeAt(localMouse));
                menu.ShowAsContext();
                e.Use();
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private void AddNode() => AddNodeAt(new Vector2(100, 100));

        private void AddNodeAt(Vector2 pos)
        {
            Undo.RecordObject(_asset, "Add NPC Node");
            _asset.nodes.Add(new NpcNode
            {
                npcId          = $"npc_new_{_asset.nodes.Count}",
                displayName    = "New NPC",
                editorPosition = pos
            });
        }

        private void RemoveNode(NpcNode node)
        {
            _asset.nodes.Remove(node);
            _asset.edges.RemoveAll(e => e.sourceNpcId == node.npcId || e.targetNpcId == node.npcId);
        }

        private NpcNode HitTestNode(Vector2 pos)
        {
            foreach (var node in _asset.nodes)
            {
                var r = new Rect(node.editorPosition, new Vector2(NodeWidth, NodeHeight));
                if (r.Contains(pos)) return node;
            }
            return null;
        }

        private SocialEdge HitTestEdge(Vector2 pos)
        {
            foreach (var edge in _asset.edges)
            {
                var src = FindNode(edge.sourceNpcId);
                var tgt = FindNode(edge.targetNpcId);
                if (src == null || tgt == null) continue;
                if (PointNearSegment(pos, NodeCenter(src), NodeCenter(tgt), 6f))
                    return edge;
            }
            return null;
        }

        private static bool PointNearSegment(Vector2 p, Vector2 a, Vector2 b, float threshold)
        {
            var ab = b - a;
            var ap = p - a;
            var t  = Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude);
            return ((a + t * ab) - p).sqrMagnitude < threshold * threshold;
        }

        private NpcNode FindNode(string npcId)
        {
            foreach (var n in _asset.nodes)
                if (n.npcId == npcId) return n;
            return null;
        }

        private static Vector2 NodeCenter(NpcNode n) =>
            n.editorPosition + new Vector2(NodeWidth * 0.5f, NodeHeight * 0.5f);

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}
#endif
