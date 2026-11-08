using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using FFG.Common;
using FFG.JIME;
using UnityEngine;

namespace JiMEOverlay
{
    // Finds what the mouse is over (a map token or an enemy in the enemy bar) and builds tooltip text for it.
    // Read-only: uses the same raycast as the game's click handling, but never clicks anything.
    public class HoverTooltips
    {
        private const float RayLength = 50f;  // matches FFG.Common.InputController
        private const int NodeLayerMask = 256; // matches FFG.Common.InputController

        private readonly ManualLogSource _log;
        private readonly RaycastHit[] _hits = new RaycastHit[4];
        private string _lastError;

        public bool PreviewTokens = true;
        public string Text { get; private set; }

        public HoverTooltips(ManualLogSource log)
        {
            _log = log;
        }

        public void Clear()
        {
            Text = null;
        }

        public void Update()
        {
            Text = null;
            try
            {
                if (!SingletonBehaviour<GameController>.IsInitialized || SingletonBehaviour<GameController>.Instance.CurrentAdventure == null)
                {
                    return;
                }
                if (UICamera.isOverUI)
                {
                    Text = DescribeEnemy(HoveredEnemy());
                    return;
                }
                Text = DescribeToken(HoveredToken());
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

        private static EnemyGroup HoveredEnemy()
        {
            var hovered = UICamera.hoveredObject;
            var view = hovered != null ? hovered.GetComponentInParent<EnemyMiniView>() : null;
            return view != null ? view.Model : null;
        }

        private GameNode HoveredToken()
        {
            var camera = SingletonBehaviour<GameController>.Instance.CurrentCamera;
            if (camera == null || camera.Camera == null)
            {
                return null;
            }
            var count = Physics.RaycastNonAlloc(camera.Camera.ScreenPointToRay(Input.mousePosition), _hits, RayLength, NodeLayerMask);
            GameNode closest = null;
            var closestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var node = _hits[i].transform.GetComponent<Node>() as GameNode;
                if (node != null && node.IsVisible && TokenTracker.TokenTypeName(node) != null && _hits[i].distance < closestDistance)
                {
                    closest = node;
                    closestDistance = _hits[i].distance;
                }
            }
            return closest;
        }

        private string DescribeToken(GameNode node)
        {
            if (node == null)
            {
                return null;
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
