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
        public const string PluginVersion = "0.5.1";

        private const int WindowId = 0x4A694D45; // "JiME"
        private const float RefreshInterval = 0.5f;
        private const float FadeSpeed = 4f; // opacity change per second

        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _visibleOnStart;
        private ConfigEntry<float> _windowX;
        private ConfigEntry<float> _windowY;
        private ConfigEntry<string> _extraSetupItems;
        private ConfigEntry<float> _idleOpacity;
        private ConfigEntry<float> _hoverOpacity;

        private bool _visible;
        private bool _blockingGameInput;
        private float _opacity = 1f;
        private Texture2D _backdrop;
        private readonly SetupChecklist _setup = new SetupChecklist();
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
            _extraSetupItems = Config.Bind("Setup", "ExtraItems", "", "Extra Round 0 checklist items, separated by |. Example: Set out dice|Deal damage and fear decks");
            _idleOpacity = Config.Bind("Window", "IdleOpacity", 0.6f, new ConfigDescription("Panel opacity when the mouse is elsewhere.", new AcceptableValueRange<float>(0.1f, 1f)));
            _hoverOpacity = Config.Bind("Window", "HoverOpacity", 1f, new ConfigDescription("Panel opacity while the mouse is over it.", new AcceptableValueRange<float>(0.1f, 1f)));

            _backdrop = new Texture2D(1, 1);
            _backdrop.SetPixel(0, 0, new Color(0.06f, 0.06f, 0.06f, 1f));
            _backdrop.Apply();

            PromptReminders.Init(Config, Logger, PluginGuid);

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

            UpdateInputBlocking();

            // Fade toward solid while hovered, see-through otherwise.
            var targetOpacity = _blockingGameInput ? _hoverOpacity.Value : _idleOpacity.Value;
            _opacity = Mathf.MoveTowards(_opacity, targetOpacity, FadeSpeed * Time.unscaledDeltaTime);
        }

        // While the mouse is over the panel, stop NGUI from also clicking the game (or dragging the map) underneath.
        private void UpdateInputBlocking()
        {
            var mouse = Input.mousePosition;
            var over = _visible && _windowRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
            if (over != _blockingGameInput)
            {
                _blockingGameInput = over;
                UICamera.ignoreAllEvents = over;
            }
        }

        private void OnDestroy()
        {
            if (_blockingGameInput)
            {
                UICamera.ignoreAllEvents = false;
            }
        }

        private void RefreshSnapshot()
        {
            try
            {
                _snapshot = GameSnapshot.Capture();
                if (_snapshot.InAdventure)
                {
                    _setup.Sync(_snapshot, _extraSetupItems.Value);
                }
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

            // The default window skin is partly transparent, so a dark backdrop underneath lets hover read as solid.
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _opacity * 0.85f);
            GUI.DrawTexture(_windowRect, _backdrop);
            GUI.color = new Color(1f, 1f, 1f, _opacity);

            var newRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, PluginName, GUILayout.Width(300f));
            GUI.color = oldColor;
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

        private void DrawAdventure(GameSnapshot snap)
        {
            GUILayout.Label($"<b>{snap.AdventureName}</b>{(snap.IsBattleMap ? "  (battle map)" : "")}");

            // Round 0 is the overlay's own setup phase: shown at the start of an adventure until the checklist is done.
            var inSetup = snap.Round <= 1 && !_setup.IsComplete;
            if (inSetup)
            {
                GUILayout.Label("<b>Round 0 · Setup</b>");
                DrawSetupChecklist();
            }
            else
            {
                var phase = snap.Phase == GamePhases.HeroPhase ? "Hero phase" : "Shadow phase";
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Round {snap.Round} · {phase}");
                if (snap.Round <= 1 && GUILayout.Button("Setup list", GUILayout.ExpandWidth(false)))
                {
                    _setup.IsDismissed = false;
                    foreach (var item in _setup.Items)
                    {
                        _setup.SetChecked(item, false);
                    }
                }
                GUILayout.EndHorizontal();
            }

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
                var titles = hero.Titles.Count > 0 ? ", " + string.Join(", ", hero.Titles) : "";
                var role = string.IsNullOrEmpty(hero.Role) ? "" : $" ({hero.Role})";
                GUILayout.Label($"{hero.Name}{titles}{role}{status}");
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

        private void DrawSetupChecklist()
        {
            foreach (var item in _setup.Items)
            {
                var isChecked = _setup.IsChecked(item);
                var newValue = GUILayout.Toggle(isChecked, " " + item.Text);
                if (newValue != isChecked)
                {
                    _setup.SetChecked(item, newValue);
                }
            }
            if (GUILayout.Button("Setup done"))
            {
                _setup.IsDismissed = true;
            }
        }
    }
}
