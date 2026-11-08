using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace JiMEOverlay
{
    // Converts the game's text for display in the overlay: NGUI [b]/[i] tags become IMGUI rich text,
    // and the game's icon characters (which the overlay's font can't draw) become words, e.g. "test Wit; 3".
    public static class GameText
    {
        private static readonly Regex MarkupTag = new Regex(@"\[/?[a-zA-Z0-9-]{0,8}\]", RegexOptions.Compiled);
        private static readonly Regex IconChar = new Regex(@"[-]", RegexOptions.Compiled);

        // Readable names where the GLYPH_ key suffix isn't one; empty means drop the icon.
        private static readonly Dictionary<string, string> NameOverrides = new Dictionary<string, string>
        {
            { "ACTION", "" },
            { "DOUBLE_HAND", "two-handed" },
            { "SINGLE_HAND", "one-handed" },
            { "REVEAL_CARD_DRAW", "reveal" },
        };

        private static Dictionary<char, string> _iconNames;

        // Plain text: tags removed, icons left as they are (for matching and length limits).
        public static string StripTags(string text) => string.IsNullOrEmpty(text) ? "" : MarkupTag.Replace(text, "");

        // Text ready for an overlay label: rich text bold/italic, other tags removed, icons named unless the
        // caller's font can draw them.
        public static string ToOverlay(string text, bool nameIcons = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var rich = MarkupTag.Replace(text.Replace("[b]", "<b>").Replace("[/b]", "</b>").Replace("[i]", "<i>").Replace("[/i]", "</i>"), "");
            return (nameIcons ? NameIcons(rich) : rich).Trim();
        }

        // Replaces icon characters with their names; keepIcon can keep the ones a font is able to draw.
        public static string NameIcons(string text, Func<char, bool> keepIcon = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var names = IconNames();
            var result = IconChar.Replace(text, m =>
                keepIcon != null && keepIcon(m.Value[0]) ? m.Value
                : names.TryGetValue(m.Value[0], out var name) && name.Length > 0 ? $"<b>{name}</b>"
                : "");
            // Dropped icons can leave doubled or leading spaces.
            result = Regex.Replace(result, " {2,}", " ");
            return Regex.Replace(result, @"(?m)^ +", "");
        }

        // Built once from the game's GLYPH_* keys, so the names follow the game's own data.
        private static Dictionary<char, string> IconNames()
        {
            if (_iconNames != null)
            {
                return _iconNames;
            }
            var names = new Dictionary<char, string>();
            var dictionary = PromptExport.DictionaryRef();
            if (dictionary == null)
            {
                return names; // not loaded yet; try again next time
            }
            foreach (var pair in dictionary)
            {
                if (!pair.Key.StartsWith("GLYPH_") || pair.Value == null || pair.Value.Length == 0 || string.IsNullOrEmpty(pair.Value[0]))
                {
                    continue;
                }
                var icon = pair.Value[0].Trim();
                if (icon.Length != 1 || icon[0] < '' || icon[0] > '')
                {
                    continue;
                }
                var suffix = pair.Key.Substring("GLYPH_".Length);
                names[icon[0]] = NameOverrides.TryGetValue(suffix, out var name) ? name
                    : suffix.StartsWith("JME") ? ""
                    : TitleCase(suffix);
            }
            _iconNames = names;
            return names;
        }

        private static string TitleCase(string suffix)
        {
            var sb = new StringBuilder();
            foreach (var word in suffix.Split('_'))
            {
                if (word.Length == 0)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(char.ToUpperInvariant(word[0])).Append(word.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }
    }
}
