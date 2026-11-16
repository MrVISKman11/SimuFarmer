using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace SimuFarmer
{
    public class Settings : ISettings
    {
        public ToggleNode Enable { get; set; } = new(false);

        [Menu("Run Bot Hotkey", "Toggles automated farming on/off")]
        public HotkeyNodeV2 RunBotHotkey { get; set; } = new(Keys.F9);

        [Menu("Status: Bot Active", "Whether the bot is currently actively farming")]
        public ToggleNode IsRunning { get; set; } = new(false);

        [Menu("Auto-Loop Reruns", "Automatically portal to hideout, dump stash, activate map device, and repeat")]
        public ToggleNode AutoLoopReruns { get; set; } = new(true);

        [Menu("Combat Keybinds", 100)]
        public EmptyNode CombatHeader { get; set; } = new();

        [Menu("Initial Aura Key", "Pressed once at the start of each Simulacrum", 101, 100)]
        public HotkeyNodeV2 AuraKey { get; set; } = new(Keys.Space);

        [Menu("Combat Hold Key", "Held down during active combat waves", 102, 100)]
        public HotkeyNodeV2 CombatKey { get; set; } = new(Keys.E);

        [Menu("Portal Key", "Pressed to open portal back to hideout", 103, 100)]
        public HotkeyNodeV2 PortalKey { get; set; } = new(Keys.T);

        [Menu("Movement Skill Key", "Pressed to jump/blink over walls, ledges, and unstick (e.g. Flame Dash, Leap Slam, Dash)", 104, 100)]
        public HotkeyNodeV2 MovementKey { get; set; } = new(Keys.R);

        [Menu("Use Movement Skill When Stuck", "Uses movement skill on R towards destination when movement is blocked by walls/balustrades", 105, 100)]
        public ToggleNode UseMovementSkill { get; set; } = new(true);

        [Menu("Timing & Delays", 200)]
        public EmptyNode TimingHeader { get; set; } = new();

        [Menu("Post-Wave Settle Delay (Seconds)", "Wait time after stash becomes clickable for drops to animate", 201, 200)]
        public RangeNode<float> PostWaveSettleSeconds { get; set; } = new(12.0f, 5.0f, 25.0f);

        [Menu("Loot Quiet Window (Seconds)", "Wait time with zero new ground items before starting next wave", 202, 200)]
        public RangeNode<float> QuietWindowSeconds { get; set; } = new(10.0f, 3.0f, 20.0f);

        [Menu("Interaction Radius (Grid)", "Max distance to walk before clicking an entity or ground label", 203, 200)]
        public RangeNode<float> InteractRadius { get; set; } = new(25.0f, 15.0f, 40.0f);

        [Menu("Hideout & Stashing", 300)]
        public EmptyNode StashHeader { get; set; } = new();

        [Menu("Use Ctrl+Shift Click to Dump", "Uses Ctrl+Shift+Click instead of Ctrl+Click to bypass full/affinity tabs", 301, 300)]
        public ToggleNode UseCtrlShiftDump { get; set; } = new(true);

        [Menu("Dump Stash Tab Index", "0-based index of your dump tab (0 is first tab)", 302, 300)]
        public RangeNode<int> StashTabIndex { get; set; } = new(0, 0, 50);

        [Menu("Humanization & Debug", 400)]
        public EmptyNode HumanHeader { get; set; } = new();

        [Menu("Waypoint Jitter (Grid)", "Random Gaussian displacement added to waypoints for organic pathing", 401, 400)]
        public RangeNode<float> WaypointJitter { get; set; } = new(3.0f, 0.0f, 8.0f);

        [Menu("Draw Debug Overlay", "Renders current state, waypoints, and timers on screen", 402, 400)]
        public ToggleNode DrawDebugOverlay { get; set; } = new(true);

        [Menu("Show HUD Window", "Show or hide the SimuFarmer HUD window overlay", 403, 400)]
        public ToggleNode ShowHud { get; set; } = new(true);

        [Menu("Click-Through HUD When Farming", "Allows mouse clicks to pass straight through the HUD to the game when farming", 404, 400)]
        public ToggleNode ClickThroughHud { get; set; } = new(true);
    }
}
