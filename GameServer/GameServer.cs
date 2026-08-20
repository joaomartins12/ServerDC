using System;
using System.Collections.Generic;
using System.IO;
using GameServer.Database;
using GameServer.Util;
using Shared;
using Shared.Network;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer
{
    public class GameServer : ServerMain
    {
        public static readonly GameServer Instance = new GameServer();
        public static GameChatCommands ChatCommands = new GameChatCommands();
        private bool _running;

        private GameServer()
        {
        }

        public DefaultServer Server { get; set; }
        public GameDatabase Database { get; private set; }
        public GameConf Config { get; set; }

        public void Run()
        {
            if (_running)
                throw new Exception("Server is already running.");

            var watch = System.Diagnostics.Stopwatch.StartNew();

            int x, y, width, height;
            Win32.GetWindowPosition(out x, out y, out width, out height);
            Win32.SetWindowPosition(width + 5, 0, width, height);

            ConsoleUtil.WriteHeader($"Game Server ({Shared.Util.Version.GetVersion()})", ConsoleColor.DarkGreen);
            ConsoleUtil.LoadingTitle();

            Log.Info("Server startup requested");
            Log.Info($"Server Version {Shared.Util.Version.GetVersion()}");

            NavigateToRoot();
            LoadConf(Config = new GameConf());
            InitDatabase(Database = new GameDatabase(), Config);

            Log.Info("Loading Vehicles..");
            if (File.Exists("system/data/Vehicles.xml"))
            {
                try
                {
                    Vehicles = GameData.LoadVehicleData("system/data/vehicles.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Vehicle Data corrupt");
#else
                    throw;
#endif
                }
            }

            Log.Info("Loading VShop Items..");
            if (File.Exists("system/data/VShopItems.xml"))
            {
                try
                {
                    VisualItems = GameData.LoadVShopItems("system/data/VShopItems.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("VShop Items corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("VShopItem data not found!");
            }
            Log.Info("VShop Items loaded with {0:D} entries", VisualItems.Count);

            Log.Info("Loading Quest Table");
            if (File.Exists("system/data/Quests.xml"))
            {
                try
                {
                    Quests = GameData.LoadQuests("system/data/Quests.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Quest data corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("Quest data not found!");
            }
            Log.Info("Quest Table loaded with {0:D} entries", Quests.Count);

            Log.Info("Loading Item Table");
            if (File.Exists("system/data/Items.xml"))
            {
                try
                {
                    Items = GameData.LoadItems("system/data/Items.xml", "system/data/UseItems.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Items data corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("Items data not found!");
            }
            Log.Info("Item Table loaded with {0:D} entries", Items.Count);

            var reader = new TdfReader();
            if (reader.Load("system/data/LevelServer.tdf"))
            {
                Log.Debug("Loading Exp Table");
                LevelTable = XiExpTable.LoadFromTdf(reader);
                if (LevelTable.Count == 0) throw new InvalidDataException("LevelTable corrupt!");
                Log.Debug("Exp Table Initialized with {0:D} rows.", LevelTable.Count);
            }
            else
            {
                Log.Debug("Exp Table Load failed.");
            }

            GameDataCatalogExporter.Export(Items, VisualItems, Vehicles, Quests, LevelTable);
            ItemCatalogJsonExporter.Export(Items);
            VehicleCatalogJsonExporter.Export(Vehicles);
            VehicleKeyResearchExporter.Export(Vehicles, Items);
            Log.Info("ItemCatalog.json, VehicleCatalog.json and VehicleKeyResearch.csv ready in Logs\\Catalogs.");

            Server = new DefaultServer(Config.Game.Port);
            Server.Start();

            ConsoleUtil.RunningTitle();
            _running = true;

            watch.Stop();
            Log.Info("Ready after {0}ms", watch.ElapsedMilliseconds);

            var commands = new GameConsoleCommands();
            commands.Wait();
        }
    }
}
