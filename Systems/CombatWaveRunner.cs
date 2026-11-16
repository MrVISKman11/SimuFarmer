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

        public CombatWaveRunner(ArenaManager arenaManager, Settings settings, UnstuckHandler unstuckHandler)
        {
            _arenaManager = arenaManager;
            _settings = settings;
            _unstuckHandler = unstuckHandler;
        }

        public async Task TickArenaInit(GameController gc, SimuState state)
        {
            state.StatusText = "Arena Init: Breaking grace period & activating aura";

            // Take a step to break grace period
            var player = gc.Player;
            if (player == null) return;

            var playerGrid = player.GridPosNum;
            var targetGrid = playerGrid + new Vector2(5, 5);
            var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(targetGrid);
            var screenPos = gc.IngameState.Camera.WorldToScreen(world3D);
            var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
            var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenPos.X, screenPos.Y));

            await BotInput.ClickAt(moveVec);
            await Task.Delay(250);

            // Press Aura key if not done this instance
            if (!state.AuraActivatedThisRun)
            {
                await BotInput.TapKey(_settings.AuraKey.Value.Key);
                state.AuraActivatedThisRun = true;
                await Task.Delay(200);
            }

            state.TransitionTo(SimPhase.StartingWave, "Aura active -> Ready to start wave");
        }

        public async Task TickStartingWave(GameController gc, SimuState state)
        {
            var monolith = _arenaManager.MonolithEntity;
            var player = gc.Player;
            if (player == null) return;

            // 1. Check if wave is already active
            if (monolith != null && monolith.IsValid && IsMonolithActive(monolith, out int currentWave))
            {
                OnWaveStarted(gc, state, currentWave);
                return;
            }

            var targetGrid = monolith?.GridPosNum ?? _arenaManager.CurrentProfile?.MonolithPosition;
            if (!targetGrid.HasValue)
            {
                state.StatusText = "Searching for Afflictionator monolith...";
                return;
            }

            var playerGrid = player.GridPosNum;
            float pDist = Vector2.Distance(playerGrid, targetGrid.Value);

            // 2. PRIMARY REQUIREMENT: If distant (> 18 units), WALK to the monolith first!
            // Never attempt to click the Afflictionator label/box from afar because PoE will not move the character.
            if (pDist > 18f)
            {
                state.StatusText = $"Walking to Afflictionator (dist: {pDist:F0})";

                // Check if stuck trying to reach the Afflictionator (e.g. wall/balustrade blocking)
                if (_unstuckHandler.IsStuck(playerGrid, 2.5f, 0.5))
                {
                    if (await _unstuckHandler.TryJumpTowards(gc, _settings, playerGrid, targetGrid.Value, resumeCombatHold: false))
                    {
                        return;
                    }
                }

                var stepGrid = TerrainPathfinder.GetNextStepTowards(gc, playerGrid, targetGrid.Value, 16f);
                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
                return;
            }

            // 3. IN RANGE (<= 18 units): Now and only now, click the Afflictionator to start the wave
            if ((DateTime.Now - _lastMonolithClick).TotalMilliseconds >= 1000)
            {
                state.StatusText = "Clicking Afflictionator to start wave";
                SimuLogger.Log($"[START] In range of Afflictionator (dist {pDist:F1}) -> Clicking to start wave");
                _lastMonolithClick = DateTime.Now;

                // Priority A: Click visible label if on screen
                if (TryGetMonolithLabelPos(gc, monolith, out var labelPos))
                {
                    await BotInput.ClickAt(labelPos);
                }
                // Priority B: Click entity bounds directly
                else if (monolith != null && monolith.IsValid)
                {
                    await BotInput.ClickEntity(gc, monolith);
                }
                // Priority C: Click ground coordinate directly
                else
                {
                    var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(targetGrid.Value);
                    var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                    await BotInput.ClickAt(BotInput.ClampToWindow(new Vector2(screenTarget.X, screenTarget.Y)));
                }

                // Check if already active or poll for activation
                await Task.Delay(400);
                if (monolith != null && monolith.IsValid && IsMonolithActive(monolith, out int activeWave))
                {
                    OnWaveStarted(gc, state, activeWave);
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

        private bool TryGetMonolithLabelPos(GameController gc, Entity? monolith, out Vector2 clickPos)
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
                        ((monolith != null && l.Entity?.Id == monolith.Id) ||
                         l.Entity?.Metadata?.Contains("Objects/Afflictionator") == true) &&
                        l.Label != null && l.Label.IsVisible);

                    if (mLabel?.Label != null)
                    {
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
            // Give wave at least 6 seconds before checking completion (so initial wave start state changes settle)
            if ((DateTime.Now - _waveActiveStartTime).TotalSeconds < 6.0)
                return false;

            // Primary: Stash became targetable
            if (_arenaManager.StashEntity != null && _arenaManager.StashEntity.IsValid && _arenaManager.StashEntity.IsTargetable)
            {
                return true;
            }

            // Fallback: Monolith StateMachine active == 0
            var monolith = _arenaManager.MonolithEntity;
            if (monolith != null && monolith.IsValid)
            {
                if (monolith.TryGetComponent<StateMachine>(out var sm))
                {
                    var activeVal = sm.States.FirstOrDefault(s => s.Name == "active")?.Value ?? 0;
                    if (activeVal == 0) return true;
                }
            }

            return false;
        }

        public void OnWaveStarted(GameController gc, SimuState state, int waveFromMonolith)
        {
            state.CurrentWave = waveFromMonolith > 0 ? waveFromMonolith : state.CurrentWave + 1;
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
