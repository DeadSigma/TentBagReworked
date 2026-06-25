using System;
using System.Collections.Generic;
using System.Linq;
using TentBagReworked.Config;
using TentBagReworked.Util;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TentBagReworked;

/// <summary>
/// Клиентский предпросмотр палатки (вариант A — серверное мини-измерение, как в World Edit).
///
/// Рендерер сам блоки НЕ рисует и НЕ парсит схематик. Он:
///   1) каждый тик сообщает серверу, что игрок держит развёрнутую палатку и на какой блок смотрит
///      (SendPreviewRequest с координатами);
///   2) сервер строит/двигает мини-измерение и присылает его id + позицию в мире;
///   3) клиент рисует измерение в этой позиции через GetOrCreateDimension(dimId, pos) —
///      ровно как клиентский обработчик World Edit;
///   4) проверяет место и подсвечивает мешающие блоки (слот 1338) — ещё ДО нажатия ПКМ.
///
/// Само мини-измерение создаётся и наполняется НА СЕРВЕРЕ (TentBagReworkedModSystem),
/// его чанки движок стримит клиенту — поэтому chisel/ящики/контейнеры рисуются корректно.
/// Позицию угла (BottomCenter, с plantRock) считает сервер из реального мира.
///
/// Цвет: движок рисует превью стандартным полупрозрачным образом; перекрасить всё
/// измерение API не позволяет. Сигнал «сюда нельзя» — красная подсветка конфликтных блоков.
/// </summary>
public class TentHologramRenderer : ModSystem
{
    /// <summary>Мост «мод-система получила пакет -> рендерер».</summary>
    internal static TentHologramRenderer? PreviewInstance;

    // Отдельный слот подсветки (серверный использует 1337), чтобы оба могли работать одновременно.
    private const int PreviewHighlightSlot = 1338;

    // Имя горячей клавиши
    private const string HotkeyCode = "tentbagpreview";
    private const string PreviewEnabledSetting = "tentbagreworked-preview-enabled";

    // Упрощенная инициализация коллекции
    private static readonly List<BlockPos> EmptyBlocks = [];

    private ICoreClientAPI capi = null!;

    /// <summary>Переключается горячей клавишей. Когда false — ничего не рисуется.</summary>
    public bool PreviewEnabled { get; private set; } = true;

    // Мини-измерение превью (создано сервером, тут только клиентская ссылка).
    private IMiniDimension? clientDim;
    private int serverDimId = -1;

    // Что в последний раз запросили у сервера
    private bool sentEnable;
    private string? sentContents;
    private BlockPos? lastSentTarget;

    // Состояние проверки места.
    private bool lastPlacementValid = true;
    private BlockPos? lastValidatedPos;
    private long lastValidatedMs;
    private bool highlightsShown;

    private static TentBagReworkedModSystem ModSys => TentBagReworkedModSystem.Instance;
    private static TentBagReworkedConfig Cfg => ModSys.EffectiveConfig;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        PreviewInstance = this;

        // Восстанавливаем выбор игрока из прошлой сессии
        if (api.Settings.Bool.Exists(PreviewEnabledSetting))
        {
            PreviewEnabled = api.Settings.Bool[PreviewEnabledSetting];
        }

        api.Event.RegisterGameTickListener(OnPreviewTick, 100);

        api.Input.RegisterHotKey(HotkeyCode, Lang.HotkeyTogglePreview(), GlKeys.B, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(HotkeyCode, OnTogglePreview);
    }

    private bool OnTogglePreview(KeyCombination comb)
    {
        PreviewEnabled = !PreviewEnabled;
        capi.Settings.Bool[PreviewEnabledSetting] = PreviewEnabled; // сохраняем выбор

        if (!PreviewEnabled)
        {
            if (sentEnable)
            {
                ModSys.SendPreviewRequest(false);
                sentEnable = false;
                sentContents = null;
                lastSentTarget = null;
            }
            HideClientPreview();
            ClearHighlights();
        }

        string status = PreviewEnabled ? Lang.StatusOn() : Lang.StatusOff();
        capi.ShowChatMessage(Lang.PreviewToggled(status));
        return true;
    }

