﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using DeathrunRemade.Components;
using DeathrunRemade.Components.RunStatsUI;
using DeathrunRemade.Configuration;
using DeathrunRemade.Handlers;
using DeathrunRemade.Items;
using DeathrunRemade.Objects;
using DeathrunRemade.Objects.Enums;
using DeathrunRemade.Patches;
using HarmonyLib;
using HootLib;
using HootLib.Components;
using HootLib.Objects;
using Nautilus.Handlers;
using Nautilus.Utility;
using Newtonsoft.Json;
using UnityEngine;
using ILogHandler = HootLib.Interfaces.ILogHandler;

namespace DeathrunRemade
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.snmodding.nautilus", "1.0")]
    internal class DeathrunInit : BaseUnityPlugin
    {
        public const string GUID = "com.github.tinyhoot.DeathrunRemade";
        public const string NAME = "Deathrun Remade";
        public const string VERSION = "0.1";

        internal static DeathrunInit _Instance;
        internal static Config _Config;
        internal static ILogHandler _Log;
        internal static NotificationHandler _Notifications;
        internal static RunHandler _RunHandler;
        internal static TutorialHandler _Tutorials;
        internal static SafeDepthHud _DepthHud;
        private VanillaRecipeChanges _recipeChanges;
        
        // The base object from which the main menu highscores window is instantiated.
        private GameObject _baseStatsWindow;
        // The object that is actually active.
        internal static RunStatsWindow _RunStatsWindow;

        // Run Update() once per second.
        private const float UpdateInterval = 1f;
        private Hootimer _updateTimer;
        private Harmony _harmony;

        private void Awake()
        {
            _Log = new HootLogger(NAME);
            _Log.Info($"{NAME} v{VERSION} starting up.");

            _updateTimer = new Hootimer(() => Time.deltaTime, UpdateInterval);
            
            // Registering config.
            _Config = new Config(Hootils.GetConfigFilePath(NAME), Info.Metadata);
            _Config.RegisterModOptions(NAME, transform);

            // Register the in-game save game of the current run.
            SaveData.Main = SaveDataHandler.RegisterSaveDataCache<SaveData>();
            
            InitHandlers();
            LoadFiles();
            SetupCraftTree();
            RegisterItems();
            RegisterCommands();
            RegisterGameEvents();
            
            // Set up all the harmony patching.
            _harmony = new Harmony(GUID);
            HarmonyPatching(_harmony);
            SaveData.OnSaveDataLoaded += HarmonyPatchingDelayed;

            _Log.Info("Finished loading.");
        }

        private void Update()
        {
            // Only run this method every so often.
            if (!_updateTimer.Tick())
                return;
            
            // Putting these things here prevents having to run them as MonoBehaviours too.
            _Notifications.Update();
        }

        /// <summary>
        /// Execute all harmony patches that need to run every time regardless of which config options were chosen.
        /// </summary>
        private void HarmonyPatching(Harmony harmony)
        {
            // Wow I really wish HarmonyX would update their fork with PatchCategories
            harmony.PatchAll(typeof(GameEventHandler));
            harmony.PatchAll(typeof(BatteryPatcher));
            harmony.PatchAll(typeof(CauseOfDeathPatcher));
            harmony.PatchAll(typeof(CompassPatcher));
            harmony.PatchAll(typeof(EscapePodPatcher));
            harmony.PatchAll(typeof(ExplosionPatcher));
            harmony.PatchAll(typeof(FilterPumpPatcher));
            harmony.PatchAll(typeof(FoodChallengePatcher));
            harmony.PatchAll(typeof(RadiationPatcher));
            harmony.PatchAll(typeof(RunStatsTracker));
            harmony.PatchAll(typeof(SuitPatcher));
            harmony.PatchAll(typeof(WaterMurkPatcher));
        }
        
        /// <summary>
        /// Execute all harmony patches that should only be applied with the right config options enabled. For that
        /// reason, they must be delayed until the game loads and the config is locked in.
        ///
        /// Some of these could totally run at the beginning of the game too, but why patch things when there's no
        /// need to?
        /// </summary>
        private void HarmonyPatchingDelayed(SaveData save)
        {
            ConfigSave config = save.Config;
            if (config.CreatureAggression != Difficulty4.Normal)
                _harmony.PatchAll(typeof(AggressionPatcher));
            if (config.DamageTaken != DamageDifficulty.Normal)
                _harmony.PatchAll(typeof(DamageTakenPatcher));
            if (config.SurfaceAir != Difficulty3.Normal)
                _harmony.PatchAll(typeof(AirPatcher));
            if (config.PowerCosts != Difficulty4.Normal)
                _harmony.PatchAll(typeof(PowerPatcher));
            if (config.PacifistChallenge)
                _harmony.PatchAll(typeof(PacifistPatcher));
        }

        /// <summary>
        /// Do all the necessary work to get the mod going which can only be done when the game is done loading and
        /// ready to play.
        /// </summary>
        /// <param name="player">A freshly awoken player instance.</param>
        private void InGameSetup(Player player)
        {
            ConfigSave config = SaveData.Main.Config;
            
            // Enable the tracker which updates all run statistics.
            player.gameObject.AddComponent<RunStatsTracker>();
            // Set up GUI components.
            RadiationPatcher.CalculateGuiPosition();
            
            // Enable crush depth if the player needs to breathe, i.e. is not in creative mode.
            if (config.PersonalCrushDepth != Difficulty3.Normal && GameModeUtils.RequiresOxygen())
                player.tookBreathEvent.AddHandler(this, CrushDepthHandler.CrushPlayer);
            // Decrease the free health provided on respawn.
            if (config.DamageTaken != DamageDifficulty.Normal && player.liveMixin)
                player.playerRespawnEvent.AddHandler(this, DamageTakenPatcher.DecreaseRespawnHealth);
            // Nitrogen and its UI if required by config and game mode settings.
            if (config.NitrogenBends != Difficulty3.Normal && GameModeUtils.RequiresOxygen())
            {
                HootHudBar.Create<NitrogenBar>("NitrogenBar", -45, out GameObject _);
                _DepthHud = SafeDepthHud.Create(out GameObject _);
                player.gameObject.AddComponent<NitrogenHandler>();
            }

            // Deal with any recipe changes.
            _recipeChanges.RegisterFragmentChanges(config);
            _recipeChanges.RegisterRecipeChanges(config);
        }

        private void InitHandlers()
        {
            _Notifications = new NotificationHandler(_Log);
            // Load statistics of all runs ever played.
            _RunHandler = new RunHandler(_Log);
            _Tutorials = new TutorialHandler(_Notifications, SaveData.Main);
        }

        /// <summary>
        /// Load any files or assets the mod needs in order to run.
        /// </summary>
        private void LoadFiles()
        {
            _Log.Debug("Loading files...");
            _recipeChanges = new VanillaRecipeChanges();
            // Ignore a compiler warning.
            _ = _recipeChanges.LoadFromDiskAsync();
            
            // Load the assets for the highscore window. This was prepared in the unity editor.
            _Log.Debug("Loading assets...");
            AssetBundle bundle = AssetBundleLoadingUtils.LoadFromAssetsFolder(Hootils.GetAssembly(), "highscores");
            _baseStatsWindow = bundle.LoadAsset<GameObject>("Highscores");
            _baseStatsWindow.SetActive(false);

            _Log.Debug("Assets loaded.");
        }

        private void RegisterCommands()
        {
            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("loc", DumpLocation);
            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("test", TestMe);
        }

        private void RegisterGameEvents()
        {
            GameEventHandler.RegisterEvents();
            // Initialise deathrun messaging as soon as uGUI_Main is ready, i.e. the main menu loads.
            GameEventHandler.OnMainMenuLoaded += _Notifications.OnMainMenuLoaded;
            // Ensure the highscore window is always ready to go.
            GameEventHandler.OnMainMenuLoaded += () =>
            {
                var window = Instantiate(_baseStatsWindow, uGUI_MainMenu.main.transform, false);
                _RunStatsWindow = window.GetComponent<RunStatsWindow>();
                var option = uGUI_MainMenu.main.primaryOptions.gameObject.AddComponent<MainMenuCustomPrimaryOption>();
                option.onClick.AddListener(window.GetComponent<MainMenuCustomWindow>().Open);
                option.SetText("Deathrun Stats");
                // Put this new option in the right place - just after the options menu button.
                int index = uGUI_MainMenu.main.primaryOptions.transform.Find("PrimaryOptions/MenuButtons/ButtonOptions").GetSiblingIndex();
                option.SetIndex(index + 1);
            };
            GameEventHandler.OnPlayerAwake += InGameSetup;
            GameEventHandler.OnSavedGameLoaded += EscapePodPatcher.OnSavedGameLoaded;
        }

        /// <summary>
        /// Register all custom items added by this mod.
        /// </summary>
        private void RegisterItems()
        {
            // Not convinced I'm keeping this list but let's have it ready for now.
            List<DeathrunPrefabBase> prefabs = new List<DeathrunPrefabBase>();
            // Very basic items first, so later items can rely on them for recipes.
            prefabs.Add(new MobDrop(MobDrop.Variant.LavaLizardScale));
            prefabs.Add(new MobDrop(MobDrop.Variant.SpineEelScale));
            prefabs.Add(new MobDrop(MobDrop.Variant.ThermophileSample));
            
            prefabs.Add(new AcidBattery(_Config.BatteryCapacity.Value));
            prefabs.Add(new AcidPowerCell(_Config.BatteryCapacity.Value));
            prefabs.Add(new DecompressionModule());
            prefabs.Add(new FilterChip());
            prefabs.Add(new Suit(Suit.Variant.ReinforcedFiltrationSuit));
            prefabs.Add(new Suit(Suit.Variant.ReinforcedSuitMk2));
            prefabs.Add(new Suit(Suit.Variant.ReinforcedSuitMk3));
            prefabs.Add(new Tank(Tank.Variant.ChemosynthesisTank));
            prefabs.Add(new Tank(Tank.Variant.PhotosynthesisTank));
            prefabs.Add(new Tank(Tank.Variant.PhotosynthesisTankSmall));
        }

        /// <summary>
        /// Add all the new nodes to the craft tree.
        /// </summary>
        private void SetupCraftTree()
        {
            Atlas.Sprite suitIcon = Hootils.LoadSprite("SuitTabIcon.png", true);
            Atlas.Sprite tankIcon = Hootils.LoadSprite("TankTabIcon.png", true);

            CraftTreeHandler.AddTabNode(CraftTree.Type.Workbench, Constants.WorkbenchSuitTab,
                "Dive Suit Upgrades", suitIcon);
            CraftTreeHandler.AddTabNode(CraftTree.Type.Workbench, Constants.WorkbenchTankTab,
                "Specialty O2 Tanks", tankIcon);
        }

        private void DumpLocation()
        {
            _Log.Info($"Current location: {Player.main.transform.position}");
        }

        private void TestMe()
        {
            
            return;
            // FMODAsset asset = AudioUtils.GetFmodAsset("event:/sub/cyclops/impact_solid_hard");
            // FMODUWE.PlayOneShot(asset, Player.main.transform.position);
            // RESULT result = FMODUWE.GetEventInstance(asset.path, out EventInstance instance);

            VanillaRecipeChanges recipe = new VanillaRecipeChanges();
            // foreach (var c in recipe.LoadFromDiskBetter())
            // {
            //     _Log.Debug($"{c._techType}: {c._ingredients}");
            // }

            recipe.LoadFromDiskAsync().Start();
            var data = recipe.GetCraftData(Difficulty4.Deathrun);
            var x = data.ToList();
            //_Log.Debug(JsonConvert.SerializeObject(data));
            // _Log.Debug("Done.");

            var settings = JsonConvert.DefaultSettings?.Invoke() ?? new JsonSerializerSettings();
            // var enumconverter = new StringEnumConverter();
            // enumconverter.NamingStrategy ??= new DefaultNamingStrategy();
            // enumconverter.NamingStrategy.ProcessDictionaryKeys = true;
            // settings.Converters.Add(enumconverter);
            settings.NullValueHandling = NullValueHandling.Include;

            // using StreamReader reader = new StreamReader(Hootils.GetAssetHandle("test2.json"));
            // var text = reader.ReadToEnd();
            // var x = JsonConvert.DeserializeObject<List<SerialTechData>>(text);

            string json = JsonConvert.SerializeObject(
                new Dictionary<string, List<SerialTechData>> { { "difficulty", x } }, Formatting.Indented, settings);
            using StreamWriter writer = new StreamWriter(Hootils.GetAssetHandle($"yolo.json"));
            writer.Write(json);
        }
    }
}