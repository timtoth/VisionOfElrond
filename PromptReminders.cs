using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using FFG.Common;
using FFG.JIME;
using HarmonyLib;

namespace JiMEOverlay
{
    // Hooks the game's message popups and runs their text through PromptRules (prompt-rules.json).
    // Display only: the label text is changed after the game has resolved it; packets and game logic are untouched.
    public static class PromptReminders
    {
        // NGUI markup such as [b], [/i], [-] or [ff0000]; stripped before matching so "[b]scout 2[/b]" matches.
        private static readonly Regex MarkupTag = new Regex(@"\[/?[a-zA-Z0-9-]{0,8}\]", RegexOptions.Compiled);

        // Config rule slots used by 0.5.x; migrated into prompt-rules.json and then removed from the .cfg.
        private const int LegacyRuleSlots = 8;
        private static readonly string[] LegacyDefaults =
        {
            @"Aragorn|\bscouts? \d|Heroes adjacent to Aragorn: [b]Scout 3[/b] instead.",
            @"Aragorn|\breset\w* (?:your|their|his|her) decks?\b[\s\S]{0,80}?\bscouts? (\d+)|Heroes adjacent to Aragorn: [b]Scout +1[/b] (scout {1+1}).",
        };

        private static ManualLogSource _log;
        private static string _lastError;

        // The popup currently inside InitializePopup; only its own label gets rules applied.
        private static MessagePopup _initializingPopup;

        private static readonly AccessTools.FieldRef<MessagePopup, UILocalizationPacket> LocalizedTextRef =
            AccessTools.FieldRefAccess<MessagePopup, UILocalizationPacket>("_localizedText");

        public static void Init(ConfigFile config, ManualLogSource log, string harmonyId, string dataDir)
        {
            _log = log;
            PromptRules.Init(log, dataDir, MigrateLegacyConfig(config));

            try
            {
                var harmony = new Harmony(harmonyId);
                harmony.Patch(AccessTools.Method(typeof(MessagePopup), "InitializePopup"),
                    prefix: new HarmonyMethod(typeof(PromptReminders), nameof(InitializePopupPrefix)),
                    finalizer: new HarmonyMethod(typeof(PromptReminders), nameof(InitializePopupFinalizer)));
                harmony.Patch(AccessTools.Method(typeof(UILocalizationPacket), "UpdateLocalization", new[] { typeof(LocalizationPacket) }),
                    postfix: new HarmonyMethod(typeof(PromptReminders), nameof(UpdateLocalizationPostfix)));
            }
            catch (Exception e)
            {
                log.LogWarning($"Prompt rules disabled; could not patch message popups: {e}");
            }
        }

        private static void InitializePopupPrefix(MessagePopup __instance)
        {
            _initializingPopup = __instance;
        }

        // Finalizer so the gate is cleared even if the game's method throws.
        private static Exception InitializePopupFinalizer(Exception __exception)
        {
            _initializingPopup = null;
            return __exception;
        }

        // Runs after the popup's label has its resolved text, but before the popup sizes and places itself.
        private static void UpdateLocalizationPostfix(UILocalizationPacket __instance, LocalizationPacket __0)
        {
            try
            {
                var popup = _initializingPopup;
                if (popup == null || LocalizedTextRef(popup) != __instance)
                {
                    return;
                }
                var label = __instance.GetComponent<UILabel>();
                if (label == null || string.IsNullOrEmpty(label.text))
                {
                    return;
                }

                var context = new PromptContext
                {
                    Keys = GetKeys(__0),
                    PlainText = MarkupTag.Replace(label.text, ""),
                    PartyNames = GetPartyNames(),
                    IsBattleMap = IsBattleMap(),
                };
                var newText = PromptRules.Apply(label.text, context);
                if (newText != label.text)
                {
                    label.text = newText;
                }
            }
            catch (Exception e)
            {
                // Leave the original text alone; log each distinct error once.
                if (e.Message != _lastError)
                {
                    _lastError = e.Message;
                    _log.LogWarning($"Prompt rule failed: {e}");
                }
            }
        }

