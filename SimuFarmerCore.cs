using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using Hexa.NET.ImGui;
using SharpDX;
using SimuFarmer.Models;
using SimuFarmer.Systems;
using Vector2 = System.Numerics.Vector2;

namespace SimuFarmer
{
    public class SimuFarmerCore : BaseSettingsPlugin<Settings>
    {
        private ArenaManager _arenaManager = null!;
        private CombatWaveRunner _combatWaveRunner = null!;
        private LootCollector _lootCollector = null!;
        private StashDumper _stashDumper = null!;
        private MapDeviceHandler _mapDeviceHandler = null!;
        private UnstuckHandler _unstuckHandler = null!;
        private SimuState _state = null!;

        private DateTime _lastAreaCheck = DateTime.MinValue;
        private DateTime _lastPortalKeyTime = DateTime.MinValue;
        private DateTime _lastFullscreenDismiss = DateTime.MinValue;

        public override bool Initialise()
        {
            Name = "SimuFarmer";
            SimuLogger.Initialize(DirectoryFullName);
            _state = new SimuState();
            _unstuckHandler = new UnstuckHandler();

            _arenaManager = new ArenaManager(DirectoryFullName);
            _combatWaveRunner = new CombatWaveRunner(_arenaManager, Settings, _unstuckHandler);
            _lootCollector = new LootCollector(_arenaManager, Settings, _unstuckHandler);
            _stashDumper = new StashDumper(Settings);
            _mapDeviceHandler = new MapDeviceHandler(Settings);

            return true;
        }

        public override void AreaChange(AreaInstance area)
        {
            BotInput.ReleaseAllKeys();
            _unstuckHandler?.Reset(Vector2.Zero);
            _arenaManager.UpdateCurrentArena(GameController);

            if (GameController.Area.CurrentArea.IsHideout)
            {
                _state.TransitionTo(SimPhase.InHideout, "In Hideout - Ready to dump stash & start device");
            }
            else if (_arenaManager.IsInSimulacrumArea)
            {
                _state.ResetArenaSession();
                _state.TransitionTo(SimPhase.ArenaInit, $"Entered {_arenaManager.CurrentProfile?.Name} -> Preparing");
            }
            else
            {
                _state.TransitionTo(SimPhase.Idle, $"In unknown area: {area.Name}");
            }
        }

