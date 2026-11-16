using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SimuFarmer.Models;

namespace SimuFarmer.Systems
{
    public class LootCollector
    {
        private readonly ArenaManager _arenaManager;
        private readonly Settings _settings;
        private readonly UnstuckHandler _unstuckHandler;
        private DateTime _lastClickTime = DateTime.MinValue;
        private DateTime _lastZRefresh = DateTime.MinValue;

        public LootCollector(ArenaManager arenaManager, Settings settings, UnstuckHandler unstuckHandler)
        {
            _arenaManager = arenaManager;
            _settings = settings;
            _unstuckHandler = unstuckHandler;
        }

        public async Task TickSettle(GameController gc, SimuState state)
        {
            var player = gc.Player;
            if (player == null) return;

            // Target is configured Loot Center, or Monolith
            var targetPos = _arenaManager.CurrentProfile?.LootCenterPosition?.ToVector2() ?? 
                            _arenaManager.CurrentProfile?.MonolithPosition?.ToVector2();

            if (targetPos.HasValue)
            {
                var distToCenter = Vector2.Distance(player.GridPosNum, targetPos.Value);

                // Arrive completely at Loot Center (dist <= 12f) before looting
                // This ensures character stands inside the loot zone and all drops surround the character in open space
                if (distToCenter <= 12f)
                {
                    state.LastItemPickedAt = DateTime.Now;
                    state.TransitionTo(SimPhase.Looting, "Arrived at Loot Center -> Looting");
                    return;
                }

                // Actively walking to loot center during settle (using terrain pathfinder around walls/stairs)
                state.StatusText = $"Moving to Loot Center (dist {distToCenter:F0})";
                var playerGrid = player.GridPosNum;

                // Check if stuck trying to reach loot center (e.g. balustrade/wall)
                if (_unstuckHandler.IsStuck(playerGrid, 2.5f, 0.5))
                {
                    if (await _unstuckHandler.TryJumpTowards(gc, _settings, playerGrid, targetPos.Value, resumeCombatHold: false))
                    {
                        return;
                    }
                }

                var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, targetPos.Value, 16f);
                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
            }
            else
            {
                // Fallback if no loot center configured
                state.LastItemPickedAt = DateTime.Now;
                state.TransitionTo(SimPhase.Looting, "Scanning ground labels");
            }
        }

        public async Task TickLooting(GameController gc, SimuState state)
        {
            var player = gc.Player;
            if (player == null) return;

            var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabels;
            if (labels == null)
            {
                state.TransitionTo(SimPhase.QuietWait, "No labels element -> Entering quiet window");
                return;
            }

            // Filter to ONLY valid dropped items (ignoring Stash, Monolith, Portals, etc.)
            var targetLabel = labels
                .Where(l => l.IsVisible && l.Label != null && l.Label.IsValid && IsValidGroundItem(l.ItemOnGround, l.Label))
                .OrderBy(l => Vector2.Distance(player.GridPosNum, l.ItemOnGround.GridPosNum))
                .FirstOrDefault();

            if (targetLabel == null)
            {
                // All visible items picked up
                state.TransitionTo(SimPhase.QuietWait, "All loot collected -> Waiting quiet window for late drops");
                return;
            }

            var itemPos = targetLabel.ItemOnGround.GridPosNum;
            var dist = Vector2.Distance(player.GridPosNum, itemPos);

            // Require close proximity (dist <= 14f) for reliable label interaction
            if (dist > 14f)
            {
                // Walk closer to item
                state.StatusText = $"Walking to loot: dist {dist:F0}";
                var playerGrid = player.GridPosNum;

                // Check if stuck walking to item
                if (_unstuckHandler.IsStuck(playerGrid, 2.5f, 0.6))
                {
                    if (await _unstuckHandler.TryJumpTowards(gc, _settings, playerGrid, itemPos, resumeCombatHold: false))
                    {
                        return;
                    }
                }

                var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, itemPos, 14f);
                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
                return;
            }

