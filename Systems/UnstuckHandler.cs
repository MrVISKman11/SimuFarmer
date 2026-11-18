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
        private int _consecutiveStuckCount = 0;

        public bool IsJumping => _isJumping;
        public int ConsecutiveStuckCount => _consecutiveStuckCount;

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
                _consecutiveStuckCount = 0;
                return false;
            }

            if (Vector2.Distance(currentPos, _lastPlayerPos) > minMoveDist)
            {
                _lastPlayerPos = currentPos;
                _lastPosChangeTime = DateTime.Now;
                _consecutiveStuckCount = 0;
                return false;
            }

            return (DateTime.Now - _lastPosChangeTime).TotalSeconds >= stuckSeconds;
        }

        public void Reset(Vector2 currentPos)
        {
            _lastPlayerPos = currentPos;
            _lastPosChangeTime = DateTime.Now;
            _consecutiveStuckCount = 0;
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

                _consecutiveStuckCount++;
                if (_consecutiveStuckCount > 8)
                {
                    _consecutiveStuckCount = 5; // Keep cycling opposite/disengage until unstuck
                }

                bool isOppositeJump = _consecutiveStuckCount >= 5;
                Vector2 jumpDir = Vector2.Normalize(dir);
                float jumpDist = Math.Clamp(dist, 18f, 35f);
                float chosenAngleOffset = 0f;

                if (isOppositeJump)
                {
                    // Target wall is too thick to jump forward over -> Jump into a cone behind (flanking diagonal)
                    // This disengages away from the wall AND breaks the line-of-approach so pathfinding doesn't walk right back into it!
                    jumpDist = 24f;
                    float forwardAngle = MathF.Atan2(dir.Y, dir.X);

                    // Choose diagonal flanks in the cone behind (115° to 155° off forward)
                    // Alternating between backward-left (+135°) and backward-right (-135°)
                    bool preferLeft = (_consecutiveStuckCount % 2 == 1);
                    float[] coneAngles = preferLeft
                        ? new[] { 2.356f, 2.007f, 2.705f, -2.356f, -2.007f, -2.705f, 1.571f, -1.571f, 3.1415f }
                        : new[] { -2.356f, -2.007f, -2.705f, 2.356f, 2.007f, 2.705f, -1.571f, 1.571f, 3.1415f };

                    // Default to preferred diagonal backward
                    chosenAngleOffset = coneAngles[0];
                    jumpDir = new Vector2(MathF.Cos(forwardAngle + chosenAngleOffset), MathF.Sin(forwardAngle + chosenAngleOffset));

                    // Verify against pathfinding grid to pick the best open walkable spot in the cone behind
                    var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
                    if (pfGrid != null && pfGrid.Length > 0)
                    {
                        int rows = pfGrid.Length;
                        int cols = pfGrid[0].Length;

                        foreach (var offset in coneAngles)
                        {
                            var candDir = new Vector2(MathF.Cos(forwardAngle + offset), MathF.Sin(forwardAngle + offset));
                            var candGrid = playerGrid + candDir * jumpDist;
                            int gx = Math.Clamp((int)candGrid.X, 0, cols - 1);
                            int gy = Math.Clamp((int)candGrid.Y, 0, rows - 1);

                            if (pfGrid[gy][gx] >= 3)
                            {
                                jumpDir = candDir;
                                chosenAngleOffset = offset;
                                break;
                            }
                        }
                    }
                }

                var jumpGrid = playerGrid + jumpDir * jumpDist;

                var world3D = gc.IngameState.Data.ToWorldWithTerrainHeight(jumpGrid);
                var screenTarget = gc.IngameState.Camera.WorldToScreen(world3D);
                var charScreen = gc.IngameState.Camera.WorldToScreen(gc.IngameState.Data.ToWorldWithTerrainHeight(playerGrid));

                var dirScreen = new Vector2(screenTarget.X - charScreen.X, screenTarget.Y - charScreen.Y);
                if (dirScreen.LengthSquared() < 1f)
                {
                    dirScreen = new Vector2(jumpDir.X, jumpDir.Y);
                }

                // Place cursor ~220-300px away from character in direction of jump
                float cursorDist = Math.Clamp(dirScreen.Length(), 180f, 300f);
                var jumpCursorPos = new Vector2(charScreen.X, charScreen.Y) + Vector2.Normalize(dirScreen) * cursorDist;
                jumpCursorPos = BotInput.ClampToWindow(jumpCursorPos);
                jumpCursorPos = CombatWaveRunner.DeflectFromEntrancePortal(gc, jumpCursorPos);

                var combatKey = settings.CombatKey.Value.Key;
                var moveKey = settings.MovementKey.Value.Key;

                if (isOppositeJump)
                {
                    string flank = chosenAngleOffset > 0 ? "backward-left" : "backward-right";
                    SimuLogger.Log($"[UNSTUCK] Wall too thick ({_consecutiveStuckCount} blocked hits) -> Using {moveKey} into cone behind ({flank}) to break line-of-approach!");
                }
                else
                {
                    SimuLogger.Log($"[UNSTUCK] Stuck near wall (hit {_consecutiveStuckCount}/5) -> Using {moveKey} towards target (dist {dist:F0})");
                }

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
