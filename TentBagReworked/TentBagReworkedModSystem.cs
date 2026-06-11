using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;
using ProtoBuf;
using System.IO;
using TentBagReworked.Behaviors;
using TentBagReworked.Config;
using TentBagReworked.Items;
using TentBagReworked.Rendering;
using TentBagReworked;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using System.Collections.Generic;
using System;

namespace TentBagReworked
{
    public class TentBagReworkedModSystem : ModSystem
    {
        public static TentBagReworkedModSystem Instance { get; private set; } = null!;

        public ICoreAPI Api { get; private set; } = null!;
        public ICoreClientAPI clientApi { get; private set; } = null!;

        public ILogger Logger => Mod.Logger;
        public string ModId => Mod.Info.ModID;
        public TentBagReworkedConfig Config => _config ?? new TentBagReworkedConfig();

        /// <summary>
        /// Конфиг, присланный сервером клиенту (нужен предпросмотру голограммы)
        /// В одиночной игре заполняется на стыковке локального клиента; в чистом
        /// клиенте мультиплеера — единственный источник правды. Null до получения
        /// </summary>
        public static TentBagReworkedConfig? SyncedConfig;

        /// <summary>
        /// Конфиг, который должна использовать клиентская логика (предпросмотр/голограмма):
        /// присланный сервером, если есть, иначе локальный
        /// </summary>
        public TentBagReworkedConfig EffectiveConfig => SyncedConfig ?? Config;

        public static Dictionary<string, List<string>> PendingWaypointNames = new();

        public Dictionary<string, List<Guid>> SchematicHistory = new();

        internal static string ExportFolderPath = null!;
        private TentBagReworkedConfig? _config;
        private FileWatcher? _fileWatcher;
        private IServerNetworkChannel? _channel;
        private IClientNetworkChannel? _clientChannel;

        // Серверная часть предпросмотра (мини-измерения на игрока)
        private ICoreServerAPI? _sapi;
        private readonly Dictionary<string, IMiniDimension> _previewDims = new();
        private readonly Dictionary<string, int> _previewDimIds = new();
        private readonly Dictionary<string, string> _previewContents = new();

        public TentBagReworkedModSystem()
        {
            Instance = this;
        }

        public override void StartPre(ICoreAPI api)
        {
            Api = api;
        }

        public override void Start(ICoreAPI api)
        {
            api.RegisterCollectibleBehaviorClass("tentbagreworked.packable", typeof(PackableBehavior));
            api.RegisterItemClass("tentbagreworked.itemtentbag", typeof(ItemTentBag));

            ExportFolderPath = api.GetOrCreateDataPath("ModData/TentBagReworked");
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            clientApi = capi;

            // Типы пакетов регистрируются в одинаковом порядке на клиенте и сервере
            _clientChannel = clientApi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>()
                .RegisterMessageType<ConfigSyncPacket>()
                .RegisterMessageType<RequestTentPreviewPacket>()
                .RegisterMessageType<TentPreviewPacket>()
                .SetMessageHandler<ErrorPacket>(packet => {
                    if (!string.IsNullOrEmpty(packet.Error))
                    {
                        // Добавилась локализация сырого ключа на стороне клиента
                        string translatedText = Vintagestory.API.Config.Lang.Get(packet.Error);

                        // Изменился код ошибки с "error" на уникальный "tentbag-error"
                        clientApi.TriggerIngameError(this, "tentbag-error", translatedText);
                    }
                })
                .SetMessageHandler<ConfigSyncPacket>(OnConfigFromServer)
                .SetMessageHandler<TentPreviewPacket>(OnPreviewFromServer);
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _sapi = sapi;
            _channel = sapi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>()
                .RegisterMessageType<ConfigSyncPacket>()
                .RegisterMessageType<RequestTentPreviewPacket>()
                .RegisterMessageType<TentPreviewPacket>()
                .SetMessageHandler<RequestTentPreviewPacket>(OnRequestPreview);

            sapi.Event.PlayerJoin += OnPlayerJoin;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            ReloadConfig();

            EnsureTentSchematicExists(sapi);
        }

        private void OnPlayerJoin(IServerPlayer byPlayer)
        {
            SendConfig(byPlayer);
        }

        private void OnConfigFromServer(ConfigSyncPacket packet)
        {
            if (string.IsNullOrEmpty(packet.Json))
            {
                return;
            }

            try
            {
                SyncedConfig = JsonConvert.DeserializeObject<TentBagReworkedConfig>(packet.Json);
            }
            catch (Exception e)
            {
                Logger.Warning("[TentBagReworked] Failed to read synced config: " + e);
            }
        }

        public void SendConfig(IServerPlayer player)
        {
            if (_channel == null)
            {
                return;
            }

            try
            {
                _channel.SendPacket(new ConfigSyncPacket { Json = JsonConvert.SerializeObject(Config) }, player);
            }
            catch (Exception e)
            {
                Logger.Warning("[TentBagReworked] Failed to send config: " + e);
            }
        }

        //  Предпросмотр - лиент просит — сервер строит мини-измерение и шлёт dimId

