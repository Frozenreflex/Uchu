using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Timers;
using RakDotNet;
using RakDotNet.IO;
using Uchu.Core;
using Uchu.World.Parsers;

namespace Uchu.World
{
    public class Zone : Object
    {
        //
        // Consts
        //
        
        private const int TicksPerSecondLimit = 20;

        //
        // Zone info
        //
        
        public readonly uint CloneId;
        public readonly ushort InstanceId;
        public readonly ZoneInfo ZoneInfo;
        public new readonly Server Server;
        public bool Loaded { get; private set; }

        //
        // Managed objects
        //
        
        internal readonly List<GameObject> ManagedGameObjects = new List<GameObject>();
        internal readonly List<Object> ManagedObjects = new List<Object>();
        internal readonly List<Player> ManagedPlayers = new List<Player>();
        
        //
        // Macro properties
        //
        
        public Object[] Objects => ManagedObjects.ToArray();
        public GameObject[] GameObjects => ManagedGameObjects.ToArray();
        public Player[] Players => ManagedPlayers.ToArray();
        public ZoneId ZoneId => (ZoneId) ZoneInfo.ZoneId;
        
        //
        // Runtime
        //
        
        public float DeltaTime { get; private set; }
        private long _passedTickTime;
        private bool _running;
        private int _ticks;
        private readonly ScriptManager _scriptManager;
        
        //
        // Events
        //
        
        public readonly AsyncEvent<Player> OnPlayerLoad = new AsyncEvent<Player>();

        public Zone(ZoneInfo zoneInfo, Server server, ushort instanceId = default, uint cloneId = default)
        {
            Zone = this;
            ZoneInfo = zoneInfo;
            Server = server;
            InstanceId = instanceId;
            CloneId = cloneId;
            _scriptManager = new ScriptManager(this);

            OnStart.AddListener(async () => await InitializeAsync());
            
            OnDestroyed.AddListener(() => { _running = false; });
        }

        #region Initializing

