#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using TentBagReworked.Config;
using TentBagReworked.Util;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TentBagReworked.Behaviors;

public class PackableBehavior : CollectibleBehavior
{
    private static readonly MethodInfo? ResendWaypoints = typeof(WaypointMapLayer).GetMethod("ResendWaypoints", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? RebuildMapComponents = typeof(WaypointMapLayer).GetMethod("RebuildMapComponents", BindingFlags.NonPublic | BindingFlags.Instance);

    private TentBagReworkedModSystem _modSystem = null!;

    private TentBagReworkedConfig Config => _modSystem.Config;
    private Dictionary<string, List<Guid>> SchematicHistory => _modSystem.SchematicHistory;
    private static string ExportFolderPath => TentBagReworkedModSystem.ExportFolderPath;

    // Метод сделан статическим
    private static bool IsAirOrNull(Block? block) => block is not { Replaceable: < 9505 };
    private bool IsPlantOrRock(Block? block) => Config.ReplacePlantsAndRocks && block?.Replaceable is >= 5500 and <= 6500;
    private bool IsReplaceable(Block? block) => IsAirOrNull(block) || IsPlantOrRock(block);

    private void SendClientError(EntityPlayer entity, string error) => _modSystem.SendClientError(entity.Player, error);

    // Вызов статического метода через имя класса
    private static void SendClientChatMessage(EntityPlayer entity, string message) => TentBagReworkedModSystem.SendClientChatMessage(entity.Player, message);

    private readonly AssetLocation? _emptyBag;
    private readonly AssetLocation? _packedBag;

    private long _highlightId;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        _modSystem = api.ModLoader.GetModSystem<TentBagReworkedModSystem>();
    }

    public PackableBehavior(CollectibleObject obj) : base(obj)
    {
        string domain = obj.Code.Domain;
        string path = obj.CodeWithoutParts(1);
        _emptyBag = new AssetLocation(domain, $"{path}-empty");
        _packedBag = new AssetLocation(domain, $"{path}-packed");
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (blockSel == null || byEntity is not EntityPlayer entity)
        {
            return;
        }

        handHandling = EnumHandHandling.PreventDefaultAction;

        if (entity.Api.Side != EnumAppSide.Server)
        {
            return;
        }

        string? contents = slot.Itemstack?.Attributes?.GetString("tent-contents") ?? slot.Itemstack?.Attributes?.GetString("packed-contents");
        int solidBlockCount = slot.Itemstack?.Attributes?.GetInt("solid-block-count") ?? 0;

        if (contents == null)
        {
            // Защита: упаковка (сбор) пустым мешком разрешена только по ВЕРХНЕЙ грани блока — по полу.
            // Нажатие на стену (боковая грань) или потолок (нижняя грань) сбор не запускает.
            if (blockSel.Face != BlockFacing.UP)
            {
                SendClientChatMessage(entity, Lang.PackFloorOnlyError());
                return;
            }

            PackContents(entity, blockSel, slot, ref solidBlockCount);
        }
        else
        {
            UnpackContents(entity, blockSel, slot, contents, ref solidBlockCount);
        }
    }

