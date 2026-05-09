# Touchpad Swipe as Selectable Mapping Action

## Background

Add "Touchpad Swipe Up/Down/Left/Right" as selectable output actions in the existing mapping system. Any input button can be mapped to any swipe direction through the standard binding dialog.

## Addressing Review Concerns

### ✅ Concern 1: Enum shift risk
**Confirmed safe.** Profiles serialize X360Controls as **strings** (e.g., `"Touchpad Click"`), not raw integers. See [ScpUtil.cs:4854](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/ScpUtil.cs#L4854) (save) and [ScpUtil.cs:7603](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/ScpUtil.cs#L7603) (load). However, to be extra safe, we'll place the new enum values **after** `Unbound`, not before it. This avoids shifting any existing value.

### ✅ Concern 2: Per-frame movement is too naive
**Agreed.** Changed to a **two-frame approach**:
- **Frame 1 (button press):** Touch starts at center (960, 471), `IsActive = true`
- **Frame 2+ (button held):** Touch jumps to the endpoint (e.g., center ± 400px), `IsActive = true`
- **Release frame:** `IsActive = false` (finger lift)

This is simple and reliable — games detect swipe by the **direction vector between start and current/end position**, not by tracking velocity frame-by-frame. The 400px offset is large enough to be unambiguous.

### ✅ Concern 3: needsRelease timing
**Clarified.** While the button is held, the touch stays at the endpoint position (not moving further). The swipe gesture is recognized by games when the finger lifts (release frame). This mimics a quick flick-and-hold gesture.

### ✅ Concern 4: No diagonal complexity
**Agreed.** Only one swipe direction is active per device at a time. If somehow multiple swipe actions fire, last-write-wins. No diagonal state machine.

### ✅ Concern 5: UI placement
**Agreed.** The swipe buttons will be placed in a **new "Touchpad Swipe" section** within the existing "Abs Mouse" tab (renamed to "Abs Mouse / Touchpad"), logically grouped near the touchpad area. Not in the mouse panel.

---

## Proposed Changes

### Component 1: X360Controls Enum & Name Registrations

#### [MODIFY] [ScpUtil.cs](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/ScpUtil.cs)

**1. Add 4 new values to `X360Controls` enum** — placed **after** `Unbound`:

```diff
 public enum X360Controls : byte {
     ..., AbsMouseUp, AbsMouseDown, AbsMouseLeft, AbsMouseRight, Unbound,
+    SwipeTouchUp, SwipeTouchDown, SwipeTouchLeft, SwipeTouchRight
 };
```

> [!NOTE]
> Placing after `Unbound` means no existing numeric values shift. The `IsMouse()` / `IsMouseRange()` range checks (`>= LeftMouse && < Unbound`) naturally exclude these new values — which is correct, they're not mouse actions.

**2. Add entries to `xboxDefaultNames` and `ds4DefaultNames` dictionaries** (after `Unbound`):

```csharp
[X360Controls.SwipeTouchUp] = "Touchpad Swipe Up",
[X360Controls.SwipeTouchDown] = "Touchpad Swipe Down",
[X360Controls.SwipeTouchLeft] = "Touchpad Swipe Left",
[X360Controls.SwipeTouchRight] = "Touchpad Swipe Right",
```

**3. Add cases to `getX360ControlsByName` and `getX360ControlString`.**

---

### Component 2: Binding Window UI

#### [MODIFY] [BindingWindow.xaml](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Forms/BindingWindow.xaml)

Rename the "Abs Mouse" tab to "Abs Mouse" and add a "Touchpad Swipe" section below the existing Abs Mouse buttons:

```xml
<TabItem Header="Abs Mouse">
    <StackPanel Orientation="Vertical" Margin="8" HorizontalAlignment="Left" MinWidth="200">
        <!-- Existing Abs Mouse buttons -->
        <Button x:Name="absMouseUpBindBtn" Content="Abs Mouse Up" />
        <Button x:Name="absMouseDownBindBtn" Content="Abs Mouse Down" Margin="0,8,0,0" />
        <Button x:Name="absMouseLeftBindBtn" Content="Abs Mouse Left" Margin="0,8,0,0" />
        <Button x:Name="absMouseRightBindBtn" Content="Abs Mouse Right" Margin="0,8,0,0" />

        <!-- New Touchpad Swipe section -->
        <Separator Margin="0,12,0,8" />
        <TextBlock Text="Touchpad Swipe" FontWeight="Bold" Margin="0,0,0,4" />
        <Button x:Name="swipeTouchUpBtn" Content="Swipe Up" Margin="0,4,0,0" />
        <Button x:Name="swipeTouchDownBtn" Content="Swipe Down" Margin="0,4,0,0" />
        <Button x:Name="swipeTouchLeftBtn" Content="Swipe Left" Margin="0,4,0,0" />
        <Button x:Name="swipeTouchRightBtn" Content="Swipe Right" Margin="0,4,0,0" />
    </StackPanel>
</TabItem>
```

#### [MODIFY] [BindingWindow.xaml.cs](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Forms/BindingWindow.xaml.cs)

Register the 4 new buttons in `InitButtonBindings()`:

```csharp
associatedBindings.Add(swipeTouchUpBtn,
    new BindAssociation() { outputType = BindAssociation.OutType.Button, control = X360Controls.SwipeTouchUp });
swipeTouchUpBtn.Click += OutputButtonBtn_Click;
// ... same for Down, Left, Right
```

Also update `FindCurrentHighlightButton()` and `conBtnMap` to handle the swipe controls (they're `OutType.Button` and not in mouse range, so they'll go into `conBtnMap` naturally).

---

### Component 3: Mapping Logic

#### [MODIFY] [Mapping.cs](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/Mapping.cs)

In `ProcessControlSettingAction`, add a new `else if` branch after the AbsMouse block (~line 3871):

```csharp
else if (xboxControl >= X360Controls.SwipeTouchUp && xboxControl <= X360Controls.SwipeTouchRight)
{
    bool pressed = GetBoolMapping(device, dcs.control, cState, eState, tp, fieldMapping);
    fakeSwipeStates[device].SetSwipeState(xboxControl, pressed);
}
```

Add a static `FakeSwipeInjector` instance as a field on `Mapping`:

```csharp
public static FakeSwipeInjector fakeSwipeInjector = new FakeSwipeInjector();
```

---

### Component 4: Fake Swipe Injector (New File)

#### [NEW] [FakeSwipeInjector.cs](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/FakeSwipeInjector.cs)

Simple state machine — no diagonal, no per-frame velocity:

```csharp
public class FakeSwipeInjector
{
    private struct PerDeviceState
    {
        public X360Controls activeSwipe;  // Which direction is active (or None)
        public bool wasPressed;           // Was pressed last frame
        public bool isFirstFrame;         // Just started pressing
        public byte touchId;              // Incremented per new swipe

        // Center point
        public const short CENTER_X = 960;
        public const short CENTER_Y = 471;
        public const short SWIPE_DISTANCE = 400;
    }

    private PerDeviceState[] states; // [MAX_DS4_CONTROLLER_COUNT]

    // Called from Mapping.ProcessControlSettingAction
    public void SetSwipeState(int device, X360Controls swipeDir, bool pressed);

    // Called from ControlService after MapCustom
    public void ApplyToState(int device, DS4State state);
}
```

**`ApplyToState` logic:**
1. If no swipe active → don't touch the state (real touchpad data passes through)
2. If swipe just started (first frame) → set `TrackPadTouch0` to center point, `IsActive = true`
3. If swipe held (subsequent frames) → set `TrackPadTouch0` to endpoint, `IsActive = true`
4. If swipe just released → set `TrackPadTouch0.IsActive = false`, `RawTrackingNum` bit 7 set
5. Also sets `Touch1 = true/false`, `Touch1Finger = true/false`, increments `TouchPacketCounter`

**Endpoint calculation:**
| Direction | X | Y |
|---|---|---|
| Up | 960 | 471 - 400 = 71 |
| Down | 960 | 471 + 400 = 871 |
| Left | 960 - 400 = 560 | 471 |
| Right | 960 + 400 = 1360 | 471 |

---

### Component 5: Integration in ControlService

#### [MODIFY] [ControlService.cs](file:///d:/dev/ds4/DS4Windows-Vader4Pro/DS4Windows/DS4Control/ControlService.cs)

After touch data is copied to `tempMapState` (~line 2750), call the injector:

```csharp
// After: tempMapState.TrackPadTouch1 = cState.TrackPadTouch1;
Mapping.fakeSwipeInjector.ApplyToState(ind, tempMapState);
```

---

## Verification Plan

### Automated Tests
```bash
dotnet build DS4WindowsWPF.sln -c Release -p:Platform=x64
```
Verify clean compile with zero errors.

### Manual Verification
1. Open profile editor, click any button (e.g., BLP/BRP)
2. Go to "Abs Mouse" tab → see "Touchpad Swipe" section with Up/Down/Left/Right
3. Select "Swipe Up" → verify the binding saves and displays correctly
4. Save/load the profile → verify the mapping persists in XML
5. Connect controller, press the mapped button → virtual DS4 reports a touchpad swipe
6. Release the button → touch lifts
