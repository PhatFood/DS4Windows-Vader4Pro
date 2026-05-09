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
    /// Auto-complete approach:
    ///   Phase 1 (frames 1-3):  Touch held at center — establishes contact
    ///   Phase 2 (frames 4-6):  Touch moves to endpoint — velocity for swipe detection
    ///   Phase 3 (frame 7):     Finger lift — triggers swipe recognition
    ///
    ///   When button is released early, the swipe auto-completes through
    ///   all remaining phases before lifting. A quick tap still produces
    ///   a full swipe gesture.
    ///
    ///   If the button is held past the endpoint (finger becomes stationary),
    ///   the swipe is replayed from center on release to ensure velocity at lift.
    /// </summary>
    public class FakeSwipeInjector
    {
        private const short CENTER_X = 960;
        private const short CENTER_Y = 471;
        private const short SWIPE_DISTANCE = 400;
        private const int CENTER_HOLD_FRAMES = 3;  // Frames to hold at center
        private const int MOVE_FRAMES = 3;          // Frames to transition center → endpoint
        private const int TOTAL_SWIPE_FRAMES = CENTER_HOLD_FRAMES + MOVE_FRAMES;

        private enum SwipePhase
        {
            Idle,           // No swipe active
            Active,         // Button is held, swipe is running
            Completing,     // Button released, auto-completing the swipe
            Releasing       // Send the finger-lift frame
        }

        private struct PerDeviceState
        {
            public X360Controls swipeDir;      // Which swipe direction
            public SwipePhase phase;
            public int frameCount;             // Frames since swipe started
            public byte touchId;               // Incremented per new swipe
        }

        private readonly PerDeviceState[] states;

        public FakeSwipeInjector()
        {
            states = new PerDeviceState[Global.MAX_DS4_CONTROLLER_COUNT];
            for (int i = 0; i < states.Length; i++)
            {
                states[i].phase = SwipePhase.Idle;
            }
        }

        /// <summary>
        /// Called from Mapping to record button press/release for swipe actions.
        /// </summary>
        public void SetSwipeState(int device, X360Controls swipeDir, bool pressed)
        {
            ref PerDeviceState s = ref states[device];

            if (pressed)
            {
                if (s.phase == SwipePhase.Idle)
                {
                    // Start new swipe
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
                    // Button released — auto-complete the swipe instead of lifting immediately
                    s.phase = SwipePhase.Completing;
                    // If finger is already stationary at endpoint, replay full motion
                    // so the game sees velocity at finger lift
                    if (s.frameCount > TOTAL_SWIPE_FRAMES)
                        s.frameCount = 0;
                }
            }
        }

        /// <summary>
        /// Called from ControlService after MapCustom. Injects fake touch data.
        /// </summary>
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
                        // Swipe sequence finished — send finger lift
                        s.phase = SwipePhase.Releasing;
                        InjectTouchRelease(ref s, state);
                        s.phase = SwipePhase.Idle;
                    }
                    else
                    {
                        // Still completing the swipe motion
                        InjectTouchActive(ref s, state);
                    }
                    break;

                case SwipePhase.Releasing:
                    // Should not normally reach here, but just in case
                    InjectTouchRelease(ref s, state);
                    s.phase = SwipePhase.Idle;
                    break;

                case SwipePhase.Idle:
                default:
                    // Nothing to do
                    break;
            }
        }

        private void InjectTouchActive(ref PerDeviceState s, DS4State state)
        {
            GetSwipeCoordinates(s.swipeDir, s.frameCount, out short x, out short y);

            state.TrackPadTouch0.IsActive = true;
            state.TrackPadTouch0.Id = s.touchId;
            state.TrackPadTouch0.X = x;
            state.TrackPadTouch0.Y = y;
            state.TrackPadTouch0.RawTrackingNum = s.touchId; // bit 7 = 0 means touching

            state.Touch1 = true;
            state.Touch1Finger = true;
            state.TouchPacketCounter++;
        }

        private void InjectTouchRelease(ref PerDeviceState s, DS4State state)
        {
            state.TrackPadTouch0.IsActive = false;
            state.TrackPadTouch0.Id = s.touchId;
            state.TrackPadTouch0.RawTrackingNum = (byte)(s.touchId | 0x80); // bit 7 = 1 means lifted

            state.Touch1 = false;
            state.Touch1Finger = false;
            state.TouchPacketCounter++;
        }

        private static void GetSwipeCoordinates(X360Controls direction, int frameCount, out short x, out short y)
        {
            if (frameCount <= CENTER_HOLD_FRAMES)
            {
                // Phase 1: hold at center
                x = CENTER_X;
                y = CENTER_Y;
                return;
            }

            // Phase 2+3: move toward endpoint, then hold there
            int moveFrame = frameCount - CENTER_HOLD_FRAMES;
            float progress = Math.Min(1.0f, (float)moveFrame / MOVE_FRAMES);

            GetEndpoint(direction, out short endX, out short endY);

            x = (short)(CENTER_X + (endX - CENTER_X) * progress);
            y = (short)(CENTER_Y + (endY - CENTER_Y) * progress);
        }

        private static void GetEndpoint(X360Controls direction, out short endX, out short endY)
        {
            switch (direction)
            {
                case X360Controls.SwipeTouchUp:
                    endX = CENTER_X;
                    endY = CENTER_Y - SWIPE_DISTANCE;
                    break;
                case X360Controls.SwipeTouchDown:
                    endX = CENTER_X;
                    endY = CENTER_Y + SWIPE_DISTANCE;
                    break;
                case X360Controls.SwipeTouchLeft:
                    endX = CENTER_X - SWIPE_DISTANCE;
                    endY = CENTER_Y;
                    break;
                case X360Controls.SwipeTouchRight:
                    endX = CENTER_X + SWIPE_DISTANCE;
                    endY = CENTER_Y;
                    break;
                default:
                    endX = CENTER_X;
                    endY = CENTER_Y;
                    break;
            }
        }
    }
}
