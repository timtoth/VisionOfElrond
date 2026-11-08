using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using FFG.Common;
using FFG.JIME;
using UnityEngine;

namespace JiMEOverlay
{
    // Finds what the mouse is over (a map token or terrain piece, or an enemy in the enemy bar) and builds
    // tooltip text for it, plus a screen point to anchor the tooltip to (the enemy icon or the token).
    // Read-only: uses the same raycast as the game's click handling, but never clicks anything.
    public class HoverTooltips
    {
        private const float RayLength = 50f;   // matches FFG.Common.InputController
        private const int NodeLayerMask = 256; // matches FFG.Common.InputController
        private const float TokenAnchorLift = 28f; // pixels above a token's centre, so the tooltip clears the token
        private const int MaxDiagnostics = 10;

        private readonly ManualLogSource _log;
        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private readonly HashSet<string> _diagnosed = new HashSet<string>();
        private string _lastError;

        public bool PreviewTokens = true;
        public string Text { get; private set; }
        // Where the tooltip should sit, in GUI coordinates (top-left origin): it is drawn centred above this point.
        // Null means "above the cursor".
        public Vector2? Anchor { get; private set; }
        // Identifies the hovered thing, and describes where it is, for the position diagnostics the panel logs.
        public string TargetKey { get; private set; }
        public string PositionInfo { get; private set; }

        public HoverTooltips(ManualLogSource log)
        {
            _log = log;
        }

        public void Clear()
        {
            Text = null;
            Anchor = null;
            TargetKey = null;
            PositionInfo = null;
        }

        public void Update()
        {
            Clear();
            try
            {
                if (!SingletonBehaviour<GameController>.IsInitialized || SingletonBehaviour<GameController>.Instance.CurrentAdventure == null)
                {
                    return;
                }

                var hovered = UICamera.hoveredObject;
                var view = hovered != null ? hovered.GetComponentInParent<EnemyMiniView>() : null;
                if (view != null)
                {
                    Text = DescribeEnemy(view.Model);
                    Anchor = WidgetTop(view, out var box);
                    TargetKey = $"enemy:{view.GetInstanceID()}";
                    PositionInfo = $"enemy '{(view.Model != null ? view.Model.GetEnemyName(null, false, false) : view.name)}' icon screen box {box}";
                    return;
                }

                // Over some other piece of the game's UI: no map tooltip through it.
                if (hovered != null && hovered.GetComponentInParent<UIWidget>() != null)
                {
                    if (_diagnosed.Count < MaxDiagnostics && _diagnosed.Add($"ui|{hovered.name}"))
                    {
                        _log.LogInfo($"Hover diagnostic: mouse is over game UI '{hovered.name}', so no map tooltip.");
                    }
                    return;
                }

                var node = HoveredToken(out var camera);
                if (node != null)
                {
                    Text = DescribeToken(node);
                    var world = node.transform.position;
                    Anchor = WorldPoint(camera, world, TokenAnchorLift);
                    var screen = camera.WorldToScreenPoint(world);
                    var kind = IsTerrain(node) ? $"terrain {GameSnapshot.Localize(node.TerrainModel.NameKey)}" : $"{TokenTracker.TokenTypeName(node)} token";
                    TargetKey = $"node:{node.GetInstanceID()}";
                    PositionInfo = $"{kind} '{node.name}' at world ({world.x:0.00}, {world.y:0.00}, {world.z:0.00})"
                        + $" -> screen ({screen.x:0}, {screen.y:0}, depth {screen.z:0.0}) via camera '{camera.name}'";
                }
            }
            catch (Exception e)
            {
                if (e.Message != _lastError)
                {
                    _lastError = e.Message;
                    _log.LogWarning($"Hover tooltip failed: {e}");
                }
            }
        }

        // Top centre of the enemy icon on screen; box describes the icon's screen rectangle for diagnostics.
        private static Vector2? WidgetTop(EnemyMiniView view, out string box)
        {
            box = "(unknown)";
            var widget = view.GetComponent<UIWidget>() ?? view.GetComponentInChildren<UIWidget>();
            var camera = NGUITools.FindCameraForLayer(view.gameObject.layer);
            if (widget == null || camera == null)
            {
                box = widget == null ? "(no widget)" : "(no UI camera)";
                return null;
            }
            var corners = widget.worldCorners;
            float top = float.MinValue, bottom = float.MaxValue, left = float.MaxValue, right = float.MinValue;
            foreach (var corner in corners)
            {
                var p = camera.WorldToScreenPoint(corner);
                top = Mathf.Max(top, p.y);
                bottom = Mathf.Min(bottom, p.y);
                left = Mathf.Min(left, p.x);
                right = Mathf.Max(right, p.x);
            }
            box = $"x {left:0}-{right:0}, y {bottom:0}-{top:0} (from bottom) via camera '{camera.name}'";
            return new Vector2((left + right) / 2f, Screen.height - top);
        }