        /// <summary>Клиент: попросить сервер включить/выключить превью палатки (и сообщить, на какой блок смотрим).</summary>
        public void SendPreviewRequest(bool enable, int x = 0, int y = 0, int z = 0, int dim = 0)
        {
            _clientChannel?.SendPacket(new RequestTentPreviewPacket { Enable = enable, X = x, Y = y, Z = z, Dim = dim });
        }

        /// <summary>Клиент: пришёл ответ сервера с id измерения и размерами схематика.</summary>
        private void OnPreviewFromServer(TentPreviewPacket packet)
        {
            TentHologramRenderer.PreviewInstance?.OnServerPreviewDim(packet.DimId, packet.PosX, packet.PosY, packet.PosZ);
        }

        /// <summary>Сервер: обработать запрос превью от игрока.</summary>
        private void OnRequestPreview(IServerPlayer fromPlayer, RequestTentPreviewPacket packet)
        {
            if (_sapi == null)
            {
                return;
            }

            if (!packet.Enable)
            {
                DestroyServerPreview(fromPlayer);
                return;
            }

            ItemStack? stack = fromPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            string? contents = stack?.Attributes?.GetString("packed-contents")
                               ?? stack?.Attributes?.GetString("tent-contents");
            if (string.IsNullOrEmpty(contents))
            {
                DestroyServerPreview(fromPlayer);
                return;
            }

            if (!EnsureServerPreview(fromPlayer.PlayerUID, contents, out IMiniDimension? dim, out int dimId, out int sizeX, out int sizeZ) || dim == null)
            {
                return;
            }

            // Позиция угла В МИРЕ (как WorldEdit: CurrentPos = мировой origin). Считаем на сервере,
            // чтобы plantRock брался из реального мира. ОБЯЗАНО совпадать с UnpackContents (BottomCenter).
            int tdim = packet.Dim;
            int plantRock = IsPlantOrRockServer(_sapi.World.BlockAccessor.GetBlock(new BlockPos(packet.X, packet.Y, packet.Z, tdim))) ? 1 : 0;
            BlockPos corner = new(packet.X - sizeX / 2, packet.Y + 1 - plantRock, packet.Z - sizeZ / 2, tdim);

            // Ставим измерение в мир рядом с игроком, иначе его чанки не стримятся клиенту.
            dim.CurrentPos.SetPos(corner);

            _channel?.SendPacket(new TentPreviewPacket { DimId = dimId, PosX = corner.X, PosY = corner.Y, PosZ = corner.Z }, fromPlayer);
        }

        /// <summary>
        /// Создаёт (один раз на игрока) мини-измерение и вставляет в него схематик.
        /// Повторная вставка только при смене содержимого. Чанки движок стримит клиенту сам.
        /// </summary>
        private bool EnsureServerPreview(string uid, string contents, out IMiniDimension? dim, out int dimId, out int sizeX, out int sizeZ)
        {
            dim = null;
            dimId = -1;
            sizeX = 0;
            sizeZ = 0;
            if (_sapi == null)
            {
                return false;
            }

            if (!_previewDims.TryGetValue(uid, out dim) || dim == null)
            {
                dim = _sapi.World.BlockAccessor.CreateMiniDimension(new Vec3d());
                int newId = _sapi.Server.LoadMiniDimension(dim);
                dim.SetSubDimensionId(newId);
                dim.BlocksPreviewSubDimension_Server = newId;
                _previewDims[uid] = dim;
                _previewDimIds[uid] = newId;
                _previewContents[uid] = "";
                Logger.VerboseDebug("[TentBagReworked] preview dim created for " + uid + ": id=" + newId);
            }

            dimId = _previewDimIds[uid];

            string error = "";
            BlockSchematic schematic = BlockSchematic.LoadFromString(contents, ref error);
            if (schematic?.BlockCodes == null || !string.IsNullOrEmpty(error))
            {
                Logger.Warning("[TentBagReworked] Preview schematic load failed: " + error);
                return false;
            }

            sizeX = schematic.SizeX;
            sizeZ = schematic.SizeZ;

            // Перевставляем блоки только если содержимое изменилось.
            if (_previewContents.GetValueOrDefault(uid) != contents)
            {
                dim.ClearChunks();

                // Локальный нулевой угол внутри измерения (как в WorldEditWorkspace.CreateDimensionFromSchematic):
                // (0,0,0) в координатном пространстве мини-измерений (dimension index 1) + сдвиг суб-измерения.
                BlockPos pasteOrigin = new(0, 0, 0, 1);
                dim.AdjustPosForSubDimension(pasteOrigin);

                // Init + Place + PlaceDecors (внутри PasteToMiniDimension). Place у не-revertable
                // аксессора сам создаёт блок-сущности (chisel/ящики), поэтому они рисуются корректно.
                schematic.PasteToMiniDimension(_sapi, _sapi.World.BlockAccessor, dim, pasteOrigin, true);
                dim.UnloadUnusedServerChunks();

                _previewContents[uid] = contents;
                Logger.VerboseDebug("[TentBagReworked] preview pasted for " + uid + ": " + sizeX + "x" + schematic.SizeY + "x" + sizeZ);
            }

            return true;
        }