        public override Job? Tick()
        {
            // Hotkey toggle
            if (Settings.RunBotHotkey.PressedOnce())
            {
                Settings.IsRunning.Value = !Settings.IsRunning.Value;
                if (!Settings.IsRunning.Value)
                {
                    BotInput.ReleaseAllKeys();
                    _state.TransitionTo(SimPhase.Idle, "Paused by F9 toggle");
                }
                else
                {
                    _arenaManager.UpdateCurrentArena(GameController);
                    if (GameController.Area.CurrentArea.IsHideout)
                    {
                        _state.TransitionTo(SimPhase.InHideout, "Started bot in Hideout");
                    }
                    else if (_arenaManager.IsInSimulacrumArea)
                    {
                        _state.TransitionTo(SimPhase.ArenaInit, "Started bot in Arena");
                    }
                }
            }

            if (!Settings.IsRunning.Value)
            {
                return null;
            }

            // Periodic location verification
            if ((DateTime.Now - _lastAreaCheck).TotalSeconds >= 1.0)
            {
                _arenaManager.UpdateCurrentArena(GameController);
                _lastAreaCheck = DateTime.Now;
            }

            BotInput.TickWatchdog();
            BotInput.WindowRect = GameController.Window.GetWindowRectangle();

            // Safety watchdog: If a fullscreen panel (e.g. MTX Shop or Passive Tree) is open, dismiss it with Escape
            var ingameUi = GameController.IngameState?.IngameUi;
            if (ingameUi?.FullscreenPanels?.Any(p => p.IsVisible) == true)
            {
                if ((DateTime.Now - _lastFullscreenDismiss).TotalMilliseconds >= 600)
                {
                    SimuLogger.Log("[SAFETY] Fullscreen panel (e.g. MTX Shop) detected open! Dismissing with Escape.");
                    _ = BotInput.TapKey(System.Windows.Forms.Keys.Escape);
                    _lastFullscreenDismiss = DateTime.Now;
                }
                return null;
            }

            // Wait if input gate is busy
            if (!BotInput.CanAct && _state.Phase != SimPhase.WaveActive)
            {
                return null;
            }

            // Safety reconciliation: if inside Simulacrum area, NEVER allow hideout phases
            if (_arenaManager.IsInSimulacrumArea)
            {
                if (_state.Phase == SimPhase.InHideout ||
                    _state.Phase == SimPhase.NavigatingToStash ||
                    _state.Phase == SimPhase.DumpingStash ||
                    _state.Phase == SimPhase.NavigatingToMapDevice ||
                    _state.Phase == SimPhase.InteractingMapDevice ||
                    _state.Phase == SimPhase.ActivatingMapDevice ||
                    _state.Phase == SimPhase.EnteringPortal)
                {
                    SimuLogger.Log($"[SAFETY] Inside arena but state was {_state.Phase}! Reconciling to StartingWave.");
                    _state.TransitionTo(SimPhase.StartingWave, "Reconciled to arena StartingWave");
                }
            }

            // Sync wave state directly from monolith StateMachine (read-only wave number sync)
            if (_arenaManager.IsInSimulacrumArea && _arenaManager.MonolithEntity != null && _arenaManager.MonolithEntity.IsValid)
            {
                if (_arenaManager.MonolithEntity.TryGetComponent<ExileCore.PoEMemory.Components.StateMachine>(out var sm))
                {
                    var wave = (int)(sm.States.FirstOrDefault(s => s.Name == "wave")?.Value ?? 0);
                    if (wave > 0)
                    {
                        _state.CurrentWave = wave;
                    }
                }
            }

            // State Machine dispatch
            switch (_state.Phase)
            {
                case SimPhase.InHideout:
                case SimPhase.NavigatingToStash:
                case SimPhase.DumpingStash:
                    _ = _stashDumper.TickDump(GameController, _state);
                    break;

                case SimPhase.NavigatingToMapDevice:
                case SimPhase.InteractingMapDevice:
                case SimPhase.ActivatingMapDevice:
                    _ = _mapDeviceHandler.TickMapDevice(GameController, _state);
                    break;

                case SimPhase.EnteringPortal:
                    _ = _mapDeviceHandler.TickEnterPortal(GameController, _state);
                    break;

                case SimPhase.ArenaInit:
                    _ = _combatWaveRunner.TickArenaInit(GameController, _state);
                    break;

                case SimPhase.StartingWave:
                    _ = _combatWaveRunner.TickStartingWave(GameController, _state);
                    break;

                case SimPhase.WaveActive:
                    _combatWaveRunner.TickWaveActive(GameController, _state);
                    break;

                case SimPhase.WaveEndSettle:
                    _ = _lootCollector.TickSettle(GameController, _state);
                    break;

                case SimPhase.Looting:
                    _ = _lootCollector.TickLooting(GameController, _state);
                    break;

                case SimPhase.QuietWait:
                    _lootCollector.TickQuietWait(GameController, _state);
                    break;

                case SimPhase.Wave15Exit:
                    _ = HandleWave15Exit();
                    break;

                case SimPhase.LoadingZone:
                    // Safety watchdog: if stuck in LoadingZone for > 3.0 seconds, recover based on current area
                    if ((DateTime.Now - _state.PhaseEnteredAt).TotalSeconds >= 3.0)
                    {
                        if (GameController.Area.CurrentArea.IsHideout)
                        {
                            SimuLogger.Log("[RECOVERY] LoadingZone timed out in Hideout -> Re-entering portal");
                            _state.TransitionTo(SimPhase.EnteringPortal, "Retrying portal entry");
                        }
                        else if (_arenaManager.IsInSimulacrumArea)
                        {
                            SimuLogger.Log("[RECOVERY] LoadingZone timed out in Arena -> Retrying exit");
                            _state.TransitionTo(SimPhase.Wave15Exit, "Retrying exit portal");
                        }
                        else
                        {
                            _state.TransitionTo(SimPhase.Idle, "LoadingZone timeout in unknown area");
                        }
                    }
                    break;
            }

            return null;
        }