            // In range: check screen bounds to ensure label is not under bottom UI / skill bar or top buff bar or HUD
            var rect = targetLabel.Label.GetClientRect();
            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);

            if (!BotInput.IsSafeFromUI(clickPos))
            {
                var playerGrid = player.GridPosNum;
                var dir = itemPos - playerGrid;

                if (dir.Length() > 4f)
                {
                    // Step closer to item to bring its label into open screen space
                    state.StatusText = "Item under UI bar -> Walking closer to bring into view";
                    var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, itemPos, 10f);
                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
                    var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                    await BotInput.ClickAt(moveVec);
                }
                else
                {
                    // Character is already right at the item, but label box was pushed under UI!
                    // Toggle 'Z' twice to refresh PoE ground label layout directly around character
                    if ((DateTime.Now - _lastZRefresh).TotalSeconds >= 1.5)
                    {
                        state.StatusText = "Item label obscured -> Refreshing labels (double-tap Z)";
                        SimuLogger.Log("[LOOT] Item label obscured by UI -> Refreshing labels via Z toggle");
                        _lastZRefresh = DateTime.Now;
                        await BotInput.TapKey(Keys.Z);
                        await Task.Delay(80);
                        await BotInput.TapKey(Keys.Z);
                        await Task.Delay(150);
                    }
                }
                return;
            }

            // Safe to click label
            if ((DateTime.Now - _lastClickTime).TotalMilliseconds >= 250)
            {
                var itemName = targetLabel.Label.Text ?? "Item";
                state.StatusText = $"Clicking loot: {itemName}";
                await BotInput.ClickAt(clickPos);
                _lastClickTime = DateTime.Now;
                state.LastItemPickedAt = DateTime.Now;
                state.TotalLootPicked++;
                state.WaveLootPicked++;
                SimuLogger.Log($"[LOOT] Picked {itemName} at {itemPos} (Total: {state.TotalLootPicked})");
            }
        }

        public void TickQuietWait(GameController gc, SimuState state)
        {
            // If any valid dropped item appeared, return to looting
            var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabels;
            bool hasVisibleItems = labels != null && labels.Any(l => 
                l.IsVisible && l.Label != null && l.Label.IsValid && IsValidGroundItem(l.ItemOnGround, l.Label));

            if (hasVisibleItems)
            {
                state.LastItemPickedAt = DateTime.Now;
                state.TransitionTo(SimPhase.Looting, "Late drop detected -> Picking up");
                return;
            }

            var elapsed = (DateTime.Now - state.LastItemPickedAt).TotalSeconds;
            var remaining = _settings.QuietWindowSeconds.Value - elapsed;

            if (remaining > 0)
            {
                state.StatusText = $"Quiet Window: {remaining:F1}s with 0 drops (confirming clear)";
                return;
            }

            // Quiet window passed with zero items -> start next wave
            if (state.CurrentWave < 15)
            {
                SimuLogger.Log($"[LOOT] Wave {state.CurrentWave} clear confirmed -> Next wave");
                state.TransitionTo(SimPhase.StartingWave, $"Wave {state.CurrentWave} loot cleared -> Starting next wave");
            }
            else
            {
                SimuLogger.Log("[RUN] Wave 15 completed and fully looted! Exiting to hideout");
                state.TransitionTo(SimPhase.Wave15Exit, "Wave 15 finished & looted -> Exiting to hideout");
            }
        }

        public static bool IsValidGroundItem(Entity? entity, Element? label)
        {
            if (entity == null || !entity.IsValid) return false;

            // Check label text first (fastest rejection of Stash / Monolith / Portals / Gold)
            var text = label?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Equals("Stash", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("Guild Stash", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Afflictionator", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                    text.EndsWith(" Gold", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check entity path / metadata
            var path = entity.Path ?? entity.Metadata ?? "";
            if (string.IsNullOrEmpty(path)) return false;

            if (path.Contains("Stash", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Afflictionator", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Transition", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Waypoint", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("NPC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (entity.Type == EntityType.WorldItem)
                return true;

            if (entity.HasComponent<ExileCore.PoEMemory.Components.WorldItem>())
                return true;

            if (path.Contains("WorldItem", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Metadata/Items/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}