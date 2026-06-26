using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;
using ProtoBuf;
using System.IO;
using TentBagReworked.Behaviors;
using TentBagReworked.Config;
using TentBagReworked.Items;
using TentBagReworked.Util;
using TentBagReworked;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using System.Collections.Generic;
using System.Linq;
using System;

namespace TentBagReworked
{
    public class TentBagReworkedModSystem : ModSystem
    {
        public static TentBagReworkedModSystem Instance { get; private set; } = null!;

        public ICoreAPI Api { get; private set; } = null!;
        public ICoreClientAPI ClientApi { get; private set; } = null!;

        public ILogger Logger => Mod.Logger;
        public string ModId => Mod.Info.ModID;
        public TentBagReworkedConfig Config => _config ?? new TentBagReworkedConfig();

        /// <summary>
        /// Конфиг, присланный сервером клиенту (нужен предпросмотру голограммы)
        /// В одиночной игре заполняется на стыковке локального клиента; в чистом
        /// клиенте мультиплеера — единственный источник правды. Null до получения
        /// </summary>
        public static TentBagReworkedConfig? SyncedConfig { get; set; }

        /// <summary>
        /// Конфиг, который должна использовать клиентская логика (предпросмотр/голограмма):
        /// присланный сервером, если есть, иначе локальный
        /// </summary>
        public TentBagReworkedConfig EffectiveConfig => SyncedConfig ?? Config;

        public static Dictionary<string, List<string>> PendingWaypointNames { get; set; } = [];

        public Dictionary<string, List<Guid>> SchematicHistory { get; set; } = [];

        internal static string ExportFolderPath = null!;
        private TentBagReworkedConfig? _config;
        private FileWatcher? _fileWatcher;
        private IServerNetworkChannel? _channel;
        private IClientNetworkChannel? _clientChannel;

        // Клиентская подсветка зоны захвата (горячая клавиша).
        // Отдельный id подсветки, чтобы не конфликтовать с подсветкой мешающих блоков (1337).
        private const string GrabAreaHotkey = "tentbagreworked-showgraparea";
        private const int GrabAreaHighlightId = 1338;
        private const string GrabAreaShownSetting = "tentbagreworked-graparea-shown";
        private bool _grabAreaShown;
        private BlockPos? _lastGrabAreaPos;
        // Запоминаем и грань тоже: цвет зависит от грани (верх -> можно собирать, иначе -> нельзя),
        // а грань может смениться без смены блока (например, провести взглядом по одному блоку).
        private BlockFacing? _lastGrabAreaFace;

        // Серверная часть предпросмотра (мини-измерения на игрока)
        private ICoreServerAPI? _sapi;
        private readonly Dictionary<string, IMiniDimension> _previewDims = [];
        private readonly Dictionary<string, int> _previewDimIds = [];
        private readonly Dictionary<string, string> _previewContents = [];

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
            ClientApi = capi;

            // Типы пакетов регистрируются в одинаковом порядке на клиенте и сервере
            _clientChannel = ClientApi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>()
                .RegisterMessageType<ConfigSyncPacket>()
                .RegisterMessageType<RequestTentPreviewPacket>()
                .RegisterMessageType<TentPreviewPacket>()
                .RegisterMessageType<RotateTentPacket>()
                .SetMessageHandler<ErrorPacket>(packet => {
                    if (!string.IsNullOrEmpty(packet.Error))
                    {
                        // Добавилась локализация сырого ключа на стороне клиента
                        string translatedText = Vintagestory.API.Config.Lang.Get(packet.Error);

                        // Изменился код ошибки с "error" на уникальный "tentbag-error"
                        ClientApi.TriggerIngameError(this, "tentbag-error", translatedText);
                    }
                })
                .SetMessageHandler<ConfigSyncPacket>(OnConfigFromServer)
                .SetMessageHandler<TentPreviewPacket>(OnPreviewFromServer);

            // Горячая клавиша: показать/скрыть зону, которую заберёт пустой мешок
            ClientApi.Input.RegisterHotKey(GrabAreaHotkey, TentBagReworked.Config.Lang.HotkeyHighlightGrabArea(), GlKeys.P, HotkeyType.GUIOrOtherControls);
            ClientApi.Input.SetHotKeyHandler(GrabAreaHotkey, OnToggleGrabArea);