        private async Task HandleWave15Exit()
        {
            if (!Settings.AutoLoopReruns.Value)
            {
                _state.TransitionTo(SimPhase.Idle, "Wave 15 finished (Auto-loop disabled)");
                Settings.IsRunning.Value = false;
                return;
            }

            var player = GameController.Player;
            if (player == null) return;

            // Find a town portal that is CLOSE to the player (spawned by T)
            var nearbyPortals = GameController.EntityListWrapper.OnlyValidEntities
                .Where(e => e.IsTargetable && (e.Type == EntityType.TownPortal || e.Path?.Contains("Town_Portals") == true || e.Metadata?.Contains("Portal") == true))
                .OrderBy(e => Vector2.Distance(player.GridPosNum, e.GridPosNum))
                .ToList();

            var nearbyPortal = nearbyPortals.FirstOrDefault(p => Vector2.Distance(player.GridPosNum, p.GridPosNum) <= 45f);

            // If no portal near the player exists yet, press T to spawn one
            if (nearbyPortal == null)
            {
                if ((DateTime.Now - _lastPortalKeyTime).TotalSeconds >= 2.5)
                {
                    _state.StatusText = "Pressing 'T' to open Portal to Hideout";
                    SimuLogger.Log("[EXIT] Pressing 'T' to open Portal to Hideout");
                    await BotInput.TapKey(Settings.PortalKey.Value.Key);
                    _lastPortalKeyTime = DateTime.Now;
                    await Task.Delay(1000); // Give the portal animation time to spawn and become targetable
                }
                return;
            }

            // We have a nearby portal entity!
            var pDist = Vector2.Distance(player.GridPosNum, nearbyPortal.GridPosNum);
            if (pDist > Settings.InteractRadius.Value)
            {
                // Step towards the portal if not quite in interact range
                _state.StatusText = $"Walking to portal (dist: {pDist:F0})";

                // Check if stuck walking to portal
                if (_unstuckHandler.IsStuck(player.GridPosNum, 2.5f, 0.6))
                {
                    if (await _unstuckHandler.TryJumpTowards(GameController, Settings, player.GridPosNum, nearbyPortal.GridPosNum, resumeCombatHold: false))
                    {
                        return;
                    }
                }

                var stepGrid = TerrainPathfinder.GetNextStepTowards(GameController, player.GridPosNum, nearbyPortal.GridPosNum, 16f);
                var world3D = GameController.IngameState.Data.ToWorldWithTerrainHeight(stepGrid);
                var screenTarget = GameController.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = GameController.IngameState.Camera.WorldToScreen(GameController.IngameState.Data.ToWorldWithTerrainHeight(player.GridPosNum));
                var moveVec = BotInput.GetSafeMovementClick(new Vector2(charScreen.X, charScreen.Y), new Vector2(screenTarget.X, screenTarget.Y));
                await BotInput.ClickAt(moveVec);
                return;
            }

            // In range: try clicking on-screen label first, or entity
            _state.StatusText = "Clicking Portal to return to Hideout";
            SimuLogger.Log("[EXIT] Clicking Portal to return to Hideout");

            bool clicked = false;
            try
            {
                var labels = GameController.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
                if (labels != null)
                {
                    var pLabel = labels.FirstOrDefault(l => l.Entity?.Id == nearbyPortal.Id && l.Label != null && l.Label.IsVisible);
                    if (pLabel?.Label != null)
                    {
                        var rect = pLabel.Label.GetClientRect();
                        var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
                        await BotInput.ClickAt(clickPos);
                        clicked = true;
                    }
                }
            }
            catch { }

            if (!clicked)
            {
                await BotInput.ClickEntity(GameController, nearbyPortal);
            }

            _state.RunsCompleted++;
            await Task.Delay(1000);
        }

        public override void Render()
        {
            if (GameController.Player == null) return;

            // Live circuit recording
            if (_arenaManager.IsRecording)
            {
                _arenaManager.TickRecording(GameController.Player.GridPosNum);
                Graphics.DrawText($"[RECORDING CIRCUIT] {_arenaManager.CurrentProfile?.CircuitWaypoints.Count} points recorded! Walk your loop and click 'Save & Stop Recording' in the HUD", new Vector2(400, 80), Color.Yellow);
            }

            // In-Game UI Overlay
            RenderHud();

            // World debug lines
            if (Settings.DrawDebugOverlay.Value && _arenaManager.IsInSimulacrumArea)
            {
                RenderWorldOverlay();
            }
        }

