using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;

namespace SimuFarmer.Systems
{
    public static class BotInput
    {
        private static readonly Random _rng = new();
        private static readonly ConcurrentDictionary<Keys, DateTime> _heldKeys = new();
        private static bool _isLeftMouseDown = false;
        private static DateTime _nextActionAt = DateTime.MinValue;

        public static bool CanAct => DateTime.Now >= _nextActionAt;
        public static SharpDX.RectangleF WindowRect { get; set; }
        public static SharpDX.RectangleF HudRect { get; set; }

        /// <summary>
        /// Checks whether a screen position is completely clear of all game UI elements:
        /// - Life globe (bottom-left)
        /// - Flasks & Delirium reward bar (bottom-left-center)
        /// - Skill bar & Shop button (bottom-right-center)
        /// - Mana globe (bottom-right)
        /// - XP bar (bottom-center)
        /// - Top buff / aura icons (top-left)
        /// - Delirium wave progress bar (top-center)
        /// - SimuFarmer ImGui HUD window
        /// </summary>
        public static bool IsSafeFromUI(Vector2 pos)
        {
            if (WindowRect.Width < 100 || WindowRect.Height < 100)
                return true;

            float w = WindowRect.Width;
            float h = WindowRect.Height;

            // SimuFarmer HUD window safety margin
            if (HudRect.Width > 10 && HudRect.Height > 10)
            {
                if (pos.X >= HudRect.Left - 10 && pos.X <= HudRect.Right + 10 &&
                    pos.Y >= HudRect.Top - 10 && pos.Y <= HudRect.Bottom + 10)
                    return false;
            }

            // Top-left HUD & Buff icons region
            if (pos.X < w * 0.22f && pos.Y < h * 0.45f)
                return false;

            // Screen outer boundaries
            if (pos.X < w * 0.08f || pos.X > w * 0.92f)
                return false;
            if (pos.Y < h * 0.12f)
                return false;

            // Bottom-left Life / Ward globe
            if (pos.Y > h * 0.65f && pos.X < w * 0.20f)
                return false;

            // Bottom-left Flasks and Simulacrum Rewards bar (icons row + flasks)
            if (pos.Y > h * 0.68f && pos.X < w * 0.45f)
                return false;

            // Bottom-right Mana globe
            if (pos.Y > h * 0.65f && pos.X > w * 0.80f)
                return false;

            // Bottom-right Skills and Shop button
            if (pos.Y > h * 0.70f && pos.X > w * 0.68f)
                return false;

            // Bottom-center Experience bar
            if (pos.Y > h * 0.75f)
                return false;

            return true;
        }

        /// <summary>
        /// Projects a movement click safely onto the open ground around the character's feet.
        /// By clicking within a controlled radius (~180-220px) of character center,
        /// the player runs at full speed in the desired direction without EVER touching
        /// any UI elements (globes, flasks, skills, buff icons, HUD, or screen borders).
        /// </summary>
        public static Vector2 GetSafeMovementClick(Vector2 center, Vector2 target)
        {
            var dir = target - center;
            float dist = dir.Length();
            if (dist < 8f) return center;

            float w = WindowRect.Width > 100 ? WindowRect.Width : 1920f;
            float h = WindowRect.Height > 100 ? WindowRect.Height : 1080f;

            // Safe movement radius around character torso (~20% of screen height, ~215px in 1080p)
            float maxRadius = Math.Min(h * 0.20f, 220f);
            float radius = Math.Min(dist, maxRadius);

            var normDir = Vector2.Normalize(dir);
            var clickPos = center + normDir * radius;

            // Strict safety loop: pull back closer to character if it comes anywhere near UI
            int attempts = 0;
            while (!IsSafeFromUI(clickPos) && radius > 30f && attempts < 10)
            {
                radius *= 0.8f;
                clickPos = center + normDir * radius;
                attempts++;
            }

            return clickPos;
        }

