using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VsLang = Vintagestory.API.Config.Lang;

namespace TentBagReworked.Config;

public readonly struct LangMsg
{
    public string Key { get; }
    public object[] Args { get; }

    public LangMsg(string key, object[] args)
    {
        Key = key;
        Args = args ?? [];
    }

    /// перевод на язык конкретного игрока - серверная сторона
    public string For(IPlayer? player)
    {
        string lang = (player as IServerPlayer)?.LanguageCode ?? VsLang.CurrentLocale;

        // вложенные ключи (например статус вкл/выкл) переводим тем же языком
        object[] args = Args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is LangMsg nested)
            {
                if (ReferenceEquals(args, Args))
                {
                    args = (object[])Args.Clone();
                }
                args[i] = nested.For(player);
            }
        }

        return VsLang.GetL(lang, Key, args);
    }

    /// перевод на язык текущего процесса - клиент и логи
    public override string ToString() => VsLang.Get(Key, Args);

    public static implicit operator string(LangMsg msg) => msg.ToString();
}

public abstract class Lang
{
    public static LangMsg UnpackError(params object[] _) => new("tentbag:unpack-error", []);
    public static LangMsg IllegalItemError(params object[] args) => new("tentbag:illegal-item-error", args);
    public static LangMsg SolidGroundError(params object[] _) => new("tentbag:solid-ground-error", []);
    public static LangMsg ClearAreaError(params object[] _) => new("tentbag:clear-area-error", []);
    public static LangMsg UnpackHungerError(params object[] _) => new("tentbag:unpack-hunger-error", []);
    public static LangMsg PackHungerError(params object[] _) => new("tentbag:pack-hunger-error", []);
    public static LangMsg EmptyBuildError(params object[] _) => new("tentbag:empty-build-error", []);
    public static LangMsg WpRemoveError(params object[] _) => new("tentbag:wp-remove-error", []);
    public static LangMsg PackFloorOnlyError(params object[] _) => new("tentbag:pack-floor-only", []);
    public static LangMsg PackSuccess(params object[] args) => new("tentbag:pack-success", args);
    public static LangMsg UnpackSuccess(params object[] args) => new("tentbag:unpack-success", args);
    public static LangMsg WaypointTitle(params object[] args) => new("tentbag:waypoint-title", args);
    public static LangMsg StatusOn(params object[] _) => new("tentbag:status-on", []);
    public static LangMsg StatusOff(params object[] _) => new("tentbag:status-off", []);
    public static LangMsg PreviewToggled(params object[] args) => new("tentbag:preview-toggled", args);
    public static LangMsg HotkeyTogglePreview(params object[] _) => new("tentbag:hotkey-toggle-preview", []);
    public static LangMsg HotkeyHighlightGrabArea(params object[] _) => new("tentbag:hotkey-highlight-grab-area", []);
    public static LangMsg HotkeyRotate(params object[] _) => new("tentbag:hotkey-rotate", []);
    public static LangMsg RotationInfo(params object[] args) => new("tentbag:rotation-info", args);
}