        private bool IsPlantOrRockServer(Block? b) => Config.ReplacePlantsAndRocks && b?.Replaceable is >= 5500 and <= 6500;

        /// <summary>Сервер: спрятать превью игрока (чанки очищаем, измерение оставляем для повторного использования).</summary>
        private void DestroyServerPreview(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (_previewDims.TryGetValue(uid, out IMiniDimension? dim))
            {
                try
                {
                    dim.ClearChunks();
                    dim.UnloadUnusedServerChunks();
                }
                catch (Exception e)
                {
                    Logger.Warning("[TentBagReworked] Failed to clear preview: " + e);
                }
                _previewContents[uid] = "";
            }
            _channel?.SendPacket(new TentPreviewPacket { DimId = -1 }, player);
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (_previewDims.TryGetValue(uid, out IMiniDimension? dim))
            {
                try
                {
                    dim.ClearChunks();
                    dim.UnloadUnusedServerChunks();
                }
                catch { /* выход игрока — не критично */ }
            }
            _previewDims.Remove(uid);
            _previewDimIds.Remove(uid);
            _previewContents.Remove(uid);
        }


        public void SendClientError(IPlayer? player, string error)
        {
            if (player is IServerPlayer serverPlayer)
            {
                _channel?.SendPacket(new ErrorPacket { Error = error }, serverPlayer);
            }
        }

        public void SendClientChatMessage(IPlayer? player, string message)
        {
            if (player is IServerPlayer serverPlayer)
            {
                serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            }
        }

        public void ReloadConfig()
        {
            _config = Api.LoadModConfig<TentBagReworkedConfig>($"{ModId}.json") ?? new TentBagReworkedConfig();

            (_fileWatcher ??= new FileWatcher(this)).Queued = true;

            string json = JsonConvert.SerializeObject(_config, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            FileInfo fileInfo = new(Path.Combine(GamePaths.ModConfig, $"{ModId}.json"));
            GamePaths.EnsurePathExists(fileInfo.Directory!.FullName);
            File.WriteAllText(fileInfo.FullName, json);

            Api.Event.RegisterCallback(_ => _fileWatcher.Queued = false, 100);

            // Держим предпросмотр у всех подключённых клиентов в актуальном состоянии
            // при правках конфига на лету.
            if (Api is ICoreServerAPI && _channel != null)
            {
                try
                {
                    _channel.BroadcastPacket(new ConfigSyncPacket { Json = JsonConvert.SerializeObject(_config) });
                }
                catch (Exception e)
                {
                    Logger.Warning("[TentBagReworked] Failed to broadcast config: " + e);
                }
            }
        }

        public override void Dispose()
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;

            if (Api is ICoreServerAPI sapi)
            {
                sapi.Event.PlayerJoin -= OnPlayerJoin;
                sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;

                foreach (IMiniDimension dim in _previewDims.Values)
                {
                    try
                    {
                        dim.ClearChunks();
                        dim.UnloadUnusedServerChunks();
                    }
                    catch { /* выгрузка — не критично */ }
                }
            }

            _previewDims.Clear();
            _previewDimIds.Clear();
            _previewContents.Clear();

            SyncedConfig = null;
            _channel = null;
            _clientChannel = null;
            _sapi = null;
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class ErrorPacket
        {
            public string? Error;
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class ConfigSyncPacket
        {
            public string? Json;
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class RequestTentPreviewPacket
        {
            public bool Enable;
            public int X;
            public int Y;
            public int Z;
            public int Dim;
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class TentPreviewPacket
        {
            public int DimId;
            public int PosX;
            public int PosY;
            public int PosZ;
        }
        private void EnsureTentSchematicExists(ICoreServerAPI sapi)
        {
            // Получаем путь к стандартной папке WorldEdit: ....\VintagestoryData\WorldEdit
            string worldEditDir = Path.Combine(GamePaths.DataPath, "WorldEdit");

            // Убеждаемся, что папка WorldEdit существует (создаем, если нет)
            GamePaths.EnsurePathExists(worldEditDir);

            string targetFilePath = Path.Combine(worldEditDir, "tent.json");

            // Если есть — не трогаем
            if (!File.Exists(targetFilePath))
            {
                // Ищем файл внутри ассетов мода
                // Домен: "tentbagreworked", Путь: "config/tent.json"
                AssetLocation assetLocation = new AssetLocation("tentbagreworked", "config/tent.json");
                IAsset? asset = sapi.Assets.Get(assetLocation);

                if (asset != null)
                {
                    try
                    {
                        // asset.Data содержит массив байтов (byte[])
                        File.WriteAllBytes(targetFilePath, asset.Data);
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"[TentBagReworked] Fehler beim Kopieren von tent.json: {e.Message}");
                    }
                }
                else
                {
                    Logger.Warning("[TentBagReworked] Die ursprüngliche Datei „tent.json“ wurde in den Mod-Assets nicht gefunden!");
                }
            }
        }
    }
}