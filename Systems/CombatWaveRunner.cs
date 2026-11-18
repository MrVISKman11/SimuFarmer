using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using SimuFarmer.Models;

namespace SimuFarmer.Systems
{
    public class CombatWaveRunner
    {
        private readonly ArenaManager _arenaManager;
        private readonly Settings _settings;
        private readonly UnstuckHandler _unstuckHandler;
        private DateTime _waveActiveStartTime = DateTime.MinValue;
        private DateTime _lastMonolithClick = DateTime.MinValue;
        private bool _lootZoneReached = false;

        public CombatWaveRunner(ArenaManager arenaManager, Settings settings, UnstuckHandler unstuckHandler)
        {
            _arenaManager = arenaManager;
            _settings = settings;
            _unstuckHandler = unstuckHandler;
        }

        public async Task TickArenaInit(GameController gc, SimuState state)
        {
            state.StatusText = "Arena Init: Activating aura";

            var player = gc.Player;
            if (player == null) return;

            // Press Aura key if not done this instance (in PoE, activating an aura immediately breaks grace period)
            // NEVER perform blind ground clicks at spawn: the player spawns directly on the entrance portal!
            if (!state.AuraActivatedThisRun)
            {
                await BotInput.TapKey(_settings.AuraKey.Value.Key);
                state.AuraActivatedThisRun = true;
                await Task.Delay(250);
            }

            _lootZoneReached = false;
            _unstuckHandler.Reset(player.GridPosNum);
            state.TransitionTo(SimPhase.StartingWave, "Aura active -> Ready to start wave");
        }

        private DateTime _lastWalkClick = DateTime.MinValue;

        public async Task TickStartingWave(GameController gc, SimuState state)
        {
            var monolith = _arenaManager.MonolithEntity;
            var stash = _arenaManager.StashEntity;
            var player = gc.Player;
            if (player == null) return;

            // 1. Check if wave is genuinely active (e.g. re-entering zone after death)
            // A wave is only active when the stash is locked (untargetable) AND monolith is not targetable.
            bool waveGenuinelyActive = stash != null && stash.IsValid && !stash.IsTargetable &&
                                      (monolith == null || !monolith.IsValid || !monolith.IsTargetable);

            if (waveGenuinelyActive)
            {
                OnWaveStarted(gc, state);
                return;
            }

            var monolithGrid = monolith?.GridPosNum ?? _arenaManager.CurrentProfile?.MonolithPosition;
            if (!monolithGrid.HasValue)
            {
                state.StatusText = "Searching for Afflictionator monolith...";
                return;
            }

            var playerGrid = player.GridPosNum;
            float distToMonolith = Vector2.Distance(playerGrid, monolithGrid.Value);

            // Determine navigation target: In Hysteriagate, navigate to Loot Center milestone first, then to Monolith!
            bool isHysteriagate = _arenaManager.CurrentProfile?.AreaKey?.Contains("Hysteria", StringComparison.OrdinalIgnoreCase) == true;
            var lootCenter = _arenaManager.CurrentProfile?.LootCenterPosition?.ToVector2();

            Vector2 targetGrid = monolithGrid.Value;
            bool goingToLootCenter = false;

            if (isHysteriagate && lootCenter.HasValue)
            {
                float distToLootCenter = Vector2.Distance(playerGrid, lootCenter.Value);

                // If player is already within 25 units of monolith, we're inside the arena -> proceed directly to monolith
                if (distToMonolith <= 25f)
                {
                    _lootZoneReached = true;
                }
                // If player has arrived at Loot Center milestone (within 20 units), transition to monolith target
                else if (distToLootCenter <= 20f)
                {
                    if (!_lootZoneReached)
                    {
                        _lootZoneReached = true;
                        SimuLogger.Log($"[START] Arrived at Loot Center milestone (dist {distToLootCenter:F1}) -> Proceeding to Afflictionator");
                    }
                }

                // If Loot Center milestone not yet reached, steer towards Loot Center!
                if (!_lootZoneReached)
                {
                    targetGrid = lootCenter.Value;
                    goingToLootCenter = true;
                }
            }

            float pDist = Vector2.Distance(playerGrid, targetGrid);

            // 2. PRIMARY REQUIREMENT: If distant (> 18 units), WALK to target first!
            // Never attempt to click the Afflictionator label/box from afar because PoE will not move the character.
            if (pDist > 18f || goingToLootCenter)
            {
                if (goingToLootCenter)
                {
                    state.StatusText = $"[Hysteriagate] Navigating to Loot Zone first (dist: {pDist:F0})";
                }
                else
                {
                    state.StatusText = $"Walking to Afflictionator (dist: {distToMonolith:F0})";
                }

                // Check if stuck trying to reach the Afflictionator (e.g. wall/balustrade blocking)
                // Require at least 1.5s in StartingWave before allowing unstuck jump, preventing instant spawn jumps near entrance portal
                if ((DateTime.Now - state.PhaseEnteredAt).TotalSeconds >= 1.5 && _unstuckHandler.IsStuck(playerGrid, 2.5f, 0.7))
                {
                    if (await _unstuckHandler.TryJumpTowards(gc, _settings, playerGrid, targetGrid, resumeCombatHold: false))
                    {
                        return;
                    }
                }

                // Throttle movement clicks to avoid click-spam stutter
                if ((DateTime.Now - _lastWalkClick).TotalMilliseconds >= 250)
                {
                    var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, targetGrid, 16f);
                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
                    var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));