        private void RenderHud()
        {
            if (!Settings.ShowHud.Value)
            {
                BotInput.HudRect = SharpDX.RectangleF.Empty;
                return;
            }

            ImGuiWindowFlags flags = ImGuiWindowFlags.None;
            if (Settings.IsRunning.Value && Settings.ClickThroughHud.Value)
            {
                flags |= ImGuiWindowFlags.NoMouseInputs;
            }

            ImGui.SetNextWindowSize(new Vector2(380, 420), ImGuiCond.FirstUseEver);
            bool isOpen = Settings.ShowHud.Value;
            bool hudBegun = ImGui.Begin("SimuFarmer HUD", ref isOpen, flags);

            try
            {
                if (hudBegun)
                {
                    Settings.ShowHud.Value = isOpen;
                    var hudPos = ImGui.GetWindowPos();
                    var hudSize = ImGui.GetWindowSize();
                    BotInput.HudRect = new SharpDX.RectangleF(hudPos.X, hudPos.Y, hudSize.X, hudSize.Y);

                    var statusColor = Settings.IsRunning.Value ? new System.Numerics.Vector4(0.2f, 1f, 0.2f, 1f) : new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f);
                    ImGui.TextColored(statusColor, Settings.IsRunning.Value ? "[RUNNING] Farming Active" : "[PAUSED] Press F9 to Start");

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Hide HUD"))
                    {
                        Settings.ShowHud.Value = false;
                    }

                    ImGui.Separator();
                    var arenaName = _arenaManager.CurrentProfile?.Name ?? (GameController.Area.CurrentArea.IsHideout ? "Hideout" : "Unknown");
                    ImGui.Text($"Location: {arenaName}");

                    ImGui.Text($"Wave: {_state.CurrentWave} / 15");
                    ImGui.SameLine();
                    if (ImGui.SmallButton(" - ") && _state.CurrentWave > 0) _state.CurrentWave--;
                    ImGui.SameLine();
                    if (ImGui.SmallButton(" + ") && _state.CurrentWave < 15) _state.CurrentWave++;

                    ImGui.Text($"Phase: {_state.Phase}");
                    ImGui.TextWrapped($"Status: {_state.StatusText}");

                    ImGui.Separator();
                    ImGui.Text($"Loot Picked: {_state.TotalLootPicked} (This run: {_state.WaveLootPicked})");
                    ImGui.Text($"Runs Completed: {_state.RunsCompleted}");

                    ImGui.Separator();
                    // Circuit & Position Calibration Controls
                    if (_arenaManager.IsInSimulacrumArea && _arenaManager.CurrentProfile != null)
                    {
                        int wpCount = _arenaManager.CurrentProfile.CircuitWaypoints.Count;
                        ImGui.Text($"Recorded Waypoints: {wpCount}");

                        if (!_arenaManager.IsRecording)
                        {
                            if (ImGui.Button("Record Circuit Loop"))
                            {
                                _arenaManager.StartRecording();
                            }
                        }
                        else
                        {
                            if (ImGui.Button("Save & Stop Recording"))
                            {
                                _arenaManager.StopRecording();
                            }
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Clear Waypoints"))
                        {
                            _arenaManager.ClearCircuit();
                        }

                        // Calibrate Loot Center & Monolith
                        if (GameController.Player != null)
                        {
                            if (ImGui.Button("Set Loot Center (Here)"))
                            {
                                _arenaManager.SetCurrentAsLootCenter(GameController.Player.GridPosNum);
                            }
                            ImGui.SameLine();
                            if (ImGui.Button("Set Monolith (Here)"))
                            {
                                _arenaManager.SetCurrentAsMonolith(GameController.Player.GridPosNum);
                            }
                        }
                    }

                    // In-Game Live Log Console
                    ImGui.Separator();
                    ImGui.Text("Recent Event Logs:");
                    bool childBegun = ImGui.BeginChild("LogConsole", new Vector2(0, 130));
                    try
                    {
                        if (childBegun)
                        {
                            var logs = SimuLogger.GetRecentLogs();
                            foreach (var l in logs)
                            {
                                if (l.Contains("[LOOT]"))
                                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.2f, 1f), l);
                                else if (l.Contains("[FSM]"))
                                    ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.75f, 1f, 1f), l);
                                else if (l.Contains("[SAFETY]"))
                                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), l);
                                else if (l.Contains("[ARENA]"))
                                    ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f), l);
                                else
                                    ImGui.TextUnformatted(l);
                            }
                            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                                ImGui.SetScrollHereY(1.0f);
                        }
                    }
                    finally
                    {
                        ImGui.EndChild();
                    }
                }
            }
            finally
            {
                ImGui.End();
            }

            if (!isOpen)
            {
                Settings.ShowHud.Value = false;
                BotInput.HudRect = SharpDX.RectangleF.Empty;
            }
        }

        private void RenderWorldOverlay()
        {
            var profile = _arenaManager.CurrentProfile;
            if (profile == null || profile.CircuitWaypoints.Count < 2) return;

            var waypoints = profile.CircuitWaypoints;
            for (int i = 0; i < waypoints.Count; i++)
            {
                var currGrid = waypoints[i].ToVector2();
                var nextGrid = waypoints[(i + 1) % waypoints.Count].ToVector2();

                var screenCurr = GameController.IngameState.Camera.WorldToScreen(GameController.IngameState.Data.ToWorldWithTerrainHeight(currGrid));
                var screenNext = GameController.IngameState.Camera.WorldToScreen(GameController.IngameState.Data.ToWorldWithTerrainHeight(nextGrid));

                Graphics.DrawLine(new Vector2(screenCurr.X, screenCurr.Y), new Vector2(screenNext.X, screenNext.Y), 2f, Color.Gold);
                Graphics.DrawCircle(new Vector2(screenCurr.X, screenCurr.Y), 4f, Color.Orange, 8);
                Graphics.DrawText($"#{i + 1}", new Vector2(screenCurr.X + 6, screenCurr.Y - 6), Color.White);
            }
        }

        public override void OnClose()
        {
            BotInput.ReleaseAllKeys();
        }
    }
}