    //  Запрос превью у сервера (с прицелом) + проверка места
    private void OnPreviewTick(float dt)
    {
        if (!TryGetPreviewState(out string contents, out BlockPos basePos))
        {
            if (sentEnable)
            {
                ModSys.SendPreviewRequest(false);
                sentEnable = false;
                sentContents = null;
                lastSentTarget = null;
            }
            HideClientPreview();
            ClearHighlights();
            return;
        }

        // Просим сервер построить/подвинуть превью при смене предмета или блока под прицелом.
        bool targetChanged = !SamePos(basePos, lastSentTarget);
        if (!sentEnable || sentContents != contents || targetChanged)
        {
            ModSys.SendPreviewRequest(true, basePos.X, basePos.Y, basePos.Z, basePos.dimension);
            sentEnable = true;
            sentContents = contents;
            lastSentTarget = basePos.Copy();
        }

        // Проверка места + подсветка конфликтных блоков (клиентская).
        long now = Environment.TickCount64;
        bool posChanged = !SamePos(basePos, lastValidatedPos);
        if (posChanged || now - lastValidatedMs > 500)
        {
            lastPlacementValid = ValidatePlacement(basePos, out List<BlockPos> badBlocks);
            SetHighlights(lastPlacementValid ? EmptyBlocks : badBlocks);
            lastValidatedPos = basePos.Copy();
            lastValidatedMs = now;
        }
    }

    /// <summary>Клиент: пришёл ответ сервера (id измерения + позиция в мире). dimId &lt; 0 — спрятать.</summary>
    public void OnServerPreviewDim(int dimId, int posX, int posY, int posZ)
    {
        if (dimId < 0)
        {
            HideClientPreview();
            return;
        }

        bool first = serverDimId < 0;
        serverDimId = dimId;

        // Порядок и набор вызовов — как в клиентском обработчике World Edit (OnReceivedPreviewBlocks).
        capi.World.SetBlocksPreviewDimension(dimId);
        clientDim = capi.World.GetOrCreateDimension(dimId, new Vec3d(posX, posY, posZ));
        clientDim.selectionTrackingOriginalPos = new BlockPos(posX, posY, posZ);
        clientDim.TrackSelection = false;

        if (first)
        {
            capi.Logger.VerboseDebug("[TentBagReworked] preview shown: dim=" + dimId + " at " + posX + "," + posY + "," + posZ);
        }
    }

    /// <summary>Спрятать превью на клиенте (рендер измерения выключается).</summary>
    private void HideClientPreview()
    {
        if (serverDimId >= 0 && capi?.World != null)
        {
            capi.World.SetBlocksPreviewDimension(-1);
        }
        serverDimId = -1;
        clientDim = null;
    }

    //  Клиентская проверка места (повтор логики PackableBehavior.CanUnpack)
    private bool ValidatePlacement(BlockPos basePos, out List<BlockPos> badBlocks)
    {
        // Упрощенная инициализация
        List<BlockPos> bad = [];
        TentBagReworkedConfig cfg = Cfg;
        IBlockAccessor ba = capi.World.BlockAccessor;
        IClientPlayer? player = capi.World.Player;

        int plantRock = IsPlantOrRock(ba.GetBlock(basePos)) ? 1 : 0;
        BlockPos start = basePos.AddCopy(-cfg.MaxRadius, -plantRock, -cfg.MaxRadius);
        BlockPos end = basePos.AddCopy(cfg.MaxRadius, Math.Max(cfg.MaxHeight, 3), cfg.MaxRadius);
        int startY = start.Y;
        int dim = basePos.dimension;

        ba.WalkBlocks(start, end, (block, px, py, pz) =>
        {
            BlockPos pos = new(px, py, pz, dim);

            if (player != null && !capi.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak))
            {
                bad.Add(pos);
            }
            else if (py == startY)
            {
                // нижний слой — это "земля" под палаткой
                if (cfg.RequireSolidGround && !block.SideSolid[BlockFacing.indexUP])
                {
                    bad.Add(pos);
                }
            }
            else if (!IsReplaceable(block))
            {
                bad.Add(pos);
            }
        });