        /// <summary>
        /// Clamps a screen coordinate to strictly stay within the game window boundaries.
        /// Prevents the mouse from leaving the game window onto a second monitor.
        /// Does NOT distort UI clicks (inventory, stash, buttons, labels).
        /// </summary>
        public static Vector2 ClampToWindow(Vector2 pos, float margin = 10f)
        {
            if (WindowRect.Width < 100 || WindowRect.Height < 100)
                return pos;

            float minX = Math.Max(margin, 5f);
            float maxX = Math.Min(WindowRect.Width - margin, WindowRect.Width - 5f);
            float minY = Math.Max(margin, 5f);
            float maxY = Math.Min(WindowRect.Height - margin, WindowRect.Height - 5f);

            return new Vector2(
                Math.Clamp(pos.X, minX, maxX),
                Math.Clamp(pos.Y, minY, maxY)
            );
        }

        // Gaussian distributions
        private const float SettleMeanMs = 60f;
        private const float SettleStdDevMs = 12f;
        private const float HoldMeanMs = 45f;
        private const float HoldStdDevMs = 10f;

        private static int Gaussian(float mean, float stdDev, int floor = 20, int ceiling = 120)
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            int res = (int)(mean + stdDev * normal);
            return Math.Clamp(res, floor, ceiling);
        }

        public static int RandSettle() => Gaussian(SettleMeanMs, SettleStdDevMs);
        public static int RandHold() => Gaussian(HoldMeanMs, HoldStdDevMs);

        public static void GateFor(int ms)
        {
            _nextActionAt = DateTime.Now.AddMilliseconds(ms);
        }

        // --- Mouse Movement & Interpolation ---

        public static async Task MoveCursorTo(Vector2 target, float jitterRange = 2.0f)
        {
            target = ClampToWindow(target);
            var cur = Input.MousePosition;
            var start = new Vector2(cur.X, cur.Y);
            var delta = target - start;
            var dist = delta.Length();

            if (dist < 4f)
            {
                Input.SetCursorPos(target);
                return;
            }

            // Humanized trajectory
            int steps = Math.Clamp((int)(dist / 50f), 4, 12);
            int stepDelay = Math.Clamp((int)(dist / 15f), 15, 65) / steps;

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                // Smooth-step curve
                float smoothT = t * t * (3f - 2f * t);
                var interp = Vector2.Lerp(start, target, smoothT);

                // Small perpendicular jitter
                if (i < steps && jitterRange > 0.1f)
                {
                    float jitter = (float)(_rng.NextDouble() * 2 - 1) * jitterRange;
                    interp.X += jitter;
                    interp.Y += jitter;
                }

                interp = ClampToWindow(interp);
                Input.SetCursorPos(interp);
                if (stepDelay > 0)
                {
                    await Task.Delay(stepDelay);
                }
            }

