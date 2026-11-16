using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SimuFarmer.Models;

namespace SimuFarmer.Systems
{
    public class MapDeviceHandler
    {
        private readonly Settings _settings;
        private DateTime _lastActionTime = DateTime.MinValue;
        private static readonly int[] ActivateButtonPath = { 7, 0, 3 };
        private const string FullSimulacrumPath = "CurrencyAfflictionFragment";

        public MapDeviceHandler(Settings settings)
        {
            _settings = settings;
        }

        public async Task<bool> TickMapDevice(GameController gc, SimuState state)
        {
            if (!gc.Area.CurrentArea.IsHideout)
            {
                SimuLogger.Log("[MAP] Safety triggered: MapDeviceHandler called while outside hideout! Aborting.");
                return false;
            }

            var atlas = gc.IngameState?.IngameUi?.Atlas;

            // 0. If map portals are already open in Hideout, enter them directly!
            if (atlas?.IsVisible != true)
            {
                var existingPortals = gc.EntityListWrapper.OnlyValidEntities
                    .Where(e => e.IsTargetable &&
                        (e.Type == EntityType.TownPortal ||
                         e.Type == EntityType.AreaTransition ||
                         e.Path?.Contains("Town_Portals", StringComparison.OrdinalIgnoreCase) == true ||
                         e.Metadata?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true ||
                         e.RenderName?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true ||
                         e.RenderName?.Contains("Simulacrum", StringComparison.OrdinalIgnoreCase) == true) &&
                        e.RenderName != "Map Device" &&
                        e.Path?.Contains("MappingDevice") != true)
                    .ToList();

                if (existingPortals.Count >= 1)
                {
                    SimuLogger.Log($"[MAP] Found {existingPortals.Count} open portals already in Hideout -> Entering portal directly");
                    state.TransitionTo(SimPhase.EnteringPortal, "Portals already open -> Entering portal");
                    return true;
                }
            }

            // 1. If Map Device is not open, find and open it
            if (atlas?.IsVisible != true)
            {
                var deviceEntity = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.IsTargetable && (e.RenderName == "Map Device" || e.Path?.Contains("MappingDevice") == true));

                if (deviceEntity == null)
                {
                    state.StatusText = "Map Device not found in hideout";
                    return false;
                }

                var player = gc.Player;
                if (player == null) return false;

                var dist = Vector2.Distance(player.GridPosNum, deviceEntity.GridPosNum);
                if (dist > _settings.InteractRadius.Value)
                {
                    state.StatusText = $"Walking to Map Device (dist: {dist:F0})";

                    // Step closer in safe increments to avoid clicking on portals around device
                    var dir = Vector2.Normalize(deviceEntity.GridPosNum - player.GridPosNum);
                    float stepDist = Math.Min(dist - 15f, 20f);
                    var walkTarget = player.GridPosNum + dir * stepDist;

                    // If a portal is blocking the direct line, route around it laterally
                    var blockingPortals = gc.EntityListWrapper.OnlyValidEntities
                        .Where(e => e.IsTargetable && (e.Type == EntityType.TownPortal || e.Path?.Contains("Portal") == true))
                        .ToList();

                    if (blockingPortals.Any(p => Vector2.Distance(walkTarget, p.GridPosNum) < 14f))
                    {
                        var perp = new Vector2(-dir.Y, dir.X);
                        walkTarget += perp * 16f; // Steer laterally around the portal ring
                    }

                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(walkTarget);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(player.GridPosNum));
                    var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                    await BotInput.ClickAt(moveVec);
                    return false;
                }

                // Click device entity: target upper apparatus / label to clear ground portal hitboxes
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
                {
                    state.StatusText = "Opening Map Device";

                    // Try clicking on-ground label first
                    var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                    bool labelClicked = false;
                    if (labels != null)
                    {
                        var dLabel = labels.FirstOrDefault(l => l.Entity?.Id == deviceEntity.Id && l.Label != null && l.Label.IsVisible);
                        if (dLabel?.Label != null)
                        {
                            var rect = dLabel.Label.GetClientRect();
                            await BotInput.ClickAt(new Vector2(rect.Center.X, rect.Center.Y));
                            labelClicked = true;
                        }
                    }

                    if (!labelClicked)
                    {
                        // Target elevated top rim of Map Device apparatus (Z + 50)
                        var center = deviceEntity.BoundsCenterPosNum;
                        if (center == Vector3.Zero) center = deviceEntity.PosNum;
                        var elevated = center + new Vector3(0, 0, 50f);
                        var screen = gc.IngameState.Camera.WorldToScreen(elevated);
                        await BotInput.ClickAt(new Vector2(screen.X, screen.Y));
                    }

                    _lastActionTime = DateTime.Now;
                }
                return false;
            }