        badBlocks = bad;
        return bad.Count == 0;
    }

    //  Подсветка мешающих блоков (клиентская, живая)
    private void SetHighlights(List<BlockPos> badBlocks)
    {
        IClientWorldAccessor? world = capi.World;
        IClientPlayer? player = world?.Player;
        if (world == null || player == null)
        {
            return;
        }

        if (badBlocks.Count == 0)
        {
            if (highlightsShown)
            {
                world.HighlightBlocks(player, PreviewHighlightSlot, EmptyBlocks);
                highlightsShown = false;
            }
            return;
        }

        int color = Cfg.HighlightErrorColor.ToColor().Reverse();

        // Упрощенная инициализация
        List<int> colors = [.. Enumerable.Repeat(color, badBlocks.Count)];
        world.HighlightBlocks(player, PreviewHighlightSlot, badBlocks, colors);
        highlightsShown = true;
    }

    private void ClearHighlights()
    {
        if (!highlightsShown)
        {
            return;
        }
        IClientWorldAccessor? world = capi.World;
        if (world?.Player is { } player)
        {
            world.HighlightBlocks(player, PreviewHighlightSlot, EmptyBlocks);
        }
        highlightsShown = false;
    }

    //  Вспомогательное
    private bool TryGetPreviewState(out string contents, out BlockPos basePos)
    {
        contents = "";
        basePos = null!;

        if (!PreviewEnabled)
        {
            return false;
        }

        IClientPlayer? player = capi.World?.Player;
        ItemStack? stack = player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        if (!IsDeployable(stack))
        {
            return false;
        }

        BlockSelection? sel = player!.CurrentBlockSelection;
        if (sel?.Position == null)
        {
            return false;
        }

        contents = stack!.Attributes?.GetString("packed-contents")
                   ?? stack.Attributes?.GetString("tent-contents")
                   ?? "";
        if (string.IsNullOrEmpty(contents))
        {
            return false;
        }

        basePos = sel.Position;
        return true;
    }

    /// <summary>Предмет считается "разворачиваемым", если в нём лежит готовый схематик.</summary>
    private static bool IsDeployable(ItemStack? stack)
    {
        if (stack?.Attributes == null)
        {
            return false;
        }
        return !string.IsNullOrEmpty(stack.Attributes.GetString("packed-contents"))
               || !string.IsNullOrEmpty(stack.Attributes.GetString("tent-contents"));
    }

    // Те же правила, что и в PackableBehavior.
    private static bool IsAirOrNull(Block? block) => block is not { Replaceable: < 9505 };
    private static bool IsPlantOrRock(Block? block) => Cfg.ReplacePlantsAndRocks && block?.Replaceable is >= 5500 and <= 6500;
    private static bool IsReplaceable(Block? block) => IsAirOrNull(block) || IsPlantOrRock(block);

    private static bool SamePos(BlockPos? a, BlockPos? b)
    {
        if (a == null || b == null)
        {
            return ReferenceEquals(a, b);
        }
        return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.dimension == b.dimension;
    }

    public override void Dispose()
    {
        base.Dispose();
        if (capi != null)
        {
            if (sentEnable)
            {
                ModSys.SendPreviewRequest(false);
            }
            ClearHighlights();
            HideClientPreview();
        }
        if (ReferenceEquals(PreviewInstance, this))
        {
            PreviewInstance = null;
        }
    }
}