        public async Task InitializeAsync()
        {
            var objects = ZoneInfo.ScenesInfo.SelectMany(s => s.Objects).ToArray();

            Logger.Information($"Loading {objects.Length} objects for {ZoneId}");

            foreach (var levelObject in objects)
            {
                try
                {
                    SpawnLevelObject(levelObject);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }

            foreach (var path in ZoneInfo.Paths.OfType<SpawnerPath>())
            {
                try
                {
                    SpawnPath(path);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }

            Logger.Information($"Loaded {objects.Length} objects for {ZoneId}");

            //
            // Load zone scripts
            //
            
            await _scriptManager.LoadScripts();
            
            Loaded = true;

            var _ = ExecuteUpdateAsync();
        }

        private void SpawnLevelObject(LevelObject levelObject)
        {
            var obj = GameObject.Instantiate(levelObject, this);

            SpawnerComponent spawner = default;
                
            if (levelObject.Settings.TryGetValue("loadSrvrOnly", out var serverOnly) && (bool) serverOnly ||
                levelObject.Settings.TryGetValue("carver_only", out var carverOnly) && (bool) carverOnly ||
                levelObject.Settings.TryGetValue("renderDisabled", out var disabled) && (bool) disabled)
            {
                obj.Layer = Layer.Hidden;
            }
            else if (!obj.TryGetComponent(out spawner))
            {
                obj.Layer = Layer.Hidden;
            }

            Start(obj);

            if (obj.Layer == Layer.Hidden)
            {
                return;
            }

            //
            // Only spawns should get constructed on the client.
            //
            
            if (spawner == default)
            {
                return;
            }
            
            GameObject.Construct(spawner.Spawn());
        }

        private void SpawnPath(SpawnerPath spawnerPath)
        {
            var obj = InstancingUtil.Spawner(spawnerPath, this);

            Start(obj);

            var spawn = obj.GetComponent<SpawnerComponent>().Spawn();

            GameObject.Construct(spawn);
        }

        #endregion

        #region Messages
        
        public void SelectiveMessage(IGameMessage message, IEnumerable<Player> players)
        {
            foreach (var player in players) player.Message(message);
        }

        public void ExcludingMessage(IGameMessage message, Player excluded)
        {
            foreach (var player in ManagedPlayers.Where(p => p != excluded)) player.Message(message);
        }

        public void BroadcastMessage(IGameMessage message)
        {
            foreach (var player in ManagedPlayers) player.Message(message);
        }

        #endregion
        
        #region Object Finder

        public GameObject GetGameObject(long objectId)
        {
            return objectId == -1 ? null : ManagedGameObjects.First(o => o.ObjectId == objectId);
        }

        public bool TryGetGameObject(long objectId, out GameObject result)
        {
            result = ManagedGameObjects.FirstOrDefault(o => o.ObjectId == objectId);
            return result != default;
        }

        public T GetGameObject<T>(long objectId) where T : GameObject
        {
            return ManagedGameObjects.OfType<T>().First(o => o.ObjectId == objectId);
        }

        public bool TryGetGameObject<T>(long objectId, out T result) where T : GameObject
        {
            result = ManagedGameObjects.OfType<T>().FirstOrDefault(o => o.ObjectId == objectId);
            return result != default;
        }
        
        #endregion

        #region Object Mangement

        #region Register

        internal async Task RegisterPlayer(Player player)
        {
            ManagedPlayers.Add(player);

            await OnPlayerLoad.InvokeAsync(player);

            foreach (var gameObject in GameObjects)
            {
                if (gameObject.GetType().GetCustomAttribute<UnconstructedAttribute>() != null) continue;

                SendConstruction(gameObject, new[] {player});
            }
        }

        internal void UnregisterObject(Object obj)
        {
            if (ManagedObjects.Contains(obj)) ManagedObjects.Remove(obj);
        }

        internal void UnregisterGameObject(GameObject gameObject)
        {
            UnregisterObject(gameObject);

            ManagedGameObjects.Remove(gameObject);
        }

        #endregion

        #region Networking

        internal static void SendConstruction(GameObject gameObject, Player recipient)
        {
            SendConstruction(gameObject, new[] {recipient});
        }

        internal static void SendConstruction(GameObject gameObject, IEnumerable<Player> recipients)
        {
            foreach (var recipient in recipients)
            {
                recipient.Perspective.Reveal(gameObject, id =>
                {
                    using var stream = new MemoryStream();
                    using var writer = new BitWriter(stream);
                    
                    writer.Write((byte) MessageIdentifier.ReplicaManagerConstruction);

                    writer.WriteBit(true);
                    writer.Write(id);

                    gameObject.WriteConstruct(writer);

                    recipient.Connection.Send(stream);
                });
            }
        }

        internal static void SendSerialization(GameObject gameObject, IEnumerable<Player> recipients)
        {
            foreach (var recipient in recipients)
            {
                if (!recipient.Perspective.TryGetNetworkId(gameObject, out var id)) continue;

                using var stream = new MemoryStream();
                using var writer = new BitWriter(stream);
                
                writer.Write((byte) MessageIdentifier.ReplicaManagerSerialize);

                writer.Write(id);

                gameObject.WriteSerialize(writer);

                recipient.Connection.Send(stream);
            }
        }

        internal static void SendDestruction(GameObject gameObject, Player player)
        {
            SendDestruction(gameObject, new[] {player});
        }

        internal static void SendDestruction(GameObject gameObject, IEnumerable<Player> recipients)
        {
            foreach (var recipient in recipients)
            {
                if (!recipient.Perspective.TryGetNetworkId(gameObject, out var id)) continue;

                using (var stream = new MemoryStream())
                {
                    using var writer = new BitWriter(stream);
                    
                    writer.Write((byte) MessageIdentifier.ReplicaManagerDestruction);

                    writer.Write(id);

                    recipient.Connection.Send(stream);
                }

                recipient.Perspective.Drop(gameObject);
            }
        }

        #endregion
        
        #endregion

        #region Runtime

        private Task ExecuteUpdateAsync()
        {
            var timer = new Timer
            {
                Interval = 1000,
                AutoReset = true
            };

            timer.Elapsed += (sender, args) =>
            {
                if (_ticks > 0)
                    Logger.Debug($"TPS: {_ticks}/{TicksPerSecondLimit} TPT: {_passedTickTime / _ticks} ms");
                _passedTickTime = 0;
                _ticks = 0;
                
                //
                // Set player count
                //

                var worldServer = (WorldServer) Server;

                worldServer.ActiveUserCount = (uint) worldServer.Zones.Sum(z => z.Players.Length);
            };

            timer.Start();

            return Task.Run(async () =>
            {
                var stopWatch = new Stopwatch();

                stopWatch.Start();

                _running = true;

                while (_running)
                {
                    if (_ticks >= TicksPerSecondLimit) continue;

                    await Task.Delay(1000 / TicksPerSecondLimit);

                    foreach (var obj in ManagedObjects)
                    {
                        try
                        {
                            Update(obj);
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e);
                        }
                    }

                    _ticks++;

                    var passedMs = stopWatch.ElapsedMilliseconds;

                    DeltaTime = passedMs / 1000f;

                    _passedTickTime += passedMs;

                    stopWatch.Restart();
                }
            });
        }
        
        #endregion
    }
}