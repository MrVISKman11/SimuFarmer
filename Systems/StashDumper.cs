using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using SimuFarmer.Models;

namespace SimuFarmer.Systems
{
    public class StashDumper
    {
        private readonly Settings _settings;
        private DateTime _lastActionTime = DateTime.MinValue;
        private const string FullSimulacrumPath = "CurrencyAfflictionFragment";

        public StashDumper(Settings settings)
        {
            _settings = settings;
        }

        public async Task<bool> TickDump(GameController gc, SimuState state)
        {
            if (!gc.Area.CurrentArea.IsHideout)
            {
                SimuLogger.Log("[STASH] Safety triggered: StashDumper called while outside hideout! Aborting.");
                return false;
            }

            var stashEl = gc.IngameState?.IngameUi?.StashElement;

            // 1. If stash is not open, find and open it
            if (stashEl?.IsVisible != true)
            {
                var stashEntity = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Metadata/MiscellaneousObjects/Stash") == true);

                if (stashEntity == null)
                {
                    state.StatusText = "Stash entity not found in hideout";
                    return false;
                }

                var player = gc.Player;
                if (player == null) return false;

                var dist = Vector2.Distance(player.GridPosNum, stashEntity.GridPosNum);
                if (dist > _settings.InteractRadius.Value)
                {
                    state.StatusText = $"Walking to Stash (dist: {dist:F0})";
                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stashEntity.GridPosNum);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(player.GridPosNum));
                    var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                    await BotInput.ClickAt(moveVec);
                    return false;
                }

                // Click stash entity
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
                {
                    state.StatusText = "Opening Stash";
                    await BotInput.ClickEntity(gc, stashEntity);
                    _lastActionTime = DateTime.Now;
                }
                return false;
            }

            // 2. Select configured dump tab via arrow keys
            int targetTab = _settings.StashTabIndex.Value;
            var currentTab = stashEl.IndexVisibleStash;
            if (currentTab != targetTab)
            {
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 250)
                {
                    var arrowKey = currentTab < targetTab ? Keys.Right : Keys.Left;
                    state.StatusText = $"Switching Stash Tab: currently {currentTab}, target {targetTab}";
                    await BotInput.TapKey(arrowKey);
                    _lastActionTime = DateTime.Now;
                }
                return false;
            }

            // 2.5 Safety recovery: if cursor is accidentally holding an item (failed pickup/modifier miss)
            var cursor = gc.IngameState?.IngameUi?.Cursor;
            if (cursor != null && cursor.ChildCount > 0)
            {
                state.StatusText = "Item stuck on cursor -> Placing into stash";
                SimuLogger.Log("[STASH] Detected item held on cursor! Depositing into open stash tab.");

                // Click into the open stash tab area to drop/deposit the held item
                var stashRect = stashEl.GetClientRect();
                var depositPos = new Vector2(stashRect.X + stashRect.Width * 0.4f, stashRect.Y + stashRect.Height * 0.4f);
                await BotInput.ClickAt(depositPos);
                await Task.Delay(250);

                // If still holding after click, tap Escape to cancel/place back in inventory
                if (gc.IngameState?.IngameUi?.Cursor?.ChildCount > 0)
                {
                    await BotInput.TapKey(Keys.Escape);
                    await Task.Delay(200);
                }

                _lastActionTime = DateTime.Now;
                return false;
            }

            // 3. Scan inventory and dump items
            var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
            if (invItems == null || invItems.Count == 0)
            {
                state.StatusText = "Inventory empty -> Closing stash";
                await CloseStashAndFinish(state);
                return true;
            }

            // Find items that are NOT full Simulacrums
            var itemToDump = invItems.FirstOrDefault(si =>
            {
                var path = si.Item?.Path;
                if (IsProtectedSimulacrum(path))
                    return false; // Protect full Simulacrums!
                return true;
            });

            if (itemToDump == null)
            {
                // Only protected Simulacrums remain
                state.StatusText = "All loot dumped (Simulacrums protected) -> Closing stash";
                await CloseStashAndFinish(state);
                return true;
            }

            // Click item to stash (with 220ms inter-item delay for server sync)
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 220)
            {
                var rect = itemToDump.GetClientRect();
                var clickPos = new Vector2(rect.Center.X, rect.Center.Y);

                if (_settings.UseCtrlShiftDump.Value)
                {
                    state.StatusText = "Dumping item (Ctrl+Shift+Click)";
                    await BotInput.CtrlShiftClickAt(clickPos);
                }
                else
                {
                    state.StatusText = "Dumping item (Ctrl+Click)";
                    await BotInput.CtrlClickAt(clickPos);
                }
                _lastActionTime = DateTime.Now;
            }

            return false;
        }

        private static bool IsProtectedSimulacrum(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("Simulacrum", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task CloseStashAndFinish(SimuState state)
        {
            await BotInput.TapKey(Keys.Escape);
            await Task.Delay(300);
            state.TransitionTo(SimPhase.NavigatingToMapDevice, "Stash dumped -> Moving to Map Device");
        }
    }
}
