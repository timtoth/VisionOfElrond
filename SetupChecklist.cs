using System.Collections.Generic;

namespace JiMEOverlay
{
    public class ChecklistItem
    {
        public string Id;
        public string Text;
    }

    // "Round 0" setup checklist for the start of an adventure.
    // Items are rebuilt from the latest snapshot (heroes, roles, titles), while ticks are kept by item id
    // and reset whenever a different adventure or save slot is loaded.
    public class SetupChecklist
    {
        private readonly HashSet<string> _checked = new HashSet<string>();
        private string _adventureKey;

        public readonly List<ChecklistItem> Items = new List<ChecklistItem>();
        public bool IsDismissed;

        public bool IsComplete
        {
            get
            {
                if (IsDismissed)
                {
                    return true;
                }
                foreach (var item in Items)
                {
                    if (!_checked.Contains(item.Id))
                    {
                        return false;
                    }
                }
                return Items.Count > 0;
            }
        }

        public bool IsChecked(ChecklistItem item) => _checked.Contains(item.Id);

        public void SetChecked(ChecklistItem item, bool value)
        {
            if (value)
            {
                _checked.Add(item.Id);
            }
            else
            {
                _checked.Remove(item.Id);
            }
        }

        public void Sync(GameSnapshot snap, string extraItems)
        {
            if (snap.AdventureKey != _adventureKey)
            {
                _adventureKey = snap.AdventureKey;
                _checked.Clear();
                IsDismissed = false;
            }

            Items.Clear();
            for (int i = 0; i < snap.Heroes.Count; i++)
            {
                var hero = snap.Heroes[i];
                var role = string.IsNullOrEmpty(hero.Role) ? "chosen" : hero.Role;
                Add($"role:{i}", $"{hero.Name}: take the {role} role card");
                Add($"deck:{i}", $"{hero.Name}: build deck from basic cards and {role} skill cards");
                if (hero.Titles.Count > 0)
                {
                    Add($"titles:{i}", $"{hero.Name}: re-add titles ({string.Join(", ", hero.Titles)})");
                }
            }
            Add("items", "Give each hero their weapon, armor and item cards");
            Add("shuffle", "Shuffle each hero's deck");
            Add("board", snap.IsBattleMap
                ? "Set up the battle map boards, terrain and heroes the app shows"
                : "Place the starting tiles, heroes and tokens the app shows");

            if (!string.IsNullOrEmpty(extraItems))
            {
                var extras = extraItems.Split('|');
                for (int i = 0; i < extras.Length; i++)
                {
                    var text = extras[i].Trim();
                    if (text.Length > 0)
                    {
                        Add($"extra:{i}", text);
                    }
                }
            }
        }

        private void Add(string id, string text)
        {
            Items.Add(new ChecklistItem { Id = id, Text = text });
        }
    }
}
