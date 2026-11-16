using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;

namespace SimuFarmer.Systems
{
    public class UnstuckHandler
    {
        private Vector2 _lastPlayerPos = Vector2.Zero;
        private DateTime _lastPosChangeTime = DateTime.Now;
        private DateTime _lastMovementSkillTime = DateTime.MinValue;
        private volatile bool _isJumping = false;

        public bool IsJumping => _isJumping;

        public bool CanJump()
        {
            return !_isJumping && (DateTime.Now - _lastMovementSkillTime).TotalMilliseconds >= 750;
        }

        public bool IsStuck(Vector2 currentPos, float minMoveDist = 2.5f, double stuckSeconds = 0.5)
        {
            if (_isJumping) return false;

            if (_lastPlayerPos == Vector2.Zero)
            {
                _lastPlayerPos = currentPos;
                _lastPosChangeTime = DateTime.Now;
                return false;
            }

            if (Vector2.Distance(currentPos, _lastPlayerPos) > minMoveDist)
            {
                _lastPlayerPos = currentPos;
                _lastPosChangeTime = DateTime.Now;
                return false;
            }

            return (DateTime.Now - _lastPosChangeTime).TotalSeconds >= stuckSeconds;
        }

        public void Reset(Vector2 currentPos)
        {
            _lastPlayerPos = currentPos;
            _lastPosChangeTime = DateTime.Now;
        }

        public async Task<bool> TryJumpTowards(GameController gc, Settings settings, Vector2 playerGrid, Vector2 targetGrid, bool resumeCombatHold = false)
        {
            if (!settings.UseMovementSkill.Value)
                return false;

            if (!CanJump())
                return false;

            _isJumping = true;
            try
            {
                var dir = targetGrid - playerGrid;
                float dist = dir.Length();
                if (dist < 1.0f)
                    return false;

                // Project jump target ~25-35 grid units towards target
                float jumpDist = Math.Clamp(dist, 18f, 35f);
                var jumpGrid = playerGrid + Vector2.Normalize(dir) * jumpDist;

                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(jumpGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));

                var dirScreen = new Vector2(screenTarget.X - charScreen.X, screenTarget.Y - charScreen.Y);
                if (dirScreen.LengthSquared() < 1f)
                    return false;

                // Place cursor ~220-300px away from character in direction of jump
                float cursorDist = Math.Clamp(dirScreen.Length(), 180f, 300f);
                var jumpCursorPos = new Vector2(charScreen.X, charScreen.Y) + Vector2.Normalize(dirScreen) * cursorDist;
                jumpCursorPos = BotInput.ClampToWindow(jumpCursorPos);

                var combatKey = settings.CombatKey.Value.Key;
                var moveKey = settings.MovementKey.Value.Key;

                SimuLogger.Log($"[UNSTUCK] Stuck near wall -> Using {moveKey} towards target (dist {dist:F0})");

                // If combat skill was being held, release it briefly so movement skill isn't blocked by channeling
                if (resumeCombatHold)
                {
                    BotInput.ReleaseKey(combatKey);
                    BotInput.ReleaseLeftMouse();
                    await Task.Delay(40);
                }

                // Move mouse and cast movement skill
                Input.SetCursorPos(jumpCursorPos);
                await Task.Delay(30);
                await BotInput.TapKey(moveKey);

                _lastMovementSkillTime = DateTime.Now;
                _lastPosChangeTime = DateTime.Now;
                _lastPlayerPos = playerGrid;

                // Allow jump/dash animation to begin and move the character
                await Task.Delay(180);

                // Resume holding combat if needed
                if (resumeCombatHold && settings.IsRunning.Value)
                {
                    BotInput.HoldKey(combatKey);
                    BotInput.HoldLeftMouse();
                }

                return true;
            }
            finally
            {
                _isJumping = false;
            }
        }
    }
}
