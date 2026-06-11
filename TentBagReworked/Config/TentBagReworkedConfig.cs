using System.ComponentModel;

namespace TentBagReworked.Config;

public class TentBagReworkedConfig {
    
    /// <summary>
    /// Maximum allowable radius for tent build
    /// </summary>
    public int MaxRadius { get; set; } = 3;

    /// <summary>
    /// Maximum allowable height for tent build
    /// </summary>
    public int MaxHeight { get; set; } = 7;

    
    /// <summary>
    /// Build Effort is calculated per solid block, so a 3x3x7 tent would have a maximum possible BuildEffort of 3*3*3*7 = 189
    /// </summary>
    public float BuildEffort { get; set; } = 2F;

    /// <summary>
    /// Requires all blocks below the build to be solid blocks
    /// </summary>
    public bool RequireSolidGround { get; set; } = false;

    /// <summary>
    /// Shifts grab position vertical down one block
    /// </summary>
    public bool GrabFloor { get; set; } = false;

    /// <summary>
    /// Copies the build area rather than picking it up
    /// </summary>
    public bool CopyMode { get; set; } = false;

    /// <summary>
    /// Replace plants and rocks in build area
    /// </summary>
    public bool ReplacePlantsAndRocks { get; set; } = true;

    /// <summary>
    /// Place packed tent in inventory after packing
    /// </summary>
    public bool PutTentInInventoryOnUse { get; set; } = true;

    /// <summary>
    /// Block error highlight color
    /// </summary>    
    public string HighlightErrorColor { get; set; } = "#2FFF0000";

    /// <summary>
    /// Adds a map waypoint at your build location after unpacking
    /// </summary>
    public bool DropWaypoint { get; set; } = true;

    /// <summary>
    /// circle, bee, cave, home, ladder, pick, rocks, ruins, spiral, star1, star2, trader, vessel, etc
    /// </summary>
    public string WaypointIcon { get; set; } = "home";

    /// <summary>
    /// https://www.99colors.net/dot-net-colors
    /// </summary>
    public string WaypointColor { get; set; } = "dodgerblue";

    /// <summary>
    /// Whether the waypoint should automatically pinned or not
    /// </summary>
    public bool PinWaypoint { get; set; } = true;

    /// <summary>
    /// AllowListMode enables explicit block whitelist
    /// </summary>
    public bool AllowListMode { get; set; } = false;

    /// <summary>
    /// Shows a client side chat notification with block count and saturation on pack/unpack
    /// </summary>
    public bool ShowChatNotification { get; set; } = true;

    /// <summary>
    /// Maximum number of schematics to keep in history
    /// </summary>
    public int MaxSchematicHistory { get; set; } = 10;

    /// <summary>
    /// Banned blocks (ignored if AllowListMode = true)
    /// </summary>
    public string[] BannedBlocks { get; set; } = {
        "game:paperlantern-*",
        "game:chandelier-*",
        "game:log-grown-*",
        "game:log-resin-*",
        "game:log-resinharvested-*",
        "game:statictranslocator-*",
        "game:teleporterbase",
        "game:crop-*",
        "game:herb-*",
        "game:mushroom-*",
        "game:smallberrybush-*",
        "game:bigberrybush-*",
        "game:water-*",
        "game:lava-*",
        "game:farmland-*",
        "game:rawclay-*",
        "game:peat-*",
        "game:rock-*",
        "game:ore-*",
        "game:crock-burned-*",
        "game:bowl-meal",
        "game:claypot-cooked",
        "game:anvil-*",
        "game:forge"
    };

    /// <summary>
    /// Allowed blocksk (ignored if AllowListMode = false
    /// </summary>
    public string[] AllowedBlocks { get; set; } = {
        "game:soil-*"
    };
}
