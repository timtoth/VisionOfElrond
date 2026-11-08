using System;
using System.Collections.Generic;
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
                _previewCache.Clear();
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

        // What the token showed the last time it was clicked this session, if it has been.
        public static bool TryGetRecord(GameNode node, out string summary, out List<string> options)
        {
            summary = null;
            options = null;
            if (node == null || !_records.TryGetValue(node.GetInstanceID(), out var record))
            {
                return false;
            }
            summary = record.Summary;
            options = record.Options;
            return true;
        }

        public static string TokenTypeName(GameNode node) => node != null ? TypeName(node.Token3DType) : null;

        private const int PreviewDepth = 4; // states to follow after the click before giving up

        // The first prompt and choices a click on this token would show, read from its script without running it.
        // Cached per token and script state, so hovering doesn't re-walk the script every frame.
        public static bool TryPreview(GameNode node, out string summary, out List<string> options)
        {
            summary = null;
            options = null;
            try
            {
                var fsm = node != null && node.Fsm != null ? node.Fsm.Fsm : null;
                var state = fsm != null ? fsm.ActiveState : null;
                if (state == null)
                {
                    return false;
                }
                var cacheKey = $"{node.GetInstanceID()}:{state.Name}";
                if (_previewCache.TryGetValue(cacheKey, out var cached))
                {
                    summary = cached?.Summary;
                    options = cached?.Options;
                    return cached != null;
                }

                Record found = null;
                var next = FindTransition(state, "NODE CLICKED");
                for (int depth = 0; depth < PreviewDepth && next != null && found == null; depth++)
                {
                    var target = fsm.GetState(next);
                    if (target == null)
                    {
                        break;
                    }
                    found = ReadMessage(target);
                    next = found == null ? FindTransition(target, "FINISHED") : null;
                }
                _previewCache[cacheKey] = found;
                summary = found?.Summary;
                options = found?.Options;
                return found != null;
            }
            catch (Exception e)
            {
                LogOnce(e);
                return false;
            }
        }

        private static readonly Dictionary<string, Record> _previewCache = new Dictionary<string, Record>();

        private static string FindTransition(HutongGames.PlayMaker.FsmState state, string eventName)
        {
            if (state.Transitions == null)
            {
                return null;
            }
            foreach (var transition in state.Transitions)
            {
                if (transition != null && transition.EventName == eventName)
                {
                    return transition.ToState;
                }
            }
            return null;
        }

        private static Record ReadMessage(HutongGames.PlayMaker.FsmState state)
        {
            if (state.Actions == null)
            {
                return null;
            }
            foreach (var action in state.Actions)
            {
                if (!(action is FFG.JIME.Actions.DisplayMessageBase message) || message.Message == null || string.IsNullOrEmpty(message.Message.Key))
                {
                    continue;
                }
                var record = new Record { Summary = Summarize(GameSnapshot.Localize(message.Message.Key)) };
                if (action is FFG.JIME.Actions.DisplayMessageMultipleChoice choice && choice.Choices != null)
                {
                    foreach (var item in choice.Choices)
                    {
                        var label = item != null ? ResolvePacket(item.Localization) : null;
                        if (!string.IsNullOrEmpty(label))
                        {
                            record.Options.Add(label);
                        }
                    }
                }
                return record;
            }
            return null;
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

        // Tags removed; icons kept so whoever displays the text can draw or name them (see GameText).
        private static string Clean(string text) => GameText.StripTags(text).Trim();

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
