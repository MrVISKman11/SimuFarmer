using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using SimuFarmer.Models;

namespace SimuFarmer.Systems
{
    public class ArenasContainer
    {
        public List<ArenaProfile> Arenas { get; set; } = new();
    }

    public class ArenaManager
    {
        private readonly string _configFilePath;
        private readonly Random _rng = new();

        public ArenasContainer Container { get; private set; } = new();
        public ArenaProfile? CurrentProfile { get; private set; }
        public bool IsInSimulacrumArea { get; private set; }

        public Entity? MonolithEntity { get; private set; }
        public Entity? StashEntity { get; private set; }

        // Recording
        public bool IsRecording { get; private set; }
        private DateTime _lastRecordSample = DateTime.MinValue;

        // Path navigation index
        private int _currentWaypointIndex = 0;
        private Vector2? _currentJitteredTarget = null;
        private DateTime _waypointTargetStartTime = DateTime.MinValue;

        public ArenaManager(string pluginDirectory)
        {
            _configFilePath = Path.Combine(pluginDirectory, "Config", "arenas.json");
            LoadConfig();
        }

        public void LoadConfig()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var container = JsonConvert.DeserializeObject<ArenasContainer>(json);
                    if (container != null && container.Arenas.Count > 0)
                    {
                        Container = container;
                        return;
                    }
                }
            }
            catch { }

            // Defaults if file missing
            Container = new ArenasContainer
            {
                Arenas = new List<ArenaProfile>
                {
                    new("Lunacy's Watch", "Lunacy"),
                    new("The Bridge Enraptured", "Bridge"),
                    new("The Syndrome Encampment", "Syndrome"),
                    new("Hysteriagate", "Hysteriagate"),
                    new("Oriath Delusion", "Oriath")
                }
            };
            SaveConfig();
        }

        public void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(Container, Formatting.Indented);
                File.WriteAllText(_configFilePath, json);
            }
            catch { }
        }

        public void UpdateCurrentArena(GameController gc)
        {
            var areaName = gc.Area?.CurrentArea?.Name ?? "";
            var areaId = gc.Area?.CurrentArea?.Area?.Id ?? "";

            // Check if matches any of the 5
            var match = Container.Arenas.FirstOrDefault(a =>
                areaName.Contains(a.Name, StringComparison.OrdinalIgnoreCase) ||
                areaName.Contains(a.AreaKey, StringComparison.OrdinalIgnoreCase) ||
                areaId.Contains(a.AreaKey, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                IsInSimulacrumArea = true;
                if (CurrentProfile != match)
                {
                    CurrentProfile = match;
                    _currentWaypointIndex = 0;
                    _currentJitteredTarget = null;
                }
            }
            else
            {
                // Fallback: check if Afflictionator entity exists anywhere in entity list
                bool hasAfflictionator = gc.EntityListWrapper.OnlyValidEntities
                    .Any(e => e.Metadata?.Contains("Objects/Afflictionator") == true);

                if (hasAfflictionator && !gc.Area.CurrentArea.IsHideout)
                {
                    IsInSimulacrumArea = true;
                    if (CurrentProfile == null && Container.Arenas.Count > 0)
                    {
                        CurrentProfile = Container.Arenas.FirstOrDefault(a => a.AreaKey.Contains("Oriath", StringComparison.OrdinalIgnoreCase)) ?? Container.Arenas[0];
                    }
                }
                else
                {
                    IsInSimulacrumArea = false;
                    CurrentProfile = null;
                }
            }

            // Update Monolith & Stash entity references
            if (IsInSimulacrumArea)
            {
                ResolveEntities(gc);
            }
            else
            {
                MonolithEntity = null;
                StashEntity = null;
            }
        }

        private void ResolveEntities(GameController gc)
        {
            // Find monolith
            if (MonolithEntity == null || !MonolithEntity.IsValid)
            {
                MonolithEntity = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Objects/Afflictionator") == true);

                if (MonolithEntity != null && CurrentProfile != null && !CurrentProfile.MonolithPosition.HasValue)
                {
                    CurrentProfile.MonolithPosition = MonolithEntity.GridPosNum;
                    SaveConfig();
                }
            }

            // Find stash
            if (StashEntity == null || !StashEntity.IsValid)
            {
                StashEntity = gc.EntityListWrapper.OnlyValidEntities
                    .FirstOrDefault(e => e.Metadata?.Contains("Metadata/MiscellaneousObjects/Stash") == true);

                if (StashEntity != null && CurrentProfile != null && !CurrentProfile.StashPosition.HasValue)
                {
                    CurrentProfile.StashPosition = StashEntity.GridPosNum;
                    SaveConfig();
                }
            }

            // Derive loot center (near monolith or stash)
            if (CurrentProfile != null && !CurrentProfile.LootCenterPosition.HasValue)
            {
                if (CurrentProfile.MonolithPosition.HasValue)
                {
                    CurrentProfile.LootCenterPosition = CurrentProfile.MonolithPosition.Value;
                }
                else if (CurrentProfile.StashPosition.HasValue)
                {
                    CurrentProfile.LootCenterPosition = CurrentProfile.StashPosition.Value;
                }
            }
        }

        public void SetCurrentAsLootCenter(Vector2 pos)
        {
            if (CurrentProfile == null) return;
            CurrentProfile.LootCenterPosition = pos;
            SaveConfig();
            SimuLogger.Log($"[ARENA] Calibrated Loot Center for {CurrentProfile.Name} to ({pos.X:F0}, {pos.Y:F0})");
        }

        public void SetCurrentAsMonolith(Vector2 pos)
        {
            if (CurrentProfile == null) return;
            CurrentProfile.MonolithPosition = pos;
            SaveConfig();
            SimuLogger.Log($"[ARENA] Calibrated Monolith for {CurrentProfile.Name} to ({pos.X:F0}, {pos.Y:F0})");
        }

        // --- Live Circuit Recording ---

        public void StartRecording()
        {
            if (CurrentProfile == null) return;
            IsRecording = true;
            _lastRecordSample = DateTime.MinValue;
        }

        public void StopRecording()
        {
            IsRecording = false;
            SaveConfig();
        }

        public void ClearCircuit()
        {
            if (CurrentProfile == null) return;
            CurrentProfile.CircuitWaypoints.Clear();
            SaveConfig();
        }

        public void TickRecording(Vector2 playerGrid)
        {
            if (!IsRecording || CurrentProfile == null) return;
            if ((DateTime.Now - _lastRecordSample).TotalMilliseconds < 250) return;

            var waypoints = CurrentProfile.CircuitWaypoints;
            if (waypoints.Count == 0 || Vector2.Distance(waypoints.Last().ToVector2(), playerGrid) >= 15f)
            {
                waypoints.Add(playerGrid);
                _lastRecordSample = DateTime.Now;
            }
        }

        // --- Waypoint Navigation ---

        public Vector2? GetNextWaypoint(Vector2 playerGrid, float jitterRange)
        {
            if (CurrentProfile == null || CurrentProfile.CircuitWaypoints.Count == 0)
                return null;

            var waypoints = CurrentProfile.CircuitWaypoints;
            int count = waypoints.Count;
            if (_currentWaypointIndex >= count)
                _currentWaypointIndex = 0;

            Vector2 baseTarget = waypoints[_currentWaypointIndex].ToVector2();
            float distToTarget = Vector2.Distance(playerGrid, baseTarget);

            // Previous waypoint (for segment progress along the path)
            int prevIdx = (_currentWaypointIndex - 1 + count) % count;
            Vector2 prevTarget = waypoints[prevIdx].ToVector2();

            // Progress along current segment (prevTarget -> baseTarget)
            var seg = baseTarget - prevTarget;
            float segLenSq = seg.LengthSquared();
            float progress = 0f;
            float perpDist = float.MaxValue;
            if (segLenSq > 1f)
            {
                progress = Vector2.Dot(playerGrid - prevTarget, seg) / segLenSq;
                var proj = prevTarget + Math.Clamp(progress, 0f, 1f) * seg;
                perpDist = Vector2.Distance(playerGrid, proj);
            }

            // Advance strictly 1 waypoint forward in sequence:
            // 1. Within 18 grid units of target waypoint
            // 2. Traveled past 85% of current segment AND within 12 units of the path (safe corner cut)
            // 3. Timed out on this node (> 3.5s)
            bool cornerCut = progress >= 0.85f && perpDist <= 12f;
            bool arrived = distToTarget < 18f || cornerCut;
            bool timedOut = _waypointTargetStartTime != DateTime.MinValue && (DateTime.Now - _waypointTargetStartTime).TotalSeconds > 3.5;

            if (arrived || timedOut)
            {
                _currentWaypointIndex = (_currentWaypointIndex + 1) % count;
                baseTarget = waypoints[_currentWaypointIndex].ToVector2();
                _currentJitteredTarget = null;
                _waypointTargetStartTime = DateTime.Now;
            }

            if (!_currentJitteredTarget.HasValue)
            {
                if (jitterRange > 0.1f)
                {
                    float jx = (float)(_rng.NextDouble() * 2 - 1) * jitterRange;
                    float jy = (float)(_rng.NextDouble() * 2 - 1) * jitterRange;
                    _currentJitteredTarget = baseTarget + new Vector2(jx, jy);
                }
                else
                {
                    _currentJitteredTarget = baseTarget;
                }
                _waypointTargetStartTime = DateTime.Now;
            }

            return _currentJitteredTarget;
        }

        public void ResetCircuitIndex(GameController? gc = null, Vector2? playerPos = null)
        {
            _currentJitteredTarget = null;
            _waypointTargetStartTime = DateTime.Now;

            if (CurrentProfile == null || CurrentProfile.CircuitWaypoints.Count == 0)
            {
                _currentWaypointIndex = 0;
                return;
            }

            if (playerPos.HasValue)
            {
                var waypoints = CurrentProfile.CircuitWaypoints;
                int bestIdx = 0;
                float bestDist = float.MaxValue;
                bool foundLos = false;

                // Priority 1: Pick nearest waypoint that has clear line-of-sight (no wall/balustrade blocking)
                if (gc != null)
                {
                    for (int i = 0; i < waypoints.Count; i++)
                    {
                        var wp = waypoints[i].ToVector2();
                        float d = Vector2.Distance(playerPos.Value, wp);
                        if (TerrainPathfinder.HasLineOfSight(gc, playerPos.Value, wp, 3))
                        {
                            if (!foundLos || d < bestDist)
                            {
                                foundLos = true;
                                bestDist = d;
                                bestIdx = i;
                            }
                        }
                    }
                }

                // Priority 2: Fallback if behind walls where no waypoint has direct line-of-sight
                if (!foundLos)
                {
                    bestDist = float.MaxValue;
                    for (int i = 0; i < waypoints.Count; i++)
                    {
                        float d = Vector2.Distance(playerPos.Value, waypoints[i].ToVector2());
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestIdx = i;
                        }
                    }
                }

                _currentWaypointIndex = bestIdx;
                SimuLogger.Log($"[ARENA] Circuit starting from nearest waypoint #{bestIdx + 1} (dist {bestDist:F0}, LoS: {foundLos})");
            }
            else
            {
                _currentWaypointIndex = 0;
            }
        }
    }
}
