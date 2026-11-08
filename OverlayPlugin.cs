using System;
using System.IO;
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
        public const string PluginVersion = "0.9.1";

        private const int WindowId = 0x4A694D45; // "JiME"
        private const float RefreshInterval = 0.5f;
        private const float FadeSpeed = 4f; // opacity change per second
        private const int MinFontSize = 9;
        private const int MaxFontSize = 28;
        private const float BaseWidth = 300f; // panel width at the default font size
        private const int DefaultFontSize = 13;

        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _visibleOnStart;
        private ConfigEntry<float> _windowX;
        private ConfigEntry<float> _windowY;
        private ConfigEntry<string> _extraSetupItems;
        private ConfigEntry<float> _idleOpacity;
        private ConfigEntry<float> _hoverOpacity;
        private ConfigEntry<int> _fontSize;
        private ConfigEntry<bool> _tooltipsEnabled;
        private ConfigEntry<bool> _previewTokens;
        private ConfigEntry<bool> _gameStyleTooltips;

        private const float TooltipBaseWidth = 280f; // tooltip width at the default font size
        private const int GameStyleExtraFontSize = 4; // the game's serif font reads small at the panel's size
        private const float TooltipCursorGap = 20f; // space between the cursor and a tooltip with no anchor
        private HoverTooltips _tooltips;
        private GUIStyle _tooltipStyle;
        private GameStyle _gameStyle;

        // Enemies only show once any have appeared in this adventure, so enemy-free adventures never show the section.
        private string _enemiesSeenAdventure;

        private bool _visible;
        private bool _blockingGameInput;
        private float _opacity = 1f;
        private Texture2D _backdrop;
        private GUIStyle _windowStyle;
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
            _fontSize = Config.Bind("Window", "FontSize", DefaultFontSize, new ConfigDescription("Panel text size; also adjustable with the A- / A+ buttons on the panel.", new AcceptableValueRange<int>(MinFontSize, MaxFontSize)));
            _tooltipsEnabled = Config.Bind("Tooltips", "Enabled", true, "Show tooltips when hovering map tokens and enemies in the enemy bar, even while the panel is hidden.");
            _previewTokens = Config.Bind("Tooltips", "PreviewTokens", true, "For tokens nobody has clicked yet, show the prompt and options a click would show. Turn off to only show what you've already seen.");
            _gameStyleTooltips = Config.Bind("Tooltips", "GameStyle", true, "Draw tooltips with the game's own popup frame, font and colours. Turn off for the overlay panel's style.");
            _tooltips = new HoverTooltips(Logger);
            _gameStyle = new GameStyle(Logger);

            _backdrop = new Texture2D(1, 1);
            _backdrop.SetPixel(0, 0, new Color(0.06f, 0.06f, 0.06f, 1f));
            _backdrop.Apply();

            var dataDir = Path.Combine(Paths.ConfigPath, "JiMEOverlay");
            Directory.CreateDirectory(dataDir);
            PromptExport.Init(Logger, dataDir);
            PromptReminders.Init(Config, Logger, PluginGuid, dataDir);

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
            PromptExport.Tick(Time.realtimeSinceStartup);

            if (_tooltipsEnabled.Value && !_blockingGameInput)
            {
                _tooltips.PreviewTokens = _previewTokens.Value;
                _tooltips.Update();
            }
            else
            {
                _tooltips.Clear();
            }

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
                    if (_snapshot.Enemies.Count > 0)
                    {
                        _enemiesSeenAdventure = _snapshot.AdventureKey;
                    }
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
            DrawTooltip();
            if (!_visible)
            {
                return;
            }

            // The default window skin is partly transparent, so a dark backdrop underneath lets hover read as solid.
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _opacity * 0.85f);
            GUI.DrawTexture(_windowRect, _backdrop);
            GUI.color = new Color(1f, 1f, 1f, _opacity);

            // The window frame gets its own style so its title scales; the contents scale inside DrawWindow,
            // because Unity runs window contents later in the frame, not during this call.
            var size = CurrentFontSize;
            if (_windowStyle == null)
            {
                _windowStyle = new GUIStyle(GUI.skin.window);
            }
            _windowStyle.fontSize = size;

            // Height 0 lets the panel shrink back when content gets shorter.
            var width = BaseWidth * size / DefaultFontSize;
            var newRect = GUILayout.Window(WindowId, new Rect(_windowRect.x, _windowRect.y, width, 0f), DrawWindow, PluginName, _windowStyle, GUILayout.Width(width));
            GUI.color = oldColor;
            if (newRect.position != _windowRect.position)
            {
                _windowX.Value = newRect.x;
                _windowY.Value = newRect.y;
            }
            _windowRect = newRect;
        }

        private int CurrentFontSize => Mathf.Clamp(_fontSize.Value, MinFontSize, MaxFontSize);

        // Drawn next to the cursor and kept on screen. By default it borrows the game's popup frame, font and
        // colours (see GameStyle); otherwise, or if those can't be found, it uses the panel's dark backdrop.
        private void DrawTooltip()
        {
            var text = _tooltips.Text;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (_tooltipStyle == null)
            {
                _tooltipStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            }
            var useGame = _gameStyleTooltips.Value && _gameStyle.TryLoad();
            var size = CurrentFontSize + (useGame && _gameStyle.Font != null ? GameStyleExtraFontSize : 0);
            _tooltipStyle.fontSize = size;
            _tooltipStyle.font = useGame ? _gameStyle.Font : null;
            _tooltipStyle.normal.textColor = useGame ? _gameStyle.TextColor : GUI.skin.label.normal.textColor;
            var framePadding = useGame && _gameStyle.HasFrame ? Mathf.RoundToInt(size * 0.9f) : 8;
            _tooltipStyle.padding = new RectOffset(framePadding, framePadding, framePadding * 3 / 4, framePadding * 3 / 4);
            _tooltipStyle.alignment = useGame ? TextAnchor.UpperCenter : TextAnchor.UpperLeft;

            // Icons the font can draw stay icons; the rest become words.
            text = GameText.NameIcons(text, useGame ? (Func<char, bool>)_gameStyle.CanDraw : null);

            var content = new GUIContent(text);
            var width = TooltipBaseWidth * size / DefaultFontSize;
            var height = _tooltipStyle.CalcHeight(content, width);

            // Centred above the hovered enemy icon or token, or above the cursor; flipped below if there's no room.
            var mouse = Input.mousePosition;
            var anchor = _tooltips.Anchor ?? new Vector2(mouse.x, Screen.height - mouse.y - TooltipCursorGap);
            var x = Mathf.Clamp(anchor.x - width / 2f, 4f, Mathf.Max(4f, Screen.width - width - 4f));
            var y = anchor.y - height - 8f;
            if (y < 4f)
            {
                y = Screen.height - mouse.y + TooltipCursorGap;
            }
            var rect = new Rect(x, Mathf.Clamp(y, 4f, Mathf.Max(4f, Screen.height - height - 4f)), width, height);

            var oldColor = GUI.color;
            if (useGame && _gameStyle.HasFrame)
            {
                GUI.color = GameStyle.FillColor;
                GUI.DrawTexture(rect, _backdrop);
                GUI.color = oldColor;
                _gameStyle.DrawFrame(rect, size / (float)DefaultFontSize * 0.5f);
            }
            else
            {
                GUI.color = new Color(1f, 1f, 1f, 0.92f);
                GUI.DrawTexture(rect, _backdrop);
            }
            GUI.color = oldColor;
            GUI.Label(rect, content, _tooltipStyle);
        }

        private void DrawWindow(int id)
        {
            // Apply the panel's text size to the shared skin while drawing this panel only, then restore it.
            var skin = GUI.skin;
            var oldSizes = new[] { skin.label.fontSize, skin.button.fontSize, skin.toggle.fontSize };
            skin.label.fontSize = skin.button.fontSize = skin.toggle.fontSize = CurrentFontSize;
            try
            {
                DrawContents();
            }
            finally
            {
                skin.label.fontSize = oldSizes[0];
                skin.button.fontSize = oldSizes[1];
                skin.toggle.fontSize = oldSizes[2];
            }
            GUI.DragWindow();
        }

        private void DrawContents()
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
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_toggleKey.Value} to hide · drag title to move");
            if (GUILayout.Button("A-", GUILayout.ExpandWidth(false)))
            {
                _fontSize.Value = Mathf.Max(MinFontSize, _fontSize.Value - 1);
            }
            if (GUILayout.Button("A+", GUILayout.ExpandWidth(false)))
            {
                _fontSize.Value = Mathf.Min(MaxFontSize, _fontSize.Value + 1);
            }
            GUILayout.EndHorizontal();
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

            if (_enemiesSeenAdventure == snap.AdventureKey)
            {
                GUILayout.Space(4f);
                GUILayout.Label($"<b>Enemies in play ({snap.Enemies.Count})</b>");
                if (snap.Enemies.Count == 0)
                {
                    GUILayout.Label("  None");
                }
                foreach (var enemy in snap.Enemies)
                {
                    var tags = (enemy.IsElite ? " [Elite]" : "") + (enemy.IsExhausted ? " [Exhausted]" : "");
                    GUILayout.Label($"{enemy.Name} ×{enemy.UnitsRemaining}/{enemy.UnitStartCount}{tags}");
                    GUILayout.Label($"    HP {enemy.Health}/{enemy.TotalHealth} · Armor {enemy.Armor} · Sorcery {enemy.Sorcery}");
                }
            }

            if (snap.Tokens.Count > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label($"<b>Discovered tokens ({snap.Tokens.Count})</b>");
                foreach (var token in snap.Tokens)
                {
                    DrawToken(token);
                }
            }
        }

        private static void DrawToken(TokenRow token)
        {
            GUILayout.BeginHorizontal();
            var summary = token.IsExplored && !string.IsNullOrEmpty(token.Summary) ? GameText.NameIcons(token.Summary) : "<i>not explored yet</i>";
            GUILayout.Label($"<b>{token.Type}:</b> {summary}");
            // Camera only: centres the map on the token without interacting with it.
            if (token.Node != null && GUILayout.Button("Find", GUILayout.ExpandWidth(false)))
            {
                SingletonBehaviour<GameController>.Instance.CurrentCamera.MoveCameraTo(token.Node.transform.position, 0.75f);
            }
            GUILayout.EndHorizontal();
            if (token.Options.Count > 0)
            {
                GUILayout.Label($"    Options: {GameText.NameIcons(string.Join(" · ", token.Options))}");
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