        private static Vector2? WorldPoint(Camera camera, Vector3 world, float lift)
        {
            if (camera == null)
            {
                return null;
            }
            var p = camera.WorldToScreenPoint(world);
            if (p.z <= 0f)
            {
                return null; // behind the camera
            }
            return new Vector2(p.x, Screen.height - p.y - lift);
        }

        private GameNode HoveredToken(out Camera camera)
        {
            camera = null;
            var controller = SingletonBehaviour<GameController>.Instance.CurrentCamera;
            if (controller == null || controller.Camera == null)
            {
                return null;
            }
            camera = controller.Camera;
            var ray = camera.ScreenPointToRay(Input.mousePosition);
            var count = Physics.RaycastNonAlloc(ray, _hits, RayLength, NodeLayerMask);
            GameNode closest = null;
            var closestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var node = _hits[i].transform.GetComponentInParent<GameNode>();
                if (node == null || (TokenTracker.TokenTypeName(node) == null && !IsTerrain(node)) || !node.IsVisible)
                {
                    Diagnose(_hits[i], node);
                    continue;
                }
                if (_hits[i].distance < closestDistance)
                {
                    closest = node;
                    closestDistance = _hits[i].distance;
                }
            }
            return closest;
        }

        // Logs (a few times per session) what the hover raycast hit when it didn't find a usable token,
        // so a token that never gets a tooltip can be traced from BepInEx\LogOutput.log.
        private void Diagnose(RaycastHit hit, GameNode node)
        {
            if (_diagnosed.Count >= MaxDiagnostics)
            {
                return;
            }
            var name = hit.transform.name;
            var reason = node == null ? "no GameNode" : $"type {node.Token3DType}, node {node.NodeType}, visible {node.IsVisible}";
            if (_diagnosed.Add($"{name}|{reason}"))
            {
                _log.LogInfo($"Hover diagnostic: hit '{name}' (layer {hit.transform.gameObject.layer}) but no tooltip: {reason}.");
            }
        }

        private static bool IsTerrain(GameNode node) => node.NodeType == GameNodeTypes.Terrain && node.TerrainModel != null;

        // Terrain: the game's own rule text for it (what clicking it shows), plus any interaction it offers.
        private string DescribeTerrain(GameNode node)
        {
            var nameKey = node.TerrainModel.NameKey ?? "";
            var baseKey = nameKey.StartsWith("TERRAIN_NODE_") ? nameKey.Substring("TERRAIN_NODE_".Length) : nameKey;
            var info = FirstLocalized($"TERRAIN_NODE_INFO_{baseKey}", $"{nameKey}_REMINDER");
            var sb = new StringBuilder(info != null ? GameText.ToOverlay(info, nameIcons: false) : $"<b>{GameSnapshot.Localize(nameKey)}</b>");
            if (TokenTracker.TryPreview(node, out _, out var options) && options != null && options.Count > 0)
            {
                sb.Append("\n<b>Options:</b> ").Append(string.Join(" · ", options));
            }
            return sb.ToString();
        }

        private static string FirstLocalized(params string[] keys)
        {
            foreach (var key in keys)
            {
                var text = GameSnapshot.Localize(key);
                if (!string.IsNullOrEmpty(text) && text != key)
                {
                    return text;
                }
            }
            return null;
        }

        private string DescribeToken(GameNode node)
        {
            if (IsTerrain(node))
            {
                return DescribeTerrain(node);
            }
            var sb = new StringBuilder($"<b>{TokenTracker.TokenTypeName(node)}</b>");
            if (TokenTracker.TryGetRecord(node, out var summary, out var options))
            {
                AppendPrompt(sb, summary, options);
            }
            else if (PreviewTokens && TokenTracker.TryPreview(node, out summary, out options))
            {
                AppendPrompt(sb, summary, options);
            }
            else
            {
                sb.Append("\n<i>Not explored yet</i>");
            }
            return sb.ToString();
        }

        private static void AppendPrompt(StringBuilder sb, string summary, List<string> options)
        {
            if (!string.IsNullOrEmpty(summary))
            {
                sb.Append('\n').Append(summary);
            }
            if (options != null && options.Count > 0)
            {
                sb.Append("\n<b>Options:</b> ").Append(string.Join(" · ", options));
            }
        }

        private static string DescribeEnemy(EnemyGroup group)
        {
            if (group == null || group.Model == null)
            {
                return null;
            }
            var enemy = GameSnapshot.ReadEnemy(group);
            var tags = (enemy.IsElite ? " [Elite]" : "") + (enemy.IsExhausted ? " [Exhausted]" : "");
            return $"<b>{enemy.Name}</b>{tags}\n×{enemy.UnitsRemaining}/{enemy.UnitStartCount} · HP {enemy.Health}/{enemy.TotalHealth}\nArmor {enemy.Armor} · Sorcery {enemy.Sorcery}";
        }
    }
}