            Input.SetCursorPos(target);
        }

        // --- Key Holding & Release ---

        public static void HoldKey(Keys key)
        {
            if (!_heldKeys.ContainsKey(key))
            {
                Input.KeyDown(key);
                _heldKeys[key] = DateTime.Now;
            }
        }

        public static void ReleaseKey(Keys key)
        {
            if (_heldKeys.TryRemove(key, out _))
            {
                Input.KeyUp(key);
            }
        }

        public static void HoldLeftMouse()
        {
            if (!_isLeftMouseDown)
            {
                Input.LeftDown();
                _isLeftMouseDown = true;
            }
        }

        public static void ReleaseLeftMouse()
        {
            if (_isLeftMouseDown)
            {
                Input.LeftUp();
                _isLeftMouseDown = false;
            }
        }

        public static void ReleaseAllKeys()
        {
            foreach (var key in _heldKeys.Keys)
            {
                Input.KeyUp(key);
            }
            _heldKeys.Clear();

            ReleaseLeftMouse();

            // Clear modifiers just in case
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.ShiftKey);
            Input.KeyUp(Keys.LControlKey);
            Input.KeyUp(Keys.RControlKey);
            Input.KeyUp(Keys.LShiftKey);
            Input.KeyUp(Keys.RShiftKey);
        }

        // --- Clicks & Actions ---

        public static async Task TapKey(Keys key)
        {
            int hold = RandHold();
            GateFor(hold + 40);
            Input.KeyDown(key);
            await Task.Delay(hold);
            Input.KeyUp(key);
        }

        public static async Task ClickAt(Vector2 screenPos)
        {
            screenPos = ClampToWindow(screenPos);
            int settle = RandSettle();
            int hold = RandHold();
            GateFor(settle + hold + 80);

            await MoveCursorTo(screenPos);
            await Task.Delay(settle);
            Input.LeftDown();
            await Task.Delay(hold);
            Input.LeftUp();
        }

        public static async Task RightClickAt(Vector2 screenPos)
        {
            int settle = RandSettle();
            int hold = RandHold();
            GateFor(settle + hold + 80);

            await MoveCursorTo(screenPos);
            await Task.Delay(settle);
            Input.RightDown();
            await Task.Delay(hold);
            Input.RightUp();
        }

        public static async Task ClickEntity(GameController gc, ExileCore.PoEMemory.MemoryObjects.Entity entity)
        {
            var center = entity.BoundsCenterPosNum;
            if (center == Vector3.Zero)
                center = entity.PosNum;
            if (center == Vector3.Zero)
                center = gc.IngameState.Data.ToWorldWithTerrainHeight(entity.GridPosNum);

            var camera = gc.IngameState.Camera;
            var screen = camera.WorldToScreen(center);

            var windowRect = gc.Window.GetWindowRectangle();
            if (screen.X < 20 || screen.X > windowRect.Width - 20 ||
                screen.Y < 20 || screen.Y > windowRect.Height - 20)
            {
                // Target is off-screen! Never move mouse outside the game window
                return;
            }

            await ClickAt(new Vector2(screen.X, screen.Y));
        }

        public static async Task CtrlClickAt(Vector2 screenPos)
        {
            screenPos = ClampToWindow(screenPos);
            int settle = RandSettle();
            int hold = RandHold();
            GateFor(settle + hold + 220);

            // Step 1: Move mouse to item center FIRST with NO keys held
            await MoveCursorTo(screenPos);
            await Task.Delay(settle + 30);

            // Step 2: Now press Control and hold it
            Input.KeyDown(Keys.ControlKey);
            Input.KeyDown(Keys.LControlKey);
            await Task.Delay(40);

            // Step 3: Click while Control is firmly held
            Input.LeftDown();
            await Task.Delay(hold + 30);
            Input.LeftUp();
            await Task.Delay(40);

            // Step 4: Release Control
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.LControlKey);
            await Task.Delay(30);
        }

        public static async Task CtrlShiftClickAt(Vector2 screenPos)
        {
            screenPos = ClampToWindow(screenPos);
            int settle = RandSettle();
            int hold = RandHold();
            GateFor(settle + hold + 240);

            // Step 1: Move mouse to item center FIRST with NO keys held
            await MoveCursorTo(screenPos);
            await Task.Delay(settle + 30);

            // Step 2: Now press Control + Shift
            Input.KeyDown(Keys.ControlKey);
            Input.KeyDown(Keys.LControlKey);
            Input.KeyDown(Keys.ShiftKey);
            Input.KeyDown(Keys.LShiftKey);
            await Task.Delay(45);

            // Step 3: Click while modifiers are held
            Input.LeftDown();
            await Task.Delay(hold + 30);
            Input.LeftUp();
            await Task.Delay(40);

            // Step 4: Release modifiers
            Input.KeyUp(Keys.ShiftKey);
            Input.KeyUp(Keys.LShiftKey);
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.LControlKey);
            await Task.Delay(30);
        }

        // Watchdog tick (releases keys held > 60s)
        public static void TickWatchdog()
        {
            var now = DateTime.Now;
            foreach (var (key, pressedAt) in _heldKeys)
            {
                if ((now - pressedAt).TotalSeconds > 60)
                {
                    ReleaseKey(key);
                }
            }
        }
    }
}
