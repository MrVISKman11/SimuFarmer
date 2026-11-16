using System;

namespace SimuFarmer.Models
{
    public enum SimPhase
    {
        Idle,
        // Hideout phases
        InHideout,
        NavigatingToStash,
        DumpingStash,
        NavigatingToMapDevice,
        InteractingMapDevice,
        ActivatingMapDevice,
        EnteringPortal,
        LoadingZone,
        // Arena phases
        ArenaInit,
        StartingWave,
        WaveActive,
        WaveEndSettle,
        Looting,
        QuietWait,
        Wave15Exit
    }

    public class SimuState
    {
        public SimPhase Phase { get; private set; } = SimPhase.Idle;
        public string StatusText { get; set; } = "Idle";
        public DateTime PhaseEnteredAt { get; private set; } = DateTime.Now;
        public DateTime WaveStartedAt { get; set; } = DateTime.Now;
        public DateTime LastItemPickedAt { get; set; } = DateTime.MinValue;
        public DateTime SettleStartedAt { get; set; } = DateTime.MinValue;

        public int CurrentWave { get; set; } = 0;
        public int RunsCompleted { get; set; } = 0;
        public int TotalLootPicked { get; set; } = 0;
        public int WaveLootPicked { get; set; } = 0;
        public bool AuraActivatedThisRun { get; set; } = false;

        public void TransitionTo(SimPhase newPhase, string detail = "")
        {
            Phase = newPhase;
            PhaseEnteredAt = DateTime.Now;
            if (!string.IsNullOrEmpty(detail))
            {
                StatusText = detail;
            }
            else
            {
                StatusText = newPhase.ToString();
            }

            Systems.SimuLogger.Log($"[FSM] {newPhase}: {StatusText}");
        }

        public void ResetArenaSession()
        {
            CurrentWave = 0;
            WaveLootPicked = 0;
            AuraActivatedThisRun = false;
            LastItemPickedAt = DateTime.MinValue;
            SettleStartedAt = DateTime.MinValue;
        }

        public void ResetAll()
        {
            ResetArenaSession();
            Phase = SimPhase.Idle;
            StatusText = "Idle";
        }
    }
}
