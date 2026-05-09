using System;

/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

namespace DS4Windows
{
    /// <summary>
    /// Injects fake touchpad swipe data into DS4State when a button
    /// mapped to a touchpad swipe action is pressed.
    ///
    /// On button press, the touch is placed at center and moves toward
    /// the endpoint. The finger keeps drifting past the endpoint (clamped
    /// to touchpad bounds) so there is always velocity at lift time.
    /// More active frames give the game more data to detect the gesture.
    /// </summary>
    public class FakeSwipeInjector
    {
        private const short CENTER_X = 960;
        private const short CENTER_Y = 471;
        private const short SWIPE_DISTANCE = 400;
        private const int CENTER_HOLD_FRAMES = 3;
        private const int MOVE_FRAMES = 24;
        private const int TOTAL_SWIPE_FRAMES = CENTER_HOLD_FRAMES + MOVE_FRAMES;
        private const short MAX_X = 1920;
        private const short MAX_Y = 943;

        private enum SwipePhase { Idle, Active, Completing }

        private struct PerDeviceState
        {
            public X360Controls swipeDir;
            public SwipePhase phase;
            public int frameCount;
            public byte touchId;
        }

        private readonly PerDeviceState[] states;

        public FakeSwipeInjector()
        {
            states = new PerDeviceState[Global.MAX_DS4_CONTROLLER_COUNT];
            for (int i = 0; i < states.Length; i++)
                states[i].phase = SwipePhase.Idle;
        }

        public void SetSwipeState(int device, X360Controls swipeDir, bool pressed)
        {
            ref PerDeviceState s = ref states[device];

            if (pressed)
            {
                if (s.phase == SwipePhase.Idle)
                {
                    s.swipeDir = swipeDir;
                    s.phase = SwipePhase.Active;
                    s.frameCount = 0;
                    s.touchId = (byte)((s.touchId + 1) & 0x7F);
                }
            }
            else
            {
                if (s.phase == SwipePhase.Active && s.swipeDir == swipeDir)
                {
                    s.phase = SwipePhase.Completing;
                }
            }
        }

        public void ApplyToState(int device, DS4State state)
        {
            ref PerDeviceState s = ref states[device];

            switch (s.phase)
            {
                case SwipePhase.Active:
                    s.frameCount++;
                    InjectTouchActive(ref s, state);
                    break;

                case SwipePhase.Completing:
                    s.frameCount++;
                    if (s.frameCount > TOTAL_SWIPE_FRAMES)
                    {
                        InjectTouchRelease(ref s, state);
                        s.phase = SwipePhase.Idle;
                    }
                    else
                    {
                        InjectTouchActive(ref s, state);
                    }
                    break;

                case SwipePhase.Idle:
                default:
                    break;
            }
        }

        private void InjectTouchActive(ref PerDeviceState s, DS4State state)
        {
            short x, y;
            if (s.frameCount <= CENTER_HOLD_FRAMES)
            {
                x = CENTER_X; y = CENTER_Y;
            }
            else
            {
                int mf = s.frameCount - CENTER_HOLD_FRAMES;
                float t = (float)mf / MOVE_FRAMES;
                GetEndpoint(s.swipeDir, out short ex, out short ey);
                float fx = CENTER_X + (ex - CENTER_X) * t;
                float fy = CENTER_Y + (ey - CENTER_Y) * t;
                x = (short)Math.Clamp(fx, 0, MAX_X);
                y = (short)Math.Clamp(fy, 0, MAX_Y);
            }

            state.TrackPadTouch0.IsActive = true;
            state.TrackPadTouch0.Id = s.touchId;
            state.TrackPadTouch0.X = x;
            state.TrackPadTouch0.Y = y;
            state.TrackPadTouch0.RawTrackingNum = s.touchId;

            state.Touch1 = true;
            state.Touch1Finger = true;
            state.TouchPacketCounter++;
        }

        private void InjectTouchRelease(ref PerDeviceState s, DS4State state)
        {
            // Advance position past endpoint so game sees velocity at lift
            GetEndpoint(s.swipeDir, out short ex, out short ey);
            short dx = (short)Math.Sign(ex - CENTER_X);
            short dy = (short)Math.Sign(ey - CENTER_Y);
            state.TrackPadTouch0.X = (short)(ex + dx * 40);
            state.TrackPadTouch0.Y = (short)(ey + dy * 40);

            state.TrackPadTouch0.IsActive = false;
            state.TrackPadTouch0.Id = s.touchId;
            state.TrackPadTouch0.RawTrackingNum = (byte)(s.touchId | 0x80);

            state.Touch1 = false;
            state.Touch1Finger = false;
            state.TouchPacketCounter++;
        }

        private static void GetEndpoint(X360Controls direction, out short endX, out short endY)
        {
            switch (direction)
            {
                case X360Controls.SwipeTouchUp:
                    endX = CENTER_X; endY = CENTER_Y - SWIPE_DISTANCE; break;
                case X360Controls.SwipeTouchDown:
                    endX = CENTER_X; endY = CENTER_Y + SWIPE_DISTANCE; break;
                case X360Controls.SwipeTouchLeft:
                    endX = CENTER_X - SWIPE_DISTANCE; endY = CENTER_Y; break;
                case X360Controls.SwipeTouchRight:
                    endX = CENTER_X + SWIPE_DISTANCE; endY = CENTER_Y; break;
                default:
                    endX = CENTER_X; endY = CENTER_Y; break;
            }
        }
    }
}