        // The packet's own key plus the keys of any localized inserts (e.g. a title name inside an award message).
        private static List<string> GetKeys(LocalizationPacket packet)
        {
            var keys = new List<string>();
            if (packet == null)
            {
                return keys;
            }
            if (!string.IsNullOrEmpty(packet.Key))
            {
                keys.Add(packet.Key);
            }
            if (packet is IA_LocalizationPacket ia && ia.Inserts != null)
            {
                foreach (var insert in ia.Inserts)
                {
                    if (insert != null && insert.IsUsed && insert.LocalizationKey != null && !string.IsNullOrEmpty(insert.LocalizationKey.Key))
                    {
                        keys.Add(insert.LocalizationKey.Key);
                    }
                }
            }
            return keys;
        }

        private static List<string> GetPartyNames()
        {
            var names = new List<string>();
            var gameData = SingletonBehaviour<GameData>.IsInitialized ? SingletonBehaviour<GameData>.Instance : null;
            var players = gameData != null ? gameData.Players : null;
            if (players != null)
            {
                foreach (var hero in players)
                {
                    if (hero != null && hero.Model != null)
                    {
                        names.Add(GameSnapshot.Localize(hero.Model.NameKey));
                    }
                }
            }
            return names;
        }

        private static bool IsBattleMap()
        {
            if (!SingletonBehaviour<GameController>.IsInitialized)
            {
                return false;
            }
            var adventure = SingletonBehaviour<GameController>.Instance.CurrentAdventure;
            return adventure != null && adventure.Type == AdventureTypes.Battleboard;
        }

        // Moves 0.5.x [Reminders] config rules into prompt-rules.json form and removes them from the .cfg.
        // Unedited defaults become the current Aragorn rule; edited rules are converted as they are.
        private static List<PromptRule> MigrateLegacyConfig(ConfigFile config)
        {
            var rules = new List<PromptRule>();
            var hasCustom = false;
            var enabled = true;

            var enabledEntry = config.Bind("Reminders", "Enabled", true, "Moved to prompt-rules.json.");
            enabled = enabledEntry.Value;
            config.Remove(enabledEntry.Definition);

            for (int i = 1; i <= LegacyRuleSlots; i++)
            {
                var entry = config.Bind("Reminders", $"Rule{i}", "", "Moved to prompt-rules.json.");
                var raw = RestoreEscapes(entry.Value ?? "");
                config.Remove(entry.Definition);
                if (string.IsNullOrWhiteSpace(raw) || Array.IndexOf(LegacyDefaults, raw) >= 0)
                {
                    continue;
                }
                var rule = ParseLegacyRule(raw, i);
                if (rule != null)
                {
                    rules.Add(rule);
                    hasCustom = true;
                }
            }
            config.Save();

            if (!hasCustom)
            {
                rules.Add(PromptRules.DefaultAragornRule());
            }
            if (!enabled)
            {
                rules.ForEach(r => r.Enabled = false);
            }
            return rules;
        }

        // BepInEx unescapes config strings, so a "\b" in the .cfg arrives as a backspace character. Turn it back.
        private static string RestoreEscapes(string pattern)
        {
            return pattern.Replace("\b", @"\b").Replace("\t", @"\t").Replace("\f", @"\f").Replace("\v", @"\v").Replace("\a", @"\a").Replace("\0", @"\0");
        }

        // "Hero|regex|text": hero before the first |, text after the last |, regex in between.
        private static PromptRule ParseLegacyRule(string raw, int slot)
        {
            var first = raw.IndexOf('|');
            var last = raw.LastIndexOf('|');
            if (first < 0 || last == first)
            {
                _log.LogWarning($"Reminders Rule{slot}: not in HeroName|regex|text form, not migrated: \"{raw}\"");
                return null;
            }
            return new PromptRule
            {
                Id = $"migrated-rule-{slot}",
                Description = "Migrated from the [Reminders] config section.",
                When = new PromptCondition
                {
                    HeroesInParty = new List<string> { raw.Substring(0, first).Trim() },
                    TextMatches = raw.Substring(first + 1, last - first - 1).Trim(),
                },
                Append = raw.Substring(last + 1).Trim(),
            };
        }
    }
}
