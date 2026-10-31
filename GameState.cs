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

    // Read-only snapshot of the live game, rebuilt on a timer so OnGUI never touches game objects.
    public class GameSnapshot
    {
        public bool InAdventure;
        public string AdventureName;
        public bool IsBattleMap;
        public int Round;
        public GamePhases Phase;
        public int Threat;
        public int ThreatMax;
        public bool IsDarkness;
        public readonly List<int> ThreatThresholds = new List<int>();
        public readonly List<EnemyRow> Enemies = new List<EnemyRow>();

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
            snap.AdventureName = Localize(adventure.Model.NameKey);
            snap.IsBattleMap = adventure.Type == AdventureTypes.Battleboard;
            snap.Phase = controller.CurrentPhase;
            snap.Round = vars.Round;
            snap.Threat = vars.AdventureThreat;
            snap.ThreatMax = adventure.ThreatMax;
            snap.IsDarkness = vars.IsDarkness;

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

            return snap;
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

        private static string Localize(string key)
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