            // 2. Check if a Simulacrum is already loaded or needs to be inserted
            var activateBtn = FindActivateButton(atlas);
            bool isReadyToActivate = activateBtn != null && activateBtn.IsVisible && activateBtn.IsActive;

            if (!isReadyToActivate)
            {
                // Option A: Check Atlas Inventory / Map Stash on the left of Map Device
                var atlasSimu = FindSimulacrumInAtlas(atlas);
                if (atlasSimu != null)
                {
                    if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
                    {
                        state.StatusText = "Right-clicking Simulacrum in Atlas Inventory";
                        var rect = atlasSimu.GetClientRect();
                        await BotInput.RightClickAt(new Vector2(rect.Center.X, rect.Center.Y));
                        _lastActionTime = DateTime.Now;
                        await Task.Delay(600);
                    }
                    return false;
                }

                // Option B: Player Inventory - must press 'I' first if panel is closed
                var invPanel = gc.IngameState?.IngameUi?.InventoryPanel;
                if (invPanel == null || !invPanel.IsVisible)
                {
                    if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 400)
                    {
                        state.StatusText = "Opening Player Inventory ('I') to insert Simulacrum";
                        await BotInput.TapKey(System.Windows.Forms.Keys.I);
                        _lastActionTime = DateTime.Now;
                        await Task.Delay(300);
                    }
                    return false;
                }

                // Player inventory is open -> Find Simulacrum in inventory
                var invItems = gc.IngameState.ServerData?.PlayerInventories?[0]?.Inventory?.InventorySlotItems;
                var simuItem = invItems?.FirstOrDefault(si =>
                    IsSimulacrumItem(si.Item?.Path));