            // Восстанавливаем сохранённый выбор игрока из прошлой сессии
            if (ClientApi.Settings.Bool.Exists(GrabAreaShownSetting))
            {
                _grabAreaShown = ClientApi.Settings.Bool[GrabAreaShownSetting];
            }

            // Пока подсветка включена — обновляем каркас зоны за тем местом, куда смотрит игрок
            ClientApi.Event.RegisterGameTickListener(_ => { if (_grabAreaShown) UpdateGrabArea(); }, 150);
        }

        /// <summary>Клиент: переключить подсветку зоны захвата.</summary>
        private bool OnToggleGrabArea(KeyCombination comb)
        {
            _grabAreaShown = !_grabAreaShown;
            ClientApi.Settings.Bool[GrabAreaShownSetting] = _grabAreaShown; // сохраняем выбор

            if (_grabAreaShown)
            {
                _lastGrabAreaPos = null; // заставить перерисовать немедленно
                _lastGrabAreaFace = null;
                UpdateGrabArea();
            }
            else
            {
                ClearGrabArea();
            }

            return true;
        }

        /// <summary>Клиент: нарисовать каркас зоны захвата на месте взгляда (только с пустым мешком в руке).</summary>
        private void UpdateGrabArea()
        {
            IClientPlayer? player = ClientApi?.World.Player;
            BlockSelection? sel = player?.CurrentBlockSelection;
            ItemStack? stack = player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;

            // Показываем только если в руке палаточный мешок и он ПУСТОЙ (готов к сбору)
            bool isEmptyBag = stack?.Collectible?.CollectibleBehaviors?.Any(b => b is PackableBehavior) == true
                              && stack.Attributes?.GetString("tent-contents") == null
                              && stack.Attributes?.GetString("packed-contents") == null;

            if (player == null || sel?.Position == null || !isEmptyBag)
            {
                if (_lastGrabAreaPos != null)
                {
                    ClearGrabArea();
                }
                return;
            }

            // Не перерисовываем, пока цель взгляда не изменилась (ни блок, ни грань)
            if (sel.Position.Equals(_lastGrabAreaPos) && Equals(sel.Face, _lastGrabAreaFace))
            {
                return;
            }
            _lastGrabAreaPos = sel.Position.Copy();
            _lastGrabAreaFace = sel.Face;

            TentArea.GetPackBounds(EffectiveConfig, ClientApi!.World.BlockAccessor, sel.Position, out BlockPos start, out BlockPos end);
            List<BlockPos> edges = TentArea.GetCuboidEdges(start, end);

            // Собирать палатку можно только по ВЕРХНЕЙ грани (по полу). Если игрок смотрит на стену
            // или потолок — сбор запрещён, поэтому показываем зону КРАСНОЙ как предупреждение.
            bool canPackHere = sel.Face == BlockFacing.UP;
            string hex = canPackHere ? EffectiveConfig.HighlightPickupColor : EffectiveConfig.HighlightErrorColor;
            int color = hex.ToColor().Reverse();

            List<int> colors = [.. Enumerable.Repeat(color, edges.Count)];
            ClientApi.World.HighlightBlocks(player, GrabAreaHighlightId, edges, colors);
        }

