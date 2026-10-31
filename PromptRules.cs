using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace JiMEOverlay
{
    // One entry in prompt-rules.json: when every condition in "when" holds, add "append"/"prepend" to the popup.
    public class PromptRule
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("description")] public string Description;
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("when")] public PromptCondition When = new PromptCondition();
        [JsonProperty("append", NullValueHandling = NullValueHandling.Ignore)] public string Append;
        [JsonProperty("prepend", NullValueHandling = NullValueHandling.Ignore)] public string Prepend;

        [JsonIgnore] public Regex TextPattern;
        [JsonIgnore] public List<Regex> KeyPatterns = new List<Regex>();
    }

    public class PromptCondition
    {
        // Any of these heroes in the party (name contains, ignoring case). Empty or missing: no hero condition.
        [JsonProperty("heroesInParty", NullValueHandling = NullValueHandling.Ignore)] public List<string> HeroesInParty;
        // Any of these localization keys (see prompts.json); * is a wildcard. Empty or missing: any key.
        [JsonProperty("keys", NullValueHandling = NullValueHandling.Ignore)] public List<string> Keys;
        // .NET regex matched against the popup text with [b]/[i] tags removed, ignoring case.
        [JsonProperty("textMatches", NullValueHandling = NullValueHandling.Ignore)] public string TextMatches;
        // true: battle maps only; false: tile maps only; missing: both.
        [JsonProperty("battleMap", NullValueHandling = NullValueHandling.Ignore)] public bool? BattleMap;
    }

    public class PromptRuleFile
    {
        [JsonProperty("_help")] public List<string> Help;
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("rules")] public List<PromptRule> Rules = new List<PromptRule>();
    }

    // What a popup looks like to the rules: its keys, plain text and the current party.
    public class PromptContext
    {
        public List<string> Keys;
        public string PlainText;
        public List<string> PartyNames;
        public bool IsBattleMap;
    }

    // Loads prompt-rules.json, reloads it when the file changes, and applies matching rules to popup text.
    public static class PromptRules
    {
        public static readonly List<string> HelpText = new List<string>
        {
            "Rules add text to the game's message popups. Edits take effect on the next popup; no restart needed.",
            "A rule applies when every condition under \"when\" holds; leave a condition out to skip it.",
            "when.heroesInParty: any of these heroes is in the party (name contains, ignoring case).",
            "when.keys: the popup uses any of these localization keys (look them up in prompts.json); * is a wildcard.",
            "when.textMatches: a .NET regex matched against the popup text with formatting tags removed, ignoring case.",
            "when.battleMap: true for battle maps only, false for tile maps only.",
            "append / prepend: text added after / before the popup text, separated by a blank line. [b]bold[/b] and [i]italic[/i] work.",
            "Rules only ever add text; the game's own prompt text is always shown unchanged.",
            "Placeholders: {1} inserts regex group 1 from textMatches, {1+1} adds 1 to it as a number, {hero} is the matched hero's name.",
        };

        private const string DefaultAragornText = "Heroes adjacent to Aragorn: [b]Scout +1[/b] (scout {1+1}).";
        private const string DefaultAragornPattern = @"\breset\w* (?:your|their|his|her) decks?\b[\s\S]{0,80}?\bscouts? (\d+)";

        // "{1}" inserts regex group 1; "{1+1}" or "{1-2}" adds to it as a number; "{hero}" is the matched hero.
        private static readonly Regex Placeholder = new Regex(@"\{(?:(\d+)(?:([+-])(\d+))?|hero)\}", RegexOptions.Compiled);

        private static ManualLogSource _log;
        private static string _path;
        private static DateTime _loadedWriteTime;
        private static PromptRuleFile _file = new PromptRuleFile { Enabled = false };

        public static string FilePath => _path;

        public static void Init(ManualLogSource log, string dataDir, IEnumerable<PromptRule> migratedRules)
        {
            _log = log;
            _path = Path.Combine(dataDir, "prompt-rules.json");
            if (!File.Exists(_path))
            {
                WriteDefaultFile(migratedRules);
            }
            ReloadIfChanged();
        }

        public static PromptRule DefaultAragornRule() => new PromptRule
        {
            Id = "aragorn-rally-scout",
            Description = "Aragorn: in the rally phase, heroes adjacent to him scout 1 more than the app says.",
            When = new PromptCondition
            {
                HeroesInParty = new List<string> { "Aragorn" },
                TextMatches = DefaultAragornPattern,
            },
            Append = DefaultAragornText,
        };

        // Returns the popup text with every matching rule applied, or the original text.
        public static string Apply(string text, PromptContext context)
        {
            ReloadIfChanged();
            if (!_file.Enabled || _file.Rules == null)
            {
                return text;
            }

            var result = text;
            foreach (var rule in _file.Rules)
            {
                if (rule == null || !rule.Enabled || !Matches(rule, context, out var match, out var hero))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(rule.Prepend))
                {
                    var addition = Expand(rule.Prepend, match, hero);
                    // Skip if already shown, so a repeated or re-localized popup never gets it twice.
                    if (!result.Contains(addition))
                    {
                        result = addition + "\n\n" + result;
                    }
                }
                if (!string.IsNullOrEmpty(rule.Append))
                {
                    var addition = Expand(rule.Append, match, hero);
                    if (!result.Contains(addition))
                    {
                        result = result + "\n\n" + addition;
                    }
                }
            }
            return result;
        }

        private static bool Matches(PromptRule rule, PromptContext context, out Match match, out string hero)
        {
            match = null;
            hero = null;
            var when = rule.When ?? new PromptCondition();

            if (when.BattleMap.HasValue && when.BattleMap.Value != context.IsBattleMap)
            {
                return false;
            }

            if (when.HeroesInParty != null && when.HeroesInParty.Count > 0)
            {
                foreach (var wanted in when.HeroesInParty)
                {
                    hero = context.PartyNames.Find(n => !string.IsNullOrEmpty(wanted) && n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hero != null)
                    {
                        break;
                    }
                }
                if (hero == null)
                {
                    return false;
                }
            }

            if (rule.KeyPatterns.Count > 0 && !rule.KeyPatterns.Exists(p => context.Keys.Exists(k => p.IsMatch(k))))
            {
                return false;
            }

            if (rule.TextPattern != null)
            {
                match = rule.TextPattern.Match(context.PlainText);
                if (!match.Success)
                {
                    return false;
                }
            }
            return true;
        }

        private static string Expand(string text, Match match, string hero)
        {
            return Placeholder.Replace(text, p =>
            {
                if (!p.Groups[1].Success)
                {
                    return hero ?? p.Value;
                }
                if (match == null)
                {
                    return p.Value;
                }
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

        private static void ReloadIfChanged()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }
                var writeTime = File.GetLastWriteTimeUtc(_path);
                if (writeTime == _loadedWriteTime)
                {
                    return;
                }
                _loadedWriteTime = writeTime;

                var file = JsonConvert.DeserializeObject<PromptRuleFile>(File.ReadAllText(_path)) ?? new PromptRuleFile();
                file.Rules = file.Rules ?? new List<PromptRule>();
                var loaded = 0;
                foreach (var rule in file.Rules)
                {
                    if (rule != null && Compile(rule))
                    {
                        loaded++;
                    }
                }
                _file = file;
                _log.LogInfo($"Loaded {loaded} prompt rule(s) from {_path}{(file.Enabled ? "" : " (all disabled)")}");
            }
            catch (Exception e)
            {
                // Keep the previous rules so a typo mid-edit doesn't switch everything off.
                _log.LogWarning($"Could not read {_path}, keeping previous rules: {e.Message}");
            }
        }

        // Builds the regexes; a rule with a bad pattern is disabled rather than breaking the file.
        private static bool Compile(PromptRule rule)
        {
            var name = string.IsNullOrEmpty(rule.Id) ? "(unnamed rule)" : rule.Id;
            var when = rule.When ?? (rule.When = new PromptCondition());
            rule.KeyPatterns = new List<Regex>();
            rule.TextPattern = null;
            try
            {
                if (when.Keys != null)
                {
                    foreach (var key in when.Keys)
                    {
                        if (!string.IsNullOrEmpty(key))
                        {
                            rule.KeyPatterns.Add(new Regex("^" + Regex.Escape(key).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                        }
                    }
                }
                if (!string.IsNullOrEmpty(when.TextMatches))
                {
                    rule.TextPattern = new Regex(when.TextMatches, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
            }
            catch (ArgumentException e)
            {
                _log.LogWarning($"Prompt rule {name}: invalid pattern, rule disabled: {e.Message}");
                rule.Enabled = false;
                return false;
            }
            if (string.IsNullOrEmpty(rule.Append) && string.IsNullOrEmpty(rule.Prepend))
            {
                _log.LogWarning($"Prompt rule {name}: has neither append nor prepend text, rule disabled.");
                rule.Enabled = false;
                return false;
            }
            return rule.Enabled;
        }

        private static void WriteDefaultFile(IEnumerable<PromptRule> migratedRules)
        {
            var file = new PromptRuleFile { Help = HelpText };
            file.Rules.AddRange(migratedRules);
            if (file.Rules.Count == 0)
            {
                file.Rules.Add(DefaultAragornRule());
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonConvert.SerializeObject(file, Formatting.Indented));
            _log.LogInfo($"Created {_path} with {file.Rules.Count} rule(s).");
        }
    }
}