                    // Deflect moveVec away from entrance portal and portal labels if near spawn
                    moveVec = DeflectFromEntrancePortal(gc, moveVec);

                    await BotInput.ClickAt(moveVec);
                    _lastWalkClick = DateTime.Now;
                }
                return;
            }

            // 3. IN RANGE (<= 18 units from Monolith): Now and only now, click the Afflictionator to start the wave
            if ((DateTime.Now - _lastMonolithClick).TotalMilliseconds >= 1200)
            {
                state.StatusText = "Clicking Afflictionator to start wave";
                SimuLogger.Log($"[START] In range of Afflictionator (dist {distToMonolith:F1}) -> Clicking to start wave");
                _lastMonolithClick = DateTime.Now;

                // Priority A: Click visible label if on screen
                if (TryGetMonolithLabelPos(gc, monolith, state, out var labelPos))
                {
                    SimuLogger.Log($"[START] Clicking Afflictionator label at ({labelPos.X:F0}, {labelPos.Y:F0})");
                    await BotInput.ClickAt(labelPos);
                }
                // Priority B: Click entity bounds directly
                else if (monolith != null && monolith.IsValid)
                {
                    SimuLogger.Log("[START] Clicking Afflictionator entity bounds");
                    await BotInput.ClickEntity(gc, monolith);
                }
                // Priority C: Click ground coordinate directly
                else
                {
                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(monolithGrid.Value);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    await BotInput.ClickAt(BotInput.ClampToWindow(new Vector2(screenTarget.X, screenTarget.Y)));
                }

                // Check if wave activation succeeded (monolith or stash becomes untargetable)
                await Task.Delay(500);
                bool nowStarted = (stash != null && stash.IsValid && !stash.IsTargetable) ||
                                  (monolith != null && (!monolith.IsValid || !monolith.IsTargetable));

                if (nowStarted)
                {
                    OnWaveStarted(gc, state);
                }
                else
                {
                    SimuLogger.Log("[START] Click sent, waiting for wave to start...");
                }
            }
        }

        private Vector2 GetWalkStepTowards(Vector2 playerGrid, Vector2 targetGrid)
        {
            var waypoints = _arenaManager.CurrentProfile?.CircuitWaypoints;
            if (waypoints != null && waypoints.Count > 0)
            {
                float curDistToGoal = Vector2.Distance(playerGrid, targetGrid);
                Vector2? candidate = null;
                float bestDistToGoal = curDistToGoal - 4f;

                foreach (var wp in waypoints)
                {
                    var wpVec = wp.ToVector2();
                    float distFromPlayer = Vector2.Distance(playerGrid, wpVec);
                    if (distFromPlayer > 3f && distFromPlayer <= 24f)
                    {
                        float distToGoal = Vector2.Distance(wpVec, targetGrid);
                        if (distToGoal < bestDistToGoal)
                        {
                            bestDistToGoal = distToGoal;
                            candidate = wpVec;
                        }
                    }
                }

                if (candidate.HasValue)
                {
                    return candidate.Value;
                }
            }

            // Fallback: direct line step of 16 units towards targetGrid
            var dir = targetGrid - playerGrid;
            return dir.Length() > 16f ? playerGrid + Vector2.Normalize(dir) * 16f : targetGrid;
        }

        private bool TryGetMonolithLabelPos(GameController gc, Entity? monolith, SimuState? state, out Vector2 clickPos)
        {
            clickPos = Vector2.Zero;
            try
            {
                // Source 1: ItemsOnGroundLabelsVisible
                var labelsVisible = gc.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible;
                if (labelsVisible != null)
                {
                    var mLabel = labelsVisible.FirstOrDefault(l =>
                        ((monolith != null && l.ItemOnGround?.Id == monolith.Id) ||
                         l.ItemOnGround?.Metadata?.Contains("Objects/Afflictionator") == true) &&
                        l.IsVisible && l.Label != null && l.Label.IsVisible);

                    if (mLabel?.Label != null)
                    {
                        if (state != null)
                        {
                            var w = TryParseWaveNumber(mLabel.Label);
                            if (w > 0) state.CurrentWave = w;
                        }

                        var rect = mLabel.Label.GetClientRect();
                        var center = new Vector2(rect.Center.X, rect.Center.Y);
                        if (BotInput.IsSafeFromUI(center))
                        {
                            clickPos = center;
                            return true;
                        }
                    }
                }

                // Source 2: ItemsOnGroundLabelElement.VisibleGroundItemLabels
                var labelElement = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labelElement != null)
                {
                    var mLabel = labelElement.FirstOrDefault(l =>
                        l.Label != null && l.Label.IsVisible &&
                        ((monolith != null && l.Entity?.Id == monolith.Id) ||
                         l.Entity?.Metadata?.Contains("Objects/Afflictionator") == true ||
                         l.Label.Text?.Contains("Strangely Familiar Entity", StringComparison.OrdinalIgnoreCase) == true ||
                         l.Label.Text?.Contains("Afflictionator", StringComparison.OrdinalIgnoreCase) == true));

                    if (mLabel?.Label != null)
                    {
                        if (state != null)
                        {
                            var w = TryParseWaveNumber(mLabel.Label);
                            if (w > 0) state.CurrentWave = w;
                        }

                        var rect = mLabel.Label.GetClientRect();
                        var center = new Vector2(rect.Center.X, rect.Center.Y);
                        if (BotInput.IsSafeFromUI(center))
                        {
                            clickPos = center;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static int TryParseWaveNumber(ExileCore.PoEMemory.Element? element)
        {
            if (element == null) return 0;
            if (!string.IsNullOrEmpty(element.Text))
            {
                var match = System.Text.RegularExpressions.Regex.Match(element.Text, @"Wave\s+(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int w))
                    return w;
            }
            foreach (var child in element.Children)
            {
                int w = TryParseWaveNumber(child);
                if (w > 0) return w;
            }
            return 0;
        }

        public void TickWaveActive(GameController gc, SimuState state)
        {
            var player = gc.Player;
            if (player == null || _unstuckHandler.IsJumping) return;

            // Ensure combat skill is held
            BotInput.HoldKey(_settings.CombatKey.Value.Key);

            // Steer along circuit
            var nextWaypoint = _arenaManager.GetNextWaypoint(player.GridPosNum, _settings.WaypointJitter.Value);
            if (!nextWaypoint.HasValue)
            {
                // Safe fallback when no circuit waypoints: patrol between Monolith and Stash
                var mPos = _arenaManager.CurrentProfile?.MonolithPosition;
                var sPos = _arenaManager.CurrentProfile?.StashPosition;
                if (mPos.HasValue && sPos.HasValue)
                {
                    float distToM = Vector2.Distance(player.GridPosNum, mPos.Value);
                    nextWaypoint = distToM < 25f ? sPos.Value : mPos.Value;
                }
                else
                {
                    nextWaypoint = player.GridPosNum;
                }
            }

            // Calculate steering direction from player to next waypoint
            var playerGrid = player.GridPosNum;

            // Check if stuck along the circuit (e.g. wall, balustrade, obstacle, mob pack)
            if (_settings.UseMovementSkill.Value && _unstuckHandler.CanJump())
            {
                if (_unstuckHandler.IsStuck(playerGrid, 2.5f, 0.7))
                {
                    _ = _unstuckHandler.TryJumpTowards(gc, _settings, playerGrid, nextWaypoint.Value, resumeCombatHold: true);
                    return;
                }
            }

            var steerDir = nextWaypoint.Value - playerGrid;
            if (steerDir.LengthSquared() < 0.001f)
                return;

            // Always project steering with a steady forward lead of 18 grid units (~240 pixels on screen).
            // If direct line-of-sight is clear, project steady forward lead of 18 grid units.
            // If blocked by a wall or obstacle (e.g. gazebo balustrade, stairs, corner), use pathfinder to steer around it!
            Vector2 steerGrid;
            if (TerrainPathfinder.HasLineOfSight(gc, playerGrid, nextWaypoint.Value, 3))
            {
                steerGrid = playerGrid + Vector2.Normalize(steerDir) * 18f;
            }
            else
            {
                steerGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, nextWaypoint.Value, 18f);
            }

            var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(steerGrid);
            var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
            var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
            var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));

            // Move mouse & keep left click held
            Input.SetCursorPos(moveVec);
            BotInput.HoldLeftMouse();

            state.StatusText = $"Wave {state.CurrentWave}/15 Active - Holding {_settings.CombatKey.Value.Key} & Running Circuit";

            // Check wave completion
            if (IsWaveComplete(gc, state))
            {
                OnWaveEnded(state);
            }
        }

        private bool IsWaveComplete(GameController gc, SimuState state)
        {
            // Give wave at least 8 seconds before checking completion (initial spawn delay)
            if ((DateTime.Now - _waveActiveStartTime).TotalSeconds < 8.0)
                return false;

            // Primary: Stash became targetable (monsters all dead)
            if (_arenaManager.StashEntity != null && _arenaManager.StashEntity.IsValid && _arenaManager.StashEntity.IsTargetable)
            {
                return true;
            }

            // Secondary: Monolith became targetable again
            if (_arenaManager.MonolithEntity != null && _arenaManager.MonolithEntity.IsValid && _arenaManager.MonolithEntity.IsTargetable)
            {
                return true;
            }

            return false;
        }

        public void OnWaveStarted(GameController gc, SimuState state, int waveOverride = 0)
        {
            if (waveOverride > 0)
            {
                state.CurrentWave = waveOverride;
            }
            else if (state.CurrentWave < 1)
            {
                state.CurrentWave = 1;
            }
            _lootZoneReached = false;
            state.WaveStartedAt = DateTime.Now;
            _waveActiveStartTime = DateTime.Now;
            _arenaManager.ResetCircuitIndex(gc, gc?.Player?.GridPosNum);
            state.TransitionTo(SimPhase.WaveActive, $"Wave {state.CurrentWave}/15 Active -> Running Circuit");
        }

        private void OnWaveEnded(SimuState state)
        {
            // Release all combat keys and movement
            BotInput.ReleaseKey(_settings.CombatKey.Value.Key);
            BotInput.ReleaseLeftMouse();
            BotInput.ReleaseAllKeys();

            state.SettleStartedAt = DateTime.Now;
            state.TransitionTo(SimPhase.WaveEndSettle, $"Wave {state.CurrentWave} ended -> Moving to Loot Center");
        }

        public static Vector2 DeflectFromEntrancePortal(GameController gc, Vector2 targetPos)
        {
            try
            {
                // 1. Check portal entities near targetPos
                var portal = gc.EntityListWrapper?.OnlyValidEntities?
                    .FirstOrDefault(e => e.Metadata?.Contains("Metadata/MiscellaneousObjects/Portal") == true ||
                                         e.Metadata?.Contains("Objects/Portal") == true);
                if (portal != null)
                {
                    var p3D = gc.IngameState.Data.ToWorldWithTerrainHeight(portal.GridPosNum);
                    var pScreen = gc.IngameState.Camera.WorldToScreen(p3D);
                    var pVec = new Vector2(pScreen.X, pScreen.Y);
                    if (Vector2.Distance(targetPos, pVec) < 110f)
                    {
                        var away = targetPos - pVec;
                        if (away.LengthSquared() < 1f) away = new Vector2(0, -1);
                        targetPos = pVec + Vector2.Normalize(away) * 140f;
                    }
                }

                // 2. Check visible portal ground labels (e.g. "Hideout", "Portal")
                var labels = gc.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labels != null)
                {
                    foreach (var l in labels)
                    {
                        if (l.Label != null && l.Label.IsVisible &&
                            (l.Label.Text?.Contains("Hideout", StringComparison.OrdinalIgnoreCase) == true ||
                             l.Label.Text?.Contains("Portal", StringComparison.OrdinalIgnoreCase) == true))
                        {
                            var rect = l.Label.GetClientRect();
                            if (rect.Contains(targetPos.X, targetPos.Y))
                            {
                                targetPos = new Vector2(rect.Center.X, rect.Top - 35f);
                            }
                        }
                    }
                }
            }
            catch { }

            return BotInput.ClampToWindow(targetPos);
        }

        private static bool IsMonolithActive(Entity monolith, out int wave)
        {
            wave = 0;
            if (monolith.TryGetComponent<StateMachine>(out var sm))
            {
                var active = sm.States.FirstOrDefault(s => s.Name == "active")?.Value ?? 0;
                var goodbye = sm.States.FirstOrDefault(s => s.Name == "goodbye")?.Value ?? 0;
                wave = (int)(sm.States.FirstOrDefault(s => s.Name == "wave")?.Value ?? 0);
                return active > 0 && goodbye == 0;
            }
            return false;
        }
    }
}
