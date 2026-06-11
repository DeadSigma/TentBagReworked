namespace TentBagReworked.Config;

public abstract class Lang {
    public static string UnpackError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:unpack-error");
    public static string IllegalItemError(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:illegal-item-error", args);
    public static string SolidGroundError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:solid-ground-error");
    public static string ClearAreaError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:clear-area-error");
    public static string UnpackHungerError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:unpack-hunger-error");
    public static string PackHungerError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:pack-hunger-error");
    public static string EmptyBuildError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:empty-build-error");
    public static string WpRemoveError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:wp-remove-error");
    public static string PackFloorOnlyError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:pack-floor-only");
    public static string PackSuccess(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:pack-success", args);
    public static string UnpackSuccess(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:unpack-success", args);
    public static string WaypointTitle(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:waypoint-title", args);
    public static string StatusOn(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:status-on");
    public static string StatusOff(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:status-off");
    public static string PreviewToggled(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:preview-toggled", args);
    public static string HotkeyTogglePreview(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:hotkey-toggle-preview");
}
