using System;
using BepInEx;
using BepInEx.Configuration;
using FFG.JIME;
using UnityEngine;

namespace JiMEOverlay
{
    // In-game overlay for Journeys in Middle-earth.
    // Draws a draggable IMGUI panel with live adventure state; F8 shows/hides it.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class OverlayPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.timto.jimeoverlay";
        public const string PluginName = "JiME Overlay";
        public const string PluginVersion = "0.3.0";

        private const int WindowId = 0x4A694D45; // "JiME"
        private const float RefreshInterval = 0.5f;

        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _visibleOnStart;
        private ConfigEntry<float> _windowX;
        private ConfigEntry<float> _windowY;

        private bool _visible;
        private Rect _windowRect;
        private float _sessionStart;

        private GameSnapshot _snapshot = new GameSnapshot();
        private float _nextRefresh;
        private string _lastError;

        private void Awake()
        {
            _toggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "Key that shows or hides the overlay.");
            _visibleOnStart = Config.Bind("General", "VisibleOnStart", true, "Show the overlay when the game launches.");
            _windowX = Config.Bind("Window", "X", 20f, "Overlay left position in pixels.");
            _windowY = Config.Bind("Window", "Y", 20f, "Overlay top position in pixels.");

            _visible = _visibleOnStart.Value;
            _windowRect = new Rect(_windowX.Value, _windowY.Value, 300f, 120f);
            _sessionStart = Time.realtimeSinceStartup;

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press {_toggleKey.Value} to toggle.");
        }

        private void Update()
        {
            if (_toggleKey.Value.IsDown())
            {
                _visible = !_visible;
            }

            if (_visible && Time.realtimeSinceStartup >= _nextRefresh)
            {
                _nextRefresh = Time.realtimeSinceStartup + RefreshInterval;
                RefreshSnapshot();
            }
        }

        private void RefreshSnapshot()
        {
            try
            {
                _snapshot = GameSnapshot.Capture();
            }
            catch (Exception e)
            {
                // Never let a read failure break the game; log each distinct error once.
                _snapshot = new GameSnapshot();
                if (e.Message != _lastError)
                {
                    _lastError = e.Message;
                    Logger.LogWarning($"Failed to read game state: {e}");
                }
            }
        }

        private void OnGUI()
        {
            if (!_visible)
            {
                return;
            }

            var newRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, PluginName, GUILayout.Width(300f));
            if (newRect.position != _windowRect.position)
            {
                _windowX.Value = newRect.x;
                _windowY.Value = newRect.y;
            }
            _windowRect = newRect;
        }

        private void DrawWindow(int id)
        {
            var elapsed = TimeSpan.FromSeconds(Time.realtimeSinceStartup - _sessionStart);
            GUILayout.Label($"Session time: {(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

            var snap = _snapshot;
            if (!snap.InAdventure)
            {
                GUILayout.Label("No adventure in progress");
            }
            else
            {
                DrawAdventure(snap);
            }

            GUILayout.Space(4f);
            GUILayout.Label($"{_toggleKey.Value} to hide · drag title to move");
            GUI.DragWindow();
        }

        private static void DrawAdventure(GameSnapshot snap)
        {
            GUILayout.Label($"<b>{snap.AdventureName}</b>{(snap.IsBattleMap ? "  (battle map)" : "")}");

            var phase = snap.Phase == GamePhases.HeroPhase ? "Hero phase" : "Shadow phase";
            GUILayout.Label($"Round {snap.Round} · {phase}");

            var threat = snap.ThreatMax > 0 ? $"Threat {snap.Threat} / {snap.ThreatMax}" : $"Threat {snap.Threat}";
            if (snap.IsDarkness)
            {
                threat += " · Darkness";
            }
            GUILayout.Label(threat);
            if (snap.ThreatThresholds.Count > 0)
            {
                GUILayout.Label($"Next threat events at: {string.Join(", ", snap.ThreatThresholds)}");
            }

            GUILayout.Space(4f);
            GUILayout.Label(snap.IsInLastStand ? "<b>Heroes</b> · <color=#ff6060>Last stand!</color>" : "<b>Heroes</b>");
            foreach (var hero in snap.Heroes)
            {
                var status = hero.IsDefeated ? " <color=#ff6060>[Defeated]</color>" : "";
                GUILayout.Label($"{hero.Name}{status}");
                GUILayout.Label($"    Corruption {hero.Corruption} · Last stand difficulty {hero.LastStandDifficulty}");
            }

            GUILayout.Space(4f);
            GUILayout.Label($"<b>Enemies in play ({snap.Enemies.Count})</b>");
            if (snap.Enemies.Count == 0)
            {
                GUILayout.Label("  none");
            }
            foreach (var enemy in snap.Enemies)
            {
                var tags = (enemy.IsElite ? " [Elite]" : "") + (enemy.IsExhausted ? " [Exhausted]" : "");
                GUILayout.Label($"{enemy.Name} ×{enemy.UnitsRemaining}/{enemy.UnitStartCount}{tags}");
                GUILayout.Label($"    HP {enemy.Health}/{enemy.TotalHealth} · Armor {enemy.Armor} · Sorcery {enemy.Sorcery}");
            }
        }
    }
}
