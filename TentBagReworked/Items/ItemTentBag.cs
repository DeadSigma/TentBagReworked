using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace TentBagReworked.Items;

public class ItemTentBag : Item
{

    private const string HotkeyToggleHologram = "tentbagpreview";
    private const string HotkeyGrabArea = "tentbagreworked-showgraparea";
    private const string HotkeyRotate = "tentbagrotate";

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        if (withDebugInfo)
        {
            dsc.AppendLine($"<font color=\"#bbbbbb\">Id:{Id}</font>");
            dsc.AppendLine($"<font color=\"#bbbbbb\">Code: {Code}</font>");
        }
        foreach (CollectibleBehavior behavior in CollectibleBehaviors)
        {
            behavior.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        }

        string itemDescText = GetDynamicDescText();
        if (itemDescText.Length > 0 && dsc.Length > 0)
        {
            dsc.Append('\n');
        }
        dsc.Append(itemDescText);

        // Текущий угол поворота — показываем только когда мешок собран и реально повёрнут.
        int rot = inSlot.Itemstack?.Attributes?.GetInt("packed-rotation", 0) ?? 0;
        if (rot != 0)
        {
            if (dsc.Length > 0)
            {
                dsc.Append('\n');
            }
            dsc.Append(Lang.GetMatchingIfExists("tentbag:rotation-info", rot) ?? $"Rotation: {rot}°");
        }

        if (Code != null && Code.Domain != "game")
        {
            dsc.AppendLine(Lang.Get("Mod: {0}", (api.ModLoader.GetMod(Code.Domain)?.Info.Name ?? Code.Domain)));
        }
    }

    /// <summary>
    /// То же описание, что и стандартное itemdesc, но с подстановкой текущих
    /// биндингов горячих клавиш ({0} — голограмма, {1} — зона захвата).
    /// </summary>
    private string GetDynamicDescText()
    {
        if (Code == null)
        {
            return "";
        }

        string hologramKey = GetHotkeyDisplay(HotkeyToggleHologram, "B");
        string grabAreaKey = GetHotkeyDisplay(HotkeyGrabArea, "P");
        string rotateKey = GetHotkeyDisplay(HotkeyRotate, "R");

        // Тот же ключ, что использует движок (поддерживает '*' в lang-файле).
        // Лишние аргументы игнорируются, если в строке нет плейсхолдеров,
        // поэтому метод безопасен и для описаний других состояний мешка.
        string key = $"{Code.Domain}:itemdesc-{Code.Path}";
        return Lang.GetMatchingIfExists(key, hologramKey, grabAreaKey, rotateKey) ?? "";
    }

    /// <summary>
    /// Текущее отображаемое имя клавиши для хоткея. Если клиента/хоткея нет
    /// (сервер, не зарегистрирован) — используем fallback.
    /// </summary>
    private string GetHotkeyDisplay(string hotkeyCode, string fallback)
    {
        if (api is ICoreClientAPI capi)
        {
            string? display = capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping?.ToString();
            if (!string.IsNullOrEmpty(display))
            {
                return display;
            }
        }
        return fallback;
    }
}