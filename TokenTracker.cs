using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using FFG.Common;
using FFG.JIME;
using HarmonyLib;
using UnityEngine;

namespace JiMEOverlay
{
    public class TokenRow
    {
        public GameNode Node;
        public string Type;
        public bool IsExplored;
        public string Summary;
        public readonly List<string> Options = new List<string>();
    }

    // Remembers what each map token showed the last time it was clicked (its prompt and choice buttons),
    // so the overlay can list discovered tokens and their options without clicking each one again.
    // Records last for the current game session only.
    public static class TokenTracker
    {
        private const float ClickWindow = 5f; // seconds after a click in which the next popup belongs to that token
        private const int SummaryLength = 90;

        private static readonly Regex MarkupTag = new Regex(@"\[/?[a-zA-Z0-9-]{0,8}\]", RegexOptions.Compiled);
        // Game icons are private-use characters the overlay's font can't draw.
        private static readonly Regex IconChars = new Regex(@"[-]", RegexOptions.Compiled);

        private class Record
        {
            public string Summary;
            public List<string> Options = new List<string>();
        }

        private static ManualLogSource _log;
        private static string _lastError;
        private static string _adventureKey;
        private static readonly Dictionary<int, Record> _records = new Dictionary<int, Record>();

        private static GameNode _clickedNode;
        private static float _clickTime;
        private static List<string> _pendingChoices;

        public static void Init(ManualLogSource log, Harmony harmony)
        {
            _log = log;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(GameNode), "OnNodeClicked"),
                    prefix: new HarmonyMethod(typeof(TokenTracker), nameof(OnNodeClickedPrefix)));
                harmony.Patch(AccessTools.Method(typeof(MessagePopup), "ShowMultipleChoiceMessage"),
                    prefix: new HarmonyMethod(typeof(TokenTracker), nameof(ShowMultipleChoicePrefix)));
            }
            catch (Exception e)
            {
                log.LogWarning($"Token tracking disabled; could not patch tokens: {e}");
            }
        }

        private static void OnNodeClickedPrefix(GameNode __instance)
        {
            _clickedNode = __instance;
            _clickTime = Time.realtimeSinceStartup;
            _pendingChoices = null;
        }

        // Runs before InitializePopup, so the choices are ready when the popup's text arrives.
        private static void ShowMultipleChoicePrefix(MessageChoiceItem[] items)
        {
            try
            {
                if (!IsClickPending() || items == null)
                {
                    return;
                }
                _pendingChoices = new List<string>();
                foreach (var item in items)
                {
                    var label = item != null ? ResolvePacket(item.Localization) : null;
                    if (!string.IsNullOrEmpty(label))
                    {
                        _pendingChoices.Add(label);
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // Called by PromptReminders with the popup's original text; the first popup after a click is that token's prompt.
        public static void OnPopupText(string text)
        {
            try
            {
                if (!IsClickPending())
                {
                    return;
                }
                var record = new Record { Summary = Summarize(text) };
                if (_pendingChoices != null)
                {
                    record.Options = _pendingChoices;
                }
                _records[_clickedNode.GetInstanceID()] = record;
                _clickedNode = null;
                _pendingChoices = null;
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // Discovered (visible) encounter tokens of the current adventure, with what each showed when last clicked.
        public static List<TokenRow> Capture(Adventure adventure, string adventureKey)
        {
            if (adventureKey != _adventureKey)
            {
                _adventureKey = adventureKey;
                _records.Clear();
            }

            var rows = new List<TokenRow>();
            if (adventure == null || adventure.ActiveEncounters == null)
            {
                return rows;
            }
            foreach (var node in adventure.ActiveEncounters)
            {
                if (node == null || !node.IsVisible)
                {
                    continue;
                }
                var type = TypeName(node.Token3DType);
                if (type == null)
                {
                    continue;
                }
                var row = new TokenRow { Node = node, Type = type };
                if (_records.TryGetValue(node.GetInstanceID(), out var record))
                {
                    row.IsExplored = true;
                    row.Summary = record.Summary;
                    row.Options.AddRange(record.Options);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static bool IsClickPending()
        {
            return _clickedNode != null && Time.realtimeSinceStartup - _clickTime <= ClickWindow;
        }

        private static string TypeName(Token3DManager.TokenTypes type)
        {
            switch (type)
            {
                case Token3DManager.TokenTypes.Circle_Search: return "Search";
                case Token3DManager.TokenTypes.Circle_Threat: return "Threat";
                case Token3DManager.TokenTypes.Circle_Explore: return "Explore";
                case Token3DManager.TokenTypes.Circle_Dwarf: return "Person (Dwarf)";
                case Token3DManager.TokenTypes.Circle_Elf: return "Person (Elf)";
                case Token3DManager.TokenTypes.Circle_Hobbit: return "Person (Hobbit)";
                case Token3DManager.TokenTypes.Circle_Human: return "Person (Human)";
                default: return null;
            }
        }

        private static string ResolvePacket(LocalizationPacket packet)
        {
            if (packet == null)
            {
                return null;
            }
            var ia = packet as IA_LocalizationPacket;
            var text = ia != null && !string.IsNullOrEmpty(ia.LocalizedValue) ? ia.LocalizedValue : GameSnapshot.Localize(packet.Key);
            return Clean(text);
        }

        // First paragraph of the prompt, cleaned for the overlay's font and shortened.
        private static string Summarize(string text)
        {
            var clean = Clean(text);
            var paragraphEnd = clean.IndexOf('\n');
            if (paragraphEnd > 0)
            {
                clean = clean.Substring(0, paragraphEnd).Trim();
            }
            return clean.Length > SummaryLength ? clean.Substring(0, SummaryLength - 1).TrimEnd() + "…" : clean;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            return IconChars.Replace(MarkupTag.Replace(text, ""), "").Trim();
        }

        private static void LogOnce(Exception e)
        {
            if (e.Message != _lastError)
            {
                _lastError = e.Message;
                _log.LogWarning($"Token tracking failed: {e}");
            }
        }
    }
}
