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
    // A reminder appended to a message popup when a hero is in the party and the text matches.
    public class ReminderRule
    {
        public string HeroName;
        public Regex Pattern;
        public string Text;
    }

    // Appends hero-specific reminders to the game's message popups, e.g. Aragorn's Scout bonus.
    // Display only: the label text is changed after the game has resolved it; packets and game logic are untouched.
    public static class PromptReminders
    {
        public const int RuleSlots = 8;

        // Aragorn's rally phase bonus: when the app says to reset your deck and scout N, heroes adjacent to him scout one more.
        private const string DefaultRule1 = @"Aragorn|\breset\w* (?:your|their|his|her) decks?\b[\s\S]{0,80}?\bscouts? (\d+)|Heroes adjacent to Aragorn: [b]Scout +1[/b] (scout {1+1}).";
        // Shipped in 0.5.0; it matched every Scout prompt, including setup. Replaced if the user never edited it.
        private const string OldDefaultRule1 = @"Aragorn|\bscouts? \d|Heroes adjacent to Aragorn: [b]Scout 3[/b] instead.";

        // "{1}" inserts regex group 1; "{1+1}" or "{1-2}" adds to it as a number.
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)(?:([+-])(\d+))?\}", RegexOptions.Compiled);

        // NGUI markup such as [b], [/i], [-] or [ff0000]; stripped before matching so "[b]scout 2[/b]" matches.
        private static readonly Regex MarkupTag = new Regex(@"\[/?[a-zA-Z0-9-]{0,8}\]", RegexOptions.Compiled);

        private static ManualLogSource _log;
        private static ConfigEntry<bool> _enabled;
        private static readonly List<ConfigEntry<string>> _ruleEntries = new List<ConfigEntry<string>>();
        private static List<ReminderRule> _rules;
        private static string _lastError;

        // The popup currently inside InitializePopup; only its own label gets reminders.
        private static MessagePopup _initializingPopup;

        private static readonly AccessTools.FieldRef<MessagePopup, UILocalizationPacket> LocalizedTextRef =
            AccessTools.FieldRefAccess<MessagePopup, UILocalizationPacket>("_localizedText");

        public static void Init(ConfigFile config, ManualLogSource log, string harmonyId)
        {
            _log = log;
            _enabled = config.Bind("Reminders", "Enabled", true, "Append hero reminders to the game's message popups.");
            for (int i = 1; i <= RuleSlots; i++)
            {
                var defaultValue = i == 1 ? DefaultRule1 : "";
                var entry = config.Bind("Reminders", $"Rule{i}", defaultValue,
                    "Format: HeroName|regex|text to append. Applies when a party hero's name contains HeroName and the popup text matches the regex (both case-insensitive). The regex sits between the first and last |. NGUI [b] and [i] tags work in the text; {1} inserts regex group 1 and {1+1} adds 1 to it. Leave empty to disable.");
                if (i == 1 && RestoreEscapes(entry.Value) == OldDefaultRule1)
                {
                    entry.Value = DefaultRule1;
                    log.LogInfo("Reminders Rule1: updated the old Aragorn default to the deck-reset rule.");
                }
                entry.SettingChanged += (s, e) => _rules = null;
                _ruleEntries.Add(entry);
            }

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
                log.LogWarning($"Prompt reminders disabled; could not patch message popups: {e}");
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
        private static void UpdateLocalizationPostfix(UILocalizationPacket __instance)
        {
            try
            {
                var popup = _initializingPopup;
                if (popup == null || !_enabled.Value || LocalizedTextRef(popup) != __instance)
                {
                    return;
                }
                var label = __instance.GetComponent<UILabel>();
                if (label == null || string.IsNullOrEmpty(label.text))
                {
                    return;
                }
                var newText = AppendReminders(label.text);
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
                    _log.LogWarning($"Prompt reminder failed: {e}");
                }
            }
        }

        private static string AppendReminders(string text)
        {
            var rules = GetRules();
            if (rules.Count == 0)
            {
                return text;
            }
            var party = GetPartyNames();
            if (party.Count == 0)
            {
                return text;
            }

            var plain = MarkupTag.Replace(text, "");
            var result = text;
            foreach (var rule in rules)
            {
                if (!party.Exists(n => n.IndexOf(rule.HeroName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }
                var match = rule.Pattern.Match(plain);
                if (!match.Success)
                {
                    continue;
                }
                var reminder = ExpandPlaceholders(rule.Text, match);
                // Skip if already shown, so a repeated or re-localized popup never gets it twice.
                if (!result.Contains(reminder))
                {
                    result += "\n\n" + reminder;
                }
            }
            return result;
        }

        private static string ExpandPlaceholders(string text, Match match)
        {
            return Placeholder.Replace(text, p =>
            {
                var group = match.Groups[int.Parse(p.Groups[1].Value)];
                if (!group.Success)
                {
                    return p.Value;
                }
                if (!p.Groups[2].Success)
                {
                    return group.Value;
                }
                if (!int.TryParse(group.Value, out var number))
                {
                    return p.Value;
                }
                var offset = int.Parse(p.Groups[3].Value);
                return (p.Groups[2].Value == "+" ? number + offset : number - offset).ToString();
            });
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

        private static List<ReminderRule> GetRules()
        {
            if (_rules != null)
            {
                return _rules;
            }
            var rules = new List<ReminderRule>();
            foreach (var entry in _ruleEntries)
            {
                var rule = ParseRule(entry.Value, entry.Definition.Key);
                if (rule != null)
                {
                    rules.Add(rule);
                }
            }
            _rules = rules;
            return rules;
        }

        // BepInEx unescapes config strings, so a single "\b" typed in the .cfg arrives as a backspace character.
        // Turn such characters back into regex escapes so both "\b" and "\\b" mean a word boundary.
        private static string RestoreEscapes(string pattern)
        {
            return pattern.Replace("\b", @"\b").Replace("\t", @"\t").Replace("\n", @"\n").Replace("\r", @"\r")
                .Replace("\f", @"\f").Replace("\v", @"\v").Replace("\a", @"\a").Replace("\0", @"\0");
        }

        // "Hero|regex|text": hero is before the first |, text after the last |, so the regex may use alternation.
        private static ReminderRule ParseRule(string raw, string name)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            var first = raw.IndexOf('|');
            var last = raw.LastIndexOf('|');
            if (first < 0 || last == first)
            {
                _log.LogWarning($"Reminders {name}: expected HeroName|regex|text, got \"{raw}\"");
                return null;
            }
            var hero = raw.Substring(0, first).Trim();
            var pattern = RestoreEscapes(raw.Substring(first + 1, last - first - 1).Trim());
            var text = raw.Substring(last + 1).Trim().Replace("\\n", "\n");
            if (hero.Length == 0 || pattern.Length == 0 || text.Length == 0)
            {
                _log.LogWarning($"Reminders {name}: hero, regex and text must all be set, got \"{raw}\"");
                return null;
            }
            try
            {
                return new ReminderRule
                {
                    HeroName = hero,
                    Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    Text = text,
                };
            }
            catch (ArgumentException e)
            {
                _log.LogWarning($"Reminders {name}: invalid regex \"{pattern}\": {e.Message}");
                return null;
            }
        }
    }
}