    private void PackContents(EntityPlayer entity, BlockSelection blockSel, ItemSlot slot, ref int solidBlockCount)
    {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

        int y = IsPlantOrRock(blockAccessor.GetBlock(blockSel.Position)) ? 1 : 0;

        // Единый расчёт зоны для сбора и для подсветки (TentArea), чтобы они не расходились.
        TentArea.GetPackBounds(Config, blockAccessor, blockSel.Position, out BlockPos start, out BlockPos end);

        if (!CanPack(entity, blockAccessor, start, end, ref solidBlockCount))
        {
            return;
        }

        // create schematic of area
        BlockSchematic bs = new();
        bs.AddAreaWithoutEntities(entity.World, blockAccessor, start, end);
        bs.PackIncludingAir(entity.World, start, solidBlockCount);

        SaveSchematic(entity, bs);

        CleanOldSchematics(entity);

        // clear area in world
        if (!Config.CopyMode) ClearArea(entity.World, start, end);

        // drop packed item on the ground and remove empty from inventory
        Item? packedItem = entity.World.GetItem(_packedBag);
        if (packedItem != null)
        {
            ItemStack packed = new(packedItem, slot.StackSize);
            packed.Attributes?.SetString("packed-contents", bs.ToJson());
            packed.Attributes?.SetInt("solid-block-count", solidBlockCount);
            if (Config.PutTentInInventoryOnUse)
            {
                ItemStack sinkStack = slot.Itemstack!.Clone();
                slot.Itemstack.StackSize = 0;
                slot.Itemstack = packed;
                slot.OnItemSlotModified(sinkStack);
            }
            else
            {
                entity.World.SpawnItemEntity(packed, blockSel.Position.ToVec3d().Add(0, 1 - y, 0));
                slot.TakeOutWhole();
            }
        }

        // Consume player saturation if in survival mode
        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
        }
        RemoveWaypoint(entity);
        ChatNotification(entity, Lang.PackSuccess(solidBlockCount, solidBlockCount * Config.BuildEffort));
    }

    private void UnpackContents(EntityPlayer entity, BlockSelection blockSel, ItemSlot slot, string contents, ref int solidBlockCount)
    {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

        //Customized BulkBlockAccessor with relight on commit
        IBulkBlockAccessor bulkBlockAccessor = entity.World.GetBlockAccessorBulkUpdate(true, true, false);

        int y = IsPlantOrRock(blockAccessor.GetBlock(blockSel.Position)) ? 1 : 0;

        BlockPos start = blockSel.Position.AddCopy(-Config.MaxRadius, 0 - y, -Config.MaxRadius);
        BlockPos end = blockSel.Position.AddCopy(Config.MaxRadius, Math.Max(Config.MaxHeight, 3), Config.MaxRadius);

        if (!CanUnpack(entity, blockAccessor, start, end, ref solidBlockCount))
        {
            return;
        }

        // try load schematic data from json contents
        string? error = null;
        BlockSchematic bs = BlockSchematic.LoadFromString(contents, ref error);
        if (!string.IsNullOrEmpty(error))
        {
            SendClientError(entity, Lang.UnpackError());
            return;
        }

        // paste the schematic into the world (requires bulk block accessor to prevent door/room issues)
        BlockPos adjustedStart = bs.AdjustStartPos(start.Add(Config.MaxRadius, 1, Config.MaxRadius), EnumOrigin.BottomCenter);
        bs.ReplaceMode = EnumReplaceMode.ReplaceAll;

        bs.Place(bulkBlockAccessor, entity.World, adjustedStart);
        bulkBlockAccessor.Commit();

        // Do this after bulk accessor commit
        bs.PlaceEntitiesAndBlockEntities(blockAccessor, entity.World, adjustedStart, bs.BlockCodes, bs.ItemCodes);

        // Багфикс освещения: источники света, расставленные
        {
            entity.Api.Event.RegisterCallback(dt =>
            {
                IBlockAccessor wa = entity.World.GetBlockAccessor(true, true, false);
                BlockPos scanPos = new(adjustedStart.dimension);

                for (int dx = 0; dx < bs.SizeX; dx++)
                {
                    for (int dy = 0; dy < bs.SizeY; dy++)
                    {
                        for (int dz = 0; dz < bs.SizeZ; dz++)
                        {
                            scanPos.Set(adjustedStart.X + dx, adjustedStart.Y + dy, adjustedStart.Z + dz);

                            Block block = wa.GetBlock(scanPos);
                            if (block.BlockId == 0 || block.LightHsv[2] <= 0)
                            {
                                continue;
                            }

                            BlockPos lightPos = scanPos.Copy();
                            try
                            {
                                BlockEntity? be = wa.GetBlockEntity(lightPos);
                                ITreeAttribute? beData = null;
                                if (be != null)
                                {
                                    beData = new TreeAttribute();
                                    be.ToTreeAttributes(beData);
                                }

                                int blockId = block.BlockId;

                                wa.SetBlock(0, lightPos);
                                wa.SetBlock(blockId, lightPos);

                                if (beData != null)
                                {
                                    BlockEntity? newBe = wa.GetBlockEntity(lightPos);
                                    if (newBe != null)
                                    {
                                        newBe.FromTreeAttributes(beData, entity.World);
                                        newBe.MarkDirty(true);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                _modSystem.Logger.Warning("[TentBagReworked] Light re-trigger failed at " + lightPos + ": " + e);
                            }
                        }
                    }
                }
            }, 250);
        }

        // drop empty item on the ground and remove empty from inventory
        Item? emptyItem = entity.World.GetItem(_emptyBag);
        if (emptyItem != null)
        {
            ItemStack empty = new(emptyItem, slot.StackSize);
            if (Config.PutTentInInventoryOnUse)
            {
                ItemStack sinkStack = slot.Itemstack!.Clone();
                slot.Itemstack.StackSize = 0;
                slot.Itemstack = empty;
                slot.OnItemSlotModified(sinkStack);
            }
            else
            {
                entity.World.SpawnItemEntity(empty, blockSel.Position.ToVec3d().Add(0, 1 - y, 0));
                slot.TakeOutWhole();
            }
        }

        if (Config.DropWaypoint) CreateWaypoint(entity, blockSel);

        // Consume player saturation if in survival mode
        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
        }

        ChatNotification(entity, Lang.UnpackSuccess(solidBlockCount, solidBlockCount * Config.BuildEffort));
    }

    private void SaveSchematic(Entity player, BlockSchematic bs)
    {
        string playerName = player.GetName() ?? "unknown";
        Guid id = Guid.NewGuid();

        // Упрощенная инициализация и оптимизация поиска в словаре
        if (!SchematicHistory.TryGetValue(playerName, out var history))
        {
            history = [];
            SchematicHistory[playerName] = history;
        }

        history.Add(id);

        bs.Save(Path.Combine(ExportFolderPath, $"tentbag-schematic-{playerName}-{id}.json"));
    }

    private void CleanOldSchematics(Entity player)
    {
        string playerName = player.GetName() ?? "unknown";

        // TryGetValue вместо ContainsKey
        if (SchematicHistory.TryGetValue(playerName, out List<Guid>? schematics) && schematics.Count > Config.MaxSchematicHistory)
        {
            if (schematics.Count > 0)
            {
                SchematicHistory[playerName].RemoveAt(0);

                string path = Path.Combine(ExportFolderPath, $"tentbag-schematic-{playerName}-{schematics[0]}.json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private void ChatNotification(EntityPlayer entity, string message)
    {
        if (Config.ShowChatNotification)
        {
            SendClientChatMessage(entity, message);
        }
    }

    private void CreateWaypoint(EntityPlayer player, BlockSelection blockSel)
    {
        if (player.Api is ICoreServerAPI)
        {
            // Сопоставление шаблонов
            if (player.Api.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) is WaypointMapLayer mapLayer)
            {
                Waypoint wp = new()
                {
                    Position = blockSel.Position.ToVec3d(),
                    Title = Lang.WaypointTitle(player.EntityId),
                    Pinned = false,
                    Icon = Config.WaypointIcon,
                    Color = ColorTranslator.FromHtml(Config.WaypointColor).ToArgb(),
                    OwningPlayerUid = player.PlayerUID,
                };

                mapLayer.AddWaypoint(wp, player.Player as IServerPlayer);
            }
        }
    }

    private static void RemoveWaypoint(EntityPlayer player)
    {
        if (player.Api is ICoreServerAPI serverApi)
        {
            IServerPlayer? serverPlayer = player.Player as IServerPlayer;

            // Сопоставление шаблонов
            if (player.Api.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) is WaypointMapLayer mapLayer)
            {
                var waypoints = GetWaypoints(serverApi);
                if (waypoints != null)
                {
                    foreach (Waypoint waypoint in waypoints.ToList().Where(w => w.OwningPlayerUid == player.PlayerUID))
                    {
                        if (waypoint.Title == Lang.WaypointTitle(player.EntityId))
                        {
                            if (player == null) // Этот блок условия всегда ложен, но оставил как в оригинале
                            {
                                player!.Api.Logger.Error(Lang.WpRemoveError());
                                StoreWaypoint(waypoint.OwningPlayerUid, waypoint);
                                continue;
                            }

                            waypoints.Remove(waypoint);

                            // Упрощенная инициализация массива параметров
                            ResendWaypoints?.Invoke(mapLayer, [serverPlayer!]);
                            RebuildMapComponents?.Invoke(mapLayer, null);
                        }
                    }
                }
            }
        }
    }

    public static List<Waypoint>? GetWaypoints(ICoreServerAPI? api)
    {
        if (api == null) return null;
        var mapLayer = api.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) as WaypointMapLayer;
        return mapLayer?.Waypoints;
    }

    private static void StoreWaypoint(string? playerUID, Waypoint wp)
    {
        if (string.IsNullOrEmpty(playerUID)) return;

        // Упрощенная инициализация и TryGetValue
        if (!TentBagReworkedModSystem.PendingWaypointNames.TryGetValue(playerUID, out var list))
        {
            list = [];
            TentBagReworkedModSystem.PendingWaypointNames[playerUID] = list;
        }

        list.Add(wp.Title);
    }

    private static void ClearArea(IWorldAccessor world, BlockPos start, BlockPos end)
    {
        ClearArea(world.BulkBlockAccessor, start, end);
        ClearArea(world.BlockAccessor, start, end);
    }

    private static void ClearArea(IBlockAccessor blockAccessor, BlockPos start, BlockPos end)
    {
        blockAccessor.WalkBlocks(start, end, (block, x, y, z) => {
            if (block.BlockId == 0)
            {
                return;
            }
            blockAccessor.SetBlock(0, new BlockPos(x, y, z, 0));
        });
        blockAccessor.Commit();
    }

    private bool CanPack(EntityPlayer entity, IBlockAccessor blockAccessor, BlockPos start, BlockPos end, ref int solidBlockCount)
    {
        // Упрощенная инициализация
        List<BlockPos> blocks = [];
        bool notified = false;
        bool canPack = true;
        int localSolidBlockCount = 0;

        blockAccessor.WalkBlocks(start, end, (block, posX, posY, posZ) =>
        {
            BlockPos pos = new(posX, posY, posZ, 0);
            if (!entity.World.Claims.TryAccess(entity.Player, pos, EnumBlockAccessFlags.BuildOrBreak))
            {
                notified = true;
                blocks.Add(pos);
            }
            else if (IsBannedBlock(block))
            {
                if (!notified)
                {
                    SendClientError(entity, Lang.IllegalItemError(block.GetPlacedBlockName(entity.World, pos)));
                    notified = true;
                }

                blocks.Add(pos);
            }

            if (block.Id != 0)
            {
                localSolidBlockCount++;
            }

            EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
            EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
            if (hunger != null && playerGameMode == EnumGameMode.Survival)
            {
                float currentHunger = hunger.Saturation;

                if (localSolidBlockCount * Config.BuildEffort > currentHunger)
                {
                    if (!notified)
                    {
                        SendClientError(entity, Lang.PackHungerError());
                        notified = true;
                    }
                    canPack = false;
                }
            }
        });

        if (localSolidBlockCount == 0)
        {
            if (!notified)
            {
                SendClientError(entity, Lang.EmptyBuildError());
                notified = true;
            }
            canPack = false;
        }

        solidBlockCount = localSolidBlockCount;
        return canPack && !ShouldHighlightBlocks(entity, blocks);
    }

    private bool CanUnpack(EntityPlayer entity, IBlockAccessor blockAccessor, BlockPos start, BlockPos end, ref int solidBlockCount)
    {
        // Упрощенная инициализация
        List<BlockPos> blocks = [];
        bool notified = false;
        bool canUnpack = true;

        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            float currentHunger = hunger.Saturation;

            if (solidBlockCount * Config.BuildEffort > currentHunger)
            {
                if (!notified)
                {
                    SendClientError(entity, Lang.UnpackHungerError());
                    notified = true;
                }
                canUnpack = false;
            }
        }

        if (canUnpack)
        {
            blockAccessor.WalkBlocks(start, end, (block, posX, posY, posZ) => {
                BlockPos pos = new(posX, posY, posZ, 0);
                if (!entity.World.Claims.TryAccess(entity.Player, pos, EnumBlockAccessFlags.BuildOrBreak))
                {
                    notified = true;
                    blocks.Add(pos);
                }
                else if (pos.Y == start.Y)
                {
                    if (Config.RequireSolidGround && !block.SideSolid[BlockFacing.indexUP])
                    {
                        if (!notified)
                        {
                            SendClientError(entity, Lang.SolidGroundError());
                            notified = true;
                        }

                        blocks.Add(pos);
                    }
                }
                else if (!IsReplaceable(block))
                {
                    if (!notified)
                    {
                        SendClientError(entity, Lang.ClearAreaError());
                        notified = true;
                    }

                    blocks.Add(pos);
                }
            });
        }

        return canUnpack && !ShouldHighlightBlocks(entity, blocks);
    }

    private bool IsBannedBlock(Block block)
    {
        AssetLocation? blockCode = block.Code;

        if (blockCode == null)
        {
            return false;
        }

        if (Config.AllowListMode)
        {
            if (block.BlockId == 0) return false;
            if (IsPlantOrRock(block)) return false;

            foreach (string allowed in Config.AllowedBlocks)
            {
                AssetLocation code = new(allowed);
                if (code.Equals(blockCode))
                {
                    return false;
                }

                if (code.IsWildCard && WildcardUtil.GetWildcardValue(code, blockCode) != null)
                {
                    return false;
                }
            }

            return true;
        }
        else
        {
            foreach (string banned in Config.BannedBlocks)
            {
                AssetLocation code = new(banned);
                if (code.Equals(blockCode))
                {
                    return true;
                }

                if (code.IsWildCard && WildcardUtil.GetWildcardValue(code, blockCode) != null)
                {
                    return true;
                }
            }

            return false;
        }

    }

    private bool ShouldHighlightBlocks(EntityPlayer entity, List<BlockPos> blocks)
    {
        if (blocks.Count <= 0)
        {
            return false;
        }

        if (_highlightId > 0)
        {
            entity.Api.Event.UnregisterCallback(_highlightId);
        }

        int color = Config.HighlightErrorColor.ToColor().Reverse();

        // Упрощенная инициализация массива
        List<int> colors = [.. Enumerable.Repeat(color, blocks.Count)];
        entity.World.HighlightBlocks(entity.Player, 1337, blocks, colors);

        _highlightId = entity.Api.Event.RegisterCallback(_ => {
            // Упрощенная инициализация пустого массива
            List<BlockPos> empty = [];
            entity.World.HighlightBlocks(entity.Player, 1337, empty);
        }, 2500);

        return true;
    }
}