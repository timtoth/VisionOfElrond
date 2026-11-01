using System;
using System.Collections.Generic;
using FFG.JIME;

namespace JiMEOverlay
{
    public class EnemyRow
    {
        public string Name;
        public int UnitsRemaining;
        public int UnitStartCount;
        public int Health;
        public int TotalHealth;
        public int Armor;
        public int Sorcery;
        public bool IsExhausted;
        public bool IsElite;
    }

    public class HeroRow
    {
        public string Name;
        public string Role;
        public readonly List<string> Titles = new List<string>();
        public int Corruption;
        public bool IsDefeated;
        public int LastStandDifficulty;
    }

    // Read-only snapshot of the live game, rebuilt on a timer so OnGUI never touches game objects.
    public class GameSnapshot
    {
        public bool InAdventure;
        // Identifies this adventure in this save slot, so per-adventure overlay state can reset.
        public string AdventureKey;
        public string AdventureName;
        public bool IsBattleMap;
        public int Round;
        public GamePhases Phase;
        public int Threat;
        public int ThreatMax;
        public bool IsDarkness;
        public bool IsInLastStand;
        public readonly List<int> ThreatThresholds = new List<int>();
        public readonly List<EnemyRow> Enemies = new List<EnemyRow>();
        public readonly List<HeroRow> Heroes = new List<HeroRow>();
        public List<TokenRow> Tokens = new List<TokenRow>();

        public static GameSnapshot Capture()
        {
            var snap = new GameSnapshot();
            if (!SingletonBehaviour<GameController>.IsInitialized)
            {
                return snap;
            }

            var controller = SingletonBehaviour<GameController>.Instance;
            var adventure = controller != null ? controller.CurrentAdventure : null;
            if (adventure == null || adventure.Model == null)
            {
                return snap;
            }

            var vars = GameData.GlobalVariables;
            snap.InAdventure = true;
            var savedGame = GameData.Game != null ? GameData.Game.SerializedGame : null;
            snap.AdventureKey = $"{(savedGame != null ? savedGame.SlotGUID : "")}:{adventure.Model.Id}";
            snap.AdventureName = Localize(adventure.Model.NameKey);
            snap.IsBattleMap = adventure.Type == AdventureTypes.Battleboard;
            snap.Phase = controller.CurrentPhase;
            snap.Round = vars.Round;
            snap.Threat = vars.AdventureThreat;
            snap.ThreatMax = adventure.ThreatMax;
            snap.IsDarkness = vars.IsDarkness;
            snap.IsInLastStand = controller.IsInLastStand;

            if (adventure.ActiveThreatEvents != null)
            {
                foreach (var threatEvent in adventure.ActiveThreatEvents)
                {
                    if (threatEvent != null && threatEvent.Threshold > snap.Threat)
                    {
                        snap.ThreatThresholds.Add(threatEvent.Threshold);
                    }
                }
                snap.ThreatThresholds.Sort();
            }

            if (adventure.EnemiesInPlay != null)
            {
                foreach (var group in adventure.EnemiesInPlay)
                {
                    if (group == null || group.Model == null)
                    {
                        continue;
                    }
                    snap.Enemies.Add(ReadEnemy(group));
                }
            }

            snap.Tokens = TokenTracker.Capture(adventure, snap.AdventureKey);

            var gameData = SingletonBehaviour<GameData>.IsInitialized ? SingletonBehaviour<GameData>.Instance : null;
            var players = gameData != null ? gameData.Players : null;
            if (players != null)
            {
                foreach (var hero in players)
                {
                    if (hero == null || hero.Model == null)
                    {
                        continue;
                    }
                    snap.Heroes.Add(ReadHero(gameData, hero));
                }
            }

            return snap;
        }

        private static HeroRow ReadHero(GameData gameData, Hero hero)
        {
            var row = new HeroRow
            {
                Name = Localize(hero.Model.NameKey),
                Corruption = hero.Corruption,
                IsDefeated = hero.Status == HeroStatus.Defeated,
                LastStandDifficulty = hero.LastStandDifficulty,
            };

            var role = UserCollectionManager.GetRole(gameData.GetHeroRoleId(hero.Index), false);
            row.Role = role != null ? Localize(role.NameKey) : "";

            var titleIds = gameData.GetUnlockedTitleIds(hero.Index);
            if (titleIds != null)
            {
                foreach (var titleId in titleIds)
                {
                    var title = UserCollectionManager.GetTitle(titleId, false);
                    if (title != null)
                    {
                        row.Titles.Add(Localize(title.NameKey));
                    }
                }
            }
            return row;
        }

        private static EnemyRow ReadEnemy(EnemyGroup group)
        {
            var row = new EnemyRow
            {
                Name = group.GetEnemyName(null, false, false),
                UnitsRemaining = group.UnitsRemaining,
                UnitStartCount = group.UnitStartCount,
                Health = group.CurrentHealth,
                TotalHealth = group.TotalHealth,
                IsExhausted = group.IsExhausted,
                IsElite = group.IsElite,
            };

            // Armor and sorcery are tracked per unit; show the first unit still standing.
            for (int i = 0; i < row.UnitStartCount; i++)
            {
                if (group.GetUnitHealth(i) > 0)
                {
                    row.Armor = group.GetUnitArmor(i);
                    row.Sorcery = group.GetUnitSorcery(i);
                    break;
                }
            }
            return row;
        }

        internal static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            try
            {
                return Localization.Get(key, true, 0uL);
            }
            catch (Exception)
            {
                return key;
            }
        }
    }
}