        /// <summary>Клиент: убрать подсветку зоны захвата.</summary>
        private void ClearGrabArea()
        {
            _lastGrabAreaPos = null;
            _lastGrabAreaFace = null;
            IClientPlayer? player = ClientApi?.World.Player;
            if (player != null)
            {
                ClientApi!.World.HighlightBlocks(player, GrabAreaHighlightId, []);
            }
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _sapi = sapi;
            _channel = sapi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>()
                .RegisterMessageType<ConfigSyncPacket>()
                .RegisterMessageType<RequestTentPreviewPacket>()
                .RegisterMessageType<TentPreviewPacket>()
                .RegisterMessageType<RotateTentPacket>()
                .SetMessageHandler<RequestTentPreviewPacket>(OnRequestPreview)
                .SetMessageHandler<RotateTentPacket>(OnRotateTent);

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

        /// <summary>Клиент: попросить сервер повернуть палатку в руке на delta градусов (кратно 90).
        /// Угол храним в самом предмете (packed-rotation); сервер шлёт его обратно через MarkDirty.</summary>
        public void SendRotateRequest(int delta = 90)
        {
            _clientChannel?.SendPacket(new RotateTentPacket { Delta = delta });
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

            int angle = stack!.Attributes!.GetInt("packed-rotation", 0);

            if (!EnsureServerPreview(fromPlayer.PlayerUID, contents, angle, out IMiniDimension? dim, out int dimId, out int sizeX, out int sizeZ) || dim == null)
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

        /// <summary>Сервер: повернуть палатку в руке игрока. Угол (0/90/180/270) храним в самом
        /// предмете (packed-rotation) — единственный источник правды, поэтому превью и реальная
        /// распаковка берут одно и то же значение. MarkDirty синхронизирует угол обратно клиенту,
        /// после чего следующий тик превью перезапросит мини-измерение с новым поворотом.</summary>
        private void OnRotateTent(IServerPlayer fromPlayer, RotateTentPacket packet)
        {
            if (_sapi == null)
            {
                return;
            }

            ItemSlot? slot = fromPlayer.InventoryManager?.ActiveHotbarSlot;
            ItemStack? stack = slot?.Itemstack;
            string? contents = stack?.Attributes?.GetString("packed-contents")
                               ?? stack?.Attributes?.GetString("tent-contents");
            if (slot == null || stack?.Attributes == null || string.IsNullOrEmpty(contents))
            {
                return; // в руке нет развёрнутого схематика — крутить нечего
            }

            int angle = GameMath.Mod(stack.Attributes.GetInt("packed-rotation", 0) + packet.Delta, 360);
            stack.Attributes.SetInt("packed-rotation", angle);
            slot.MarkDirty(); // синхронизируем угол обратно клиенту (для unpack и перезапроса превью)

            // Сбрасываем кэш превью игрока, чтобы следующий запрос пересобрал мини-измерение
            // с новым углом (токен кэша и так включает угол — это подстраховка).
            _previewContents[fromPlayer.PlayerUID] = "";

            SendClientChatMessage(fromPlayer, TentBagReworked.Config.Lang.RotationInfo(angle));
        }

        /// <summary>
        /// Создаёт (один раз на игрока) мини-измерение и вставляет в него схематик.
        /// Повторная вставка только при смене содержимого. Чанки движок стримит клиенту сам.
        /// </summary>
        private bool EnsureServerPreview(string uid, string contents, int angle, out IMiniDimension? dim, out int dimId, out int sizeX, out int sizeZ)
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

            // Поворот — ровно как ванильный WorldEdit: на упакованном схематике, ДО вставки.
            // TransformWhilePacked внутри перепаковывает блоки/декор/блок-сущности и пересчитывает
            // SizeX/Y/Z (для 90/270 меняет местами X<->Z), поэтому центровка corner остаётся верной.
            if (angle != 0)
            {
                schematic.TransformWhilePacked(_sapi.World, EnumOrigin.BottomCenter, angle);
            }

            sizeX = schematic.SizeX;
            sizeZ = schematic.SizeZ;

            // Токен кэша включает угол: тот же contents с новым поворотом обязан вызвать переврезку.
            string token = contents + "@" + angle;

            // Перевставляем блоки только если изменилось содержимое ИЛИ угол.
            if (_previewContents.GetValueOrDefault(uid) != token)
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

                _previewContents[uid] = token;
                Logger.VerboseDebug("[TentBagReworked] preview pasted for " + uid + ": " + sizeX + "x" + schematic.SizeY + "x" + sizeZ + " rot=" + angle);
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

        // IDE1822: Метод сделан статическим, так как не использует локальные поля класса
        public static void SendClientChatMessage(IPlayer? player, string message)
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

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class RotateTentPacket
        {
            public int Delta = 90; // +90 по часовой за нажатие; можно слать -90 для обратного поворота
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
                AssetLocation assetLocation = new("tentbagreworked", "config/tent.json");
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