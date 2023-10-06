using DeathrunRemade.Configuration;
using DeathrunRemade.Items;
using DeathrunRemade.Objects;
using DeathrunRemade.Objects.Enums;
using UnityEngine;

namespace DeathrunRemade.Handlers
{
    internal static class CrushDepthHandler
    {
        public const float InfiniteCrushDepth = 10000f;
        
        /// <summary>
        /// Do the math and check whether the player needs to take crush damage.
        ///
        /// This method is tied to the player taking breaths, so it runs every three seconds or so.
        /// </summary>
        public static void CrushPlayer(Player player)
        {
            // Only do this if the player is exposed to the elements.
            if (!player.IsUnderwater() || player.currentWaterPark != null)
                return;
            
            TechType suit = Inventory.main.equipment.GetTechTypeInSlot("Body");
            float crushDepth = GetCrushDepth(suit, SaveData.Main.Config);
            float diff = player.GetDepth() - crushDepth;
            // Not below the crush depth, do nothing.
            if (diff <= 0)
                return;

            // Show a warning before dealing damage.
            if (WarningHandler.ShowWarning(Warning.CrushDepth))
                return;
            
            // Fifty-fifty on whether you take damage this time.
            if (UnityEngine.Random.value < 0.5f)
                return;
            
            // At 50 depth, ^2 (4dmg). At 250 depth, ^6 (64dmg).
            // Together with the separate global damage multiplier, this gets quite punishing.
            float damageExp = 1f + Mathf.Clamp(diff / 50f, 1f, 5f);
            player.GetComponent<LiveMixin>().TakeDamage(Mathf.Pow(2f, damageExp), type: DamageType.Pressure);
            DeathrunInit._Log.InGameMessage("The pressure is crushing you!");
        }
        
        /// <summary>
        /// Get the crush depth of the provided suit based on config values.
        /// </summary>
        public static float GetCrushDepth(TechType suit, ConfigSave config)
        {
            bool deathrun = config.PersonalCrushDepth == Difficulty3.Deathrun;
            float depth = suit switch
            {
                TechType.RadiationSuit => 500f,
                TechType.ReinforcedDiveSuit => deathrun ? 800f : InfiniteCrushDepth,
                TechType.WaterFiltrationSuit => deathrun ? 800f : 1300f,
                _ => 200f,
            };
            // If the player wasn't wearing any of the vanilla suits, check for custom ones.
            depth = Mathf.Max(depth, Suit.GetCrushDepth(suit, deathrun));
            return depth;
        }
    }
}