                if (simuItem == null)
                {
                    state.StatusText = "ERROR: No Simulacrum found in Atlas Inventory or Player Inventory!";
                    return false;
                }

                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
                {
                    state.StatusText = "Right-clicking Simulacrum from Player Inventory";
                    var rect = simuItem.GetClientRect();
                    await BotInput.RightClickAt(new Vector2(rect.Center.X, rect.Center.Y));
                    _lastActionTime = DateTime.Now;
                    await Task.Delay(600);
                }
                return false;
            }

            // 3. Click ACTIVATE
            if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
            {
                state.StatusText = "Clicking ACTIVATE on Map Device";
                var rect = activateBtn.GetClientRect();
                await BotInput.ClickAt(new Vector2(rect.Center.X, rect.Center.Y));
                _lastActionTime = DateTime.Now;

                // Map Device automatically closes upon activation in PoE.
                await Task.Delay(400);

                // If player inventory is still open, close it safely with Space (NEVER blind Escape)
                var inv = gc.IngameState?.IngameUi?.InventoryPanel;
                if (inv != null && inv.IsVisible)
                {
                    await BotInput.TapKey(Keys.Space);
                    await Task.Delay(200);
                }

                state.TransitionTo(SimPhase.EnteringPortal, "Map Device activated -> Waiting for portals");
                return true;
            }

            return false;
        }

        private static Element? FindSimulacrumInAtlas(Element atlas)
        {
            try
            {
                // Standard MapStashPath = { 3, 0, 1 }
                var mapStash = atlas.GetChildFromIndices(new[] { 3, 0, 1 });
                if (mapStash != null)
                {
                    for (int i = 0; i < mapStash.ChildCount; i++)
                    {
                        var child = mapStash.GetChildAtIndex(i);
                        if (child == null || !child.IsVisible) continue;
                        var path = child.Entity?.Path;
                        if (path != null && path.Contains(FullSimulacrumPath, StringComparison.OrdinalIgnoreCase))
                            return child;
                    }
                }

                return SearchElementForSimulacrum(atlas, 0);
            }
            catch { return null; }
        }

        private static Element? SearchElementForSimulacrum(Element elem, int depth)
        {
            if (elem == null || depth > 5) return null;
            var path = elem.Entity?.Path;
            if (path != null && path.Contains(FullSimulacrumPath, StringComparison.OrdinalIgnoreCase) && elem.IsVisible)
                return elem;

            foreach (var child in elem.Children)
            {
                var found = SearchElementForSimulacrum(child, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        public async Task TickEnterPortal(GameController gc, SimuState state)
        {
            if (!gc.Area.CurrentArea.IsHideout)
            {
                // Already transitioned out of hideout
                return;
            }

            // 1. Ensure blocking UI panels (Atlas / Inventory / Stash) are closed before attempting to click portal
            var atlasOpen = gc.IngameState?.IngameUi?.Atlas?.IsVisible == true;
            var invOpen = gc.IngameState?.IngameUi?.InventoryPanel?.IsVisible == true;
            var stashOpen = gc.IngameState?.IngameUi?.StashElement?.IsVisible == true;

            if (atlasOpen || invOpen || stashOpen)
            {
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 300)
                {
                    state.StatusText = "Closing UI panels to clear view of portals";
                    await BotInput.TapKey(Keys.Space); // Space safely closes panels without opening Escape menu
                    _lastActionTime = DateTime.Now;
                }
                return;
            }

            // 2. Query targetable map portals in hideout
            var portals = gc.EntityListWrapper.OnlyValidEntities
                .Where(e => e.IsTargetable &&
                    (e.Type == EntityType.TownPortal ||
                     e.Type == EntityType.AreaTransition ||
                     e.Path?.Contains("Town_Portals", StringComparison.OrdinalIgnoreCase) == true ||
                     e.Path?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true ||
                     e.Metadata?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true ||
                     e.Metadata?.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase) == true ||
                     e.RenderName?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true ||
                     e.RenderName?.Contains("Simulacrum", StringComparison.OrdinalIgnoreCase) == true) &&
                    e.RenderName != "Map Device" &&
                    e.Path?.Contains("MappingDevice") != true)
                .OrderBy(e => e.GridPosNum.Y) // Top-most / back portal is never occluded by the player or front rim
                .ToList();

            if (portals.Count == 0)
            {
                state.StatusText = "Waiting for Map Device portals to open...";
                return;
            }

            var targetPortal = portals.First();
            var player = gc.Player;
            if (player == null) return;

            var dist = Vector2.Distance(player.GridPosNum, targetPortal.GridPosNum);
            if (dist > _settings.InteractRadius.Value)
            {
                state.StatusText = $"Walking to portal (dist: {dist:F0})";
                var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, player.GridPosNum, targetPortal.GridPosNum, 16f);
                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(player.GridPosNum));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
                return;
            }

            if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 800)
            {
                state.StatusText = "Clicking Portal to enter Simulacrum";
                _lastActionTime = DateTime.Now;

                // Priority A: Try clicking on-ground label first (checks portal entity, simulacrum, and arena names)
                bool clicked = false;
                try
                {
                    var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                    if (labels != null)
                    {
                        var pLabel = labels.FirstOrDefault(l =>
                            l.Label != null && l.Label.IsVisible &&
                            (l.Entity?.Id == targetPortal.Id ||
                             portals.Any(p => p.Id == l.Entity?.Id) ||
                             IsPortalLabelText(l.Label.Text)));

                        if (pLabel?.Label != null)
                        {
                            var rect = pLabel.Label.GetClientRect();
                            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
                            if (BotInput.IsSafeFromUI(clickPos))
                            {
                                SimuLogger.Log($"[MAP] Clicking portal label '{pLabel.Label.Text}' at ({clickPos.X:F0}, {clickPos.Y:F0})");
                                await BotInput.ClickAt(clickPos);
                                clicked = true;
                            }
                        }
                    }
                }
                catch { }

                // Priority B: Click portal entity directly (elevated into middle of vortex)
                if (!clicked)
                {
                    var center = targetPortal.BoundsCenterPosNum;
                    if (center == Vector3.Zero) center = targetPortal.PosNum;
                    if (center == Vector3.Zero) center = gc.IngameState.Data.ToWorldWithTerrainHeight(targetPortal.GridPosNum);
                    var elevated = center + new Vector3(0, 0, 40f);
                    var screen = gc.IngameState.Camera.WorldToScreen(elevated);
                    var screenPos = BotInput.ClampToWindow(new Vector2(screen.X, screen.Y));
                    if (BotInput.IsSafeFromUI(screenPos))
                    {
                        SimuLogger.Log($"[MAP] Clicking portal entity directly at ({screenPos.X:F0}, {screenPos.Y:F0})");
                        await BotInput.ClickAt(screenPos);
                    }
                }

                // Stay in EnteringPortal: if loading starts, AreaChange() transitions to ArenaInit.
                // If loading hasn't started after delay, TickEnterPortal retries automatically.
                await Task.Delay(1000);
            }
        }

        private static bool IsPortalLabelText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Contains("Simulacrum", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Lunacy", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Bridge", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Syndrome", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Hysteriagate", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Oriath", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Encampment", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Delusion", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Watch", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSimulacrumItem(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Contains("CurrencyAfflictionFragment", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("Simulacrum", StringComparison.OrdinalIgnoreCase);
        }

        private static Element? FindActivateButton(Element atlas)
        {
            var btn = atlas.GetChildFromIndices(ActivateButtonPath);
            if (btn != null && btn.IsVisible) return btn;

            // Recursive search fallback
            return FindElementWithText(atlas, "activate");
        }

        private static Element? FindElementWithText(Element parent, string text)
        {
            if (parent == null) return null;
            if (!string.IsNullOrEmpty(parent.Text) && parent.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
                return parent;

            foreach (var child in parent.Children)
            {
                var found = FindElementWithText(child, text);
                if (found != null) return found;
            }
            return null;
        }
    }
}
