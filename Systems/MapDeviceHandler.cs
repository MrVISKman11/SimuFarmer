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
        private DateTime _lastWalkLog = DateTime.MinValue;
        private DateTime _lastPortalLog = DateTime.MinValue;
        private DateTime _lastPortalClickTime = DateTime.MinValue;
        private bool _waitingForPortalLoad = false;
        private static readonly int[] ActivateButtonPath = { 7, 0, 3 };
        private const string FullSimulacrumPath = "CurrencyAfflictionFragment";

        public void ResetPortalState()
        {
            _waitingForPortalLoad = false;
            _lastPortalClickTime = DateTime.MinValue;
        }

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

            // 1. If Map Device is not open, find and open it
            if (atlas?.IsVisible != true)
            {
                var deviceEntity = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.IsTargetable && (e.RenderName == "Map Device" || e.Path?.Contains("MappingDevice") == true));

                if (deviceEntity == null)
                {
                    state.StatusText = "Map Device not found in hideout";
                    if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 3000)
                    {
                        SimuLogger.Log("[MAP] ERROR: Map Device entity not found in hideout!");
                        _lastActionTime = DateTime.Now;
                    }
                    return false;
                }

                var player = gc.Player;
                if (player == null) return false;

                var dist = Vector2.Distance(player.GridPosNum, deviceEntity.GridPosNum);
                if (dist > _settings.InteractRadius.Value)
                {
                    state.StatusText = $"Walking to Map Device (dist: {dist:F0})";

                    if ((DateTime.Now - _lastWalkLog).TotalMilliseconds >= 2000)
                    {
                        SimuLogger.Log($"[MAP] Walking to Map Device (dist: {dist:F0})");
                        _lastWalkLog = DateTime.Now;
                    }

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
                        var dLabel = labels.FirstOrDefault(l =>
                            l.Label != null && l.Label.IsVisible &&
                            (l.Entity?.Id == deviceEntity.Id ||
                             l.Label.Text?.Contains("Map Device", StringComparison.OrdinalIgnoreCase) == true));

                        if (dLabel?.Label != null)
                        {
                            var rect = dLabel.Label.GetClientRect();
                            SimuLogger.Log($"[MAP] Clicking Map Device label at ({rect.Center.X:F0}, {rect.Center.Y:F0})");
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
                        SimuLogger.Log($"[MAP] Clicking Map Device entity apparatus directly at ({screen.X:F0}, {screen.Y:F0})");
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
                        SimuLogger.Log("[MAP] Found Simulacrum in Atlas Inventory -> Right-clicking to insert");
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
                        SimuLogger.Log("[MAP] Atlas is open -> Pressing 'I' to open Player Inventory");
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
                    if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 2000)
                    {
                        SimuLogger.Log($"[MAP] ERROR: No Simulacrum item found in player inventory! (Scanned {invItems?.Count ?? 0} items)");
                        _lastActionTime = DateTime.Now;
                    }
                    return false;
                }

                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 500)
                {
                    state.StatusText = "Right-clicking Simulacrum from Player Inventory";
                    var rect = simuItem.GetClientRect();
                    SimuLogger.Log($"[MAP] Found Simulacrum in player inventory -> Right-clicking at ({rect.Center.X:F0}, {rect.Center.Y:F0})");
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
                SimuLogger.Log($"[MAP] ACTIVATE button ready -> Clicking ACTIVATE at ({rect.Center.X:F0}, {rect.Center.Y:F0})");
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

                ResetPortalState();
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
            if (atlasOpen)
            {
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 300)
                {
                    state.StatusText = "Closing Map Device panel to clear view of portals";
                    SimuLogger.Log("[MAP] Map Device panel is open in EnteringPortal -> Pressing Escape to dismiss");
                    await BotInput.TapKey(Keys.Escape);
                    _lastActionTime = DateTime.Now;
                    _waitingForPortalLoad = false;
                }
                return;
            }

            var invOpen = gc.IngameState?.IngameUi?.InventoryPanel?.IsVisible == true;
            var stashOpen = gc.IngameState?.IngameUi?.StashElement?.IsVisible == true;

            if (invOpen || stashOpen)
            {
                if ((DateTime.Now - _lastActionTime).TotalMilliseconds >= 300)
                {
                    state.StatusText = "Closing UI panels to clear view of portals";
                    await BotInput.TapKey(Keys.Space); // Space safely closes inventory/stash
                    _lastActionTime = DateTime.Now;
                    _waitingForPortalLoad = false;
                }
                return;
            }

            // 2. Locate Map Device entity in hideout so we never click it or confuse it with portals
            var deviceEntity = gc.EntityListWrapper.OnlyValidEntities
                .FirstOrDefault(e => e.IsTargetable && (
                    e.RenderName?.Contains("Map Device", StringComparison.OrdinalIgnoreCase) == true ||
                    e.Path?.Contains("MapDevice", StringComparison.OrdinalIgnoreCase) == true ||
                    e.Path?.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase) == true));

            Vector2? devScreenPos = null;
            if (deviceEntity != null)
            {
                var devCenter = deviceEntity.BoundsCenterPosNum != Vector3.Zero ? deviceEntity.BoundsCenterPosNum : deviceEntity.PosNum;
                if (devCenter == Vector3.Zero) devCenter = gc.IngameState.Data.ToWorldWithTerrainHeight(deviceEntity.GridPosNum);
                var dScr = gc.IngameState.Camera.WorldToScreen(devCenter);
                devScreenPos = new Vector2(dScr.X, dScr.Y);
            }

            // 3. Query targetable map portals in hideout (strictly excluding the Map Device apparatus)
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
                    (deviceEntity == null || e.Id != deviceEntity.Id) &&
                    e.RenderName?.Contains("Map Device", StringComparison.OrdinalIgnoreCase) != true &&
                    e.Path?.Contains("MapDevice", StringComparison.OrdinalIgnoreCase) != true &&
                    e.Path?.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase) != true &&
                    (deviceEntity == null || (Vector2.Distance(e.GridPosNum, deviceEntity.GridPosNum) >= 6f && Vector2.Distance(e.GridPosNum, deviceEntity.GridPosNum) <= 35f)))
                .ToList();

            if (portals.Count == 0)
            {
                state.StatusText = "Waiting for Map Device portals to open...";
                if ((DateTime.Now - _lastPortalLog).TotalMilliseconds >= 2000)
                {
                    SimuLogger.Log("[MAP] Waiting for Map Device portals to open...");
                    _lastPortalLog = DateTime.Now;
                }
                return;
            }

            var player = gc.Player;
            if (player == null) return;

            // 4. Select the best portal:
            // Prefer portals that have strong lateral clearance on screen from the Map Device apparatus (>= 150px)
            // (e.g. side portals like East/West which never overlap the central dais in foreground or background)
            var candidatePortals = portals
                .Select(p => {
                    var pCenter = p.BoundsCenterPosNum != Vector3.Zero ? p.BoundsCenterPosNum : p.PosNum;
                    if (pCenter == Vector3.Zero) pCenter = gc.IngameState.Data.ToWorldWithTerrainHeight(p.GridPosNum);
                    var pScr = gc.IngameState.Camera.WorldToScreen(pCenter);
                    var scrVec = new Vector2(pScr.X, pScr.Y);
                    float devDist = devScreenPos.HasValue ? Vector2.Distance(scrVec, devScreenPos.Value) : 999f;
                    float lateralDist = devScreenPos.HasValue ? Math.Abs(scrVec.X - devScreenPos.Value.X) : 999f;
                    float playerDist = Vector2.Distance(player.GridPosNum, p.GridPosNum);
                    return new { Portal = p, ScreenPos = scrVec, DevDist = devDist, LateralDist = lateralDist, PlayerDist = playerDist };
                })
                .OrderByDescending(x => x.LateralDist >= 150f && x.DevDist >= 180f) // Strongly prefer clear side portals
                .ThenByDescending(x => x.DevDist)
                .ThenBy(x => x.PlayerDist)
                .ToList();

            var bestCandidate = candidatePortals.FirstOrDefault();
            if (bestCandidate == null) return;

            var targetPortal = bestCandidate.Portal;
            var dist = Vector2.Distance(player.GridPosNum, targetPortal.GridPosNum);

            // Require walking to within 10 units of the portal first so the character is standing right in front of it!
            if (dist > 10f)
            {
                state.StatusText = $"Walking to portal (dist: {dist:F0})";
                var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, player.GridPosNum, targetPortal.GridPosNum, 14f);
                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(player.GridPosNum));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
                return;
            }

            // 5. Single-click & wait: Never spam clicks!
            // Click once and give the engine up to 3.5s to trigger the loading screen.
            if (_waitingForPortalLoad)
            {
                double elapsed = (DateTime.Now - _lastPortalClickTime).TotalSeconds;
                if (elapsed < 3.5)
                {
                    state.StatusText = $"Portal clicked -> Waiting for loading screen ({3.5 - elapsed:F1}s)...";
                    return;
                }

                SimuLogger.Log($"[MAP] Portal click timed out after {elapsed:F1}s without loading screen -> Retrying precise click");
                _waitingForPortalLoad = false;
            }

            state.StatusText = "Clicking Portal to enter Simulacrum (waiting for load)";

            // Determine click coordinate
            Vector2 clickPos = Vector2.Zero;
            bool foundLabel = false;

            try
            {
                var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labels != null)
                {
                    var pLabel = labels.FirstOrDefault(l =>
                        l.Label != null && l.Label.IsVisible &&
                        (l.Entity?.Id == targetPortal.Id || portals.Any(p => p.Id == l.Entity?.Id) || IsPortalLabelText(l.Label.Text)));

                    if (pLabel?.Label != null)
                    {
                        var rect = pLabel.Label.GetClientRect();
                        var lblCenter = new Vector2(rect.Center.X, rect.Center.Y);
                        float dDist = devScreenPos.HasValue ? Vector2.Distance(lblCenter, devScreenPos.Value) : 999f;
                        if (dDist > 80f && BotInput.IsSafeFromUI(lblCenter))
                        {
                            clickPos = lblCenter;
                            foundLabel = true;
                            SimuLogger.Log($"[MAP] Precise click: targeting portal label '{pLabel.Label.Text}' at ({clickPos.X:F0}, {clickPos.Y:F0})");
                        }
                    }
                }
            }
            catch { }

            if (!foundLabel)
            {
                // Target portal center directly at entity PosNum (no excessive vertical offset)
                var pCenter = targetPortal.BoundsCenterPosNum != Vector3.Zero ? targetPortal.BoundsCenterPosNum : targetPortal.PosNum;
                if (pCenter == Vector3.Zero) pCenter = gc.IngameState.Data.ToWorldWithTerrainHeight(targetPortal.GridPosNum);
                var scr = gc.IngameState.Camera.WorldToScreen(pCenter);
                clickPos = BotInput.ClampToWindow(new Vector2(scr.X, scr.Y));
                SimuLogger.Log($"[MAP] Precise click: targeting portal entity at ({clickPos.X:F0}, {clickPos.Y:F0}) (DevDist: {bestCandidate.DevDist:F0}px, Lateral: {bestCandidate.LateralDist:F0}px)");
            }

            _lastPortalClickTime = DateTime.Now;
            _waitingForPortalLoad = true;
            _lastActionTime = DateTime.Now;

            await BotInput.ClickAt(clickPos);
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
