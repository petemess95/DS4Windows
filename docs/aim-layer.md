# Aim layer

The aim layer swaps the right stick's settings while a trigger is held. For example, a profile
with a linear curve can use the "Edge Expo" profile's curve only while you hold L2 to aim.

A profile can have up to four aim layers, for example L2 for aiming and R2 (after a short hold)
for hip-fire. See [Several layers](#several-layers).

There is no editor UI for it yet. You add it by hand to the profile's XML file.

## What it does

- While the trigger is held past the threshold, the right stick uses the **source profile's**
  right-stick settings. When you let go, the base profile's settings come back.
- No profile switch happens. Buttons, left stick, triggers, gyro and everything else stay on the
  base profile.
- The change applies on the next input report, so there is no delay (unless you set `<Delay>`).
- The game still sees L2 (or R2) exactly as before, so aim-down-sights works normally.

### Settings borrowed from the source profile

- RS dead zone, anti-dead zone, max zone, max output, dead-zone type (Radial/Axial) and the rest
  of the RS dead-zone settings, including the per-axis values for Axial
- RS sensitivity (only used with the Radial dead-zone type, same as normal)
- RS square stick mode and roundness
- RS output curve mode and custom curve

### Settings kept from the base profile

- RS rotation, anti-snapback, calibration and fuzz. These filters remember previous reports, so
  changing them mid-hold could make the stick jump.
- RS output mode settings (for example stick-to-mouse or flick stick options). Stick-to-mouse and
  normal stick output use the swapped dead zone and curve. Flick stick reads the raw stick, so no
  dead zone or curve affects it, with or without the layer.

## The XML block

```xml
<AimLayer>
  <Enabled>True</Enabled>
  <Trigger>L2</Trigger>
  <Threshold>100</Threshold>
  <SourceProfile>Edge Expo</SourceProfile>
  <UseSourceLightbar>True</UseSourceLightbar>
</AimLayer>
```

This exact block is loaded by a test (`SampleAimLayerXml` in
`DS4WindowsTests/AimLayerProfileTests.cs`).

| Element | Values | Default | Bad value |
| --- | --- | --- | --- |
| `Enabled` | `True` / `False`, any case | `False` | `False` |
| `Trigger` | `L2` or `R2`, any case | `L2` | `L2` |
| `Threshold` | whole number 0-255. The layer is on while the trigger is **greater than** this value. | `100` | `100` |
| `Delay` | whole milliseconds 0-1000. The trigger must stay above `Threshold` this long before the layer turns on. `0` means at once. | `0` | `0` |
| `SourceProfile` | Profile name without `.xml`. Spaces around it are trimmed. | empty | layer stays off (see below) |
| `UseSourceLightbar` | `True` / `False`, any case | `False` | `False` |

Any element can be left out; its default is used. A bad value never stops the profile loading.
On save, `<Delay>` is written after `<Threshold>`, and only when it is not 0.

## Adding it to a profile

1. **Close DS4Windows before editing a profile file.** If it is running, it may overwrite your
   change.
2. Back up the profile file first.
3. Open `%APPDATA%\DS4Windows\Profiles\<name>.xml` in a text editor.
4. Paste the `<AimLayer>` block as a direct child of the `<DS4Windows>` root element. Position
   among the other elements does not matter.
5. Remove any old hold-to-switch action for the same trigger (see below).
6. Save and start DS4Windows.

Example: `Edge Linear.xml` borrowing from "Edge Expo", L2, threshold 100, source lightbar on:

```xml
<DS4Windows app_version="..." config_version="...">
  ...existing settings...
  <AimLayer>
    <Enabled>True</Enabled>
    <Trigger>L2</Trigger>
    <Threshold>100</Threshold>
    <SourceProfile>Edge Expo</SourceProfile>
    <UseSourceLightbar>True</UseSourceLightbar>
  </AimLayer>
</DS4Windows>
```

## Several layers

A profile can hold several `<AimLayer>` blocks, side by side under `<DS4Windows>`. Order in the file
is priority:

- On each input report, the **first ready layer** in file order is used. A layer is ready when its
  trigger is above its `Threshold` and has stayed there for at least its `Delay`.
- If no layer is ready, the base profile's settings are used.
- At most **4** blocks are used. Extra blocks are ignored and one warning appears in the Log tab:
  `Aim layer of profile "X": N extra <AimLayer> block(s) ignored; at most 4 are used`. The profile
  still loads.

Each layer has its own hold timer, separate from the others:

- The timer starts on the report where that layer's raw trigger goes above **its own** `Threshold`.
- Letting go of the trigger turns the layer off on that report. The next press waits the full delay
  again.
- Timers keep running while a higher layer is on. So if you hold R2 while aiming with L2, and R2
  has already been held for its delay, releasing L2 switches straight to the R2 layer.
- Timers restart when a profile is applied or the controller reconnects, and when a source profile
  is saved. A trigger that is already held then waits the full delay from the next report.

### Example: aim and hip-fire

`Edge Linear.xml` keeps its L2 → Edge Expo block first, then adds R2 → "Edge Hipfire" after a
100 ms hold, with the source lightbar on:

```xml
<AimLayer>
  <Enabled>True</Enabled>
  <Trigger>L2</Trigger>
  <Threshold>100</Threshold>
  <SourceProfile>Edge Expo</SourceProfile>
  <UseSourceLightbar>True</UseSourceLightbar>
</AimLayer>
<AimLayer>
  <Enabled>True</Enabled>
  <Trigger>R2</Trigger>
  <Threshold>100</Threshold>
  <Delay>100</Delay>
  <SourceProfile>Edge Hipfire</SourceProfile>
  <UseSourceLightbar>True</UseSourceLightbar>
</AimLayer>
```

These exact blocks are loaded by a test (`SampleTwoAimLayersXml` in
`DS4WindowsTests/AimLayerProfileTests.cs`).

| Held (raw triggers) | Right stick uses |
| --- | --- |
| Nothing, or R2 for under 100 ms | Edge Linear (base) |
| R2 for 100 ms or more, L2 not held | Edge Hipfire |
| L2, with or without R2 | Edge Expo |

### Tuning the delay

- **The delay counts from the layer's own `Threshold`, not from where the game starts firing.**
  With `Threshold 100` and a slow squeeze, the game may already be firing before the timer starts.
  A lower R2 threshold such as 30-50 is fine, because the delay already filters out taps. The
  default stays 100.
- **Shotgun pulls.** A firm pull can last over 100 ms, so Hipfire briefly comes on at the end of the
  shot. The lightbar shows this. If it bothers you, raise `<Delay>` by hand (for example to 150).

### Remove the old profile-switch action

If the base profile has a special action that switches profile while the same trigger is held
(for example "Switch 2 Edge Expo" on L2 with automatic untrigger), delete or disable it. Otherwise
both run: the profile switches and the layer also turns on.

## Keeping it working

- **Saving the base profile in DS4Windows keeps every block, in order.** A block is only left out on
  save when every value is the default. A disabled layer that still names a source profile is kept.
- **Editing the source profile in DS4Windows** (for example changing Edge Expo's curve) and saving
  it updates the layers that borrow it straight away.
- **Editing the source file by hand** takes effect the next time the base profile is loaded.

## Lightbar cue

With `UseSourceLightbar` on, the lightbar shows the source profile's main colour while that layer
is the active one. With several layers, the colour follows the active layer; a layer with
`UseSourceLightbar` off leaves the normal colour. It only replaces the plain main colour. These still take priority as usual: custom colour,
rainbow, LED as battery indicator, low-battery flash, charging modes, and other lightbar overrides.

## If the source profile is missing or invalid

Only that layer stays off; the profile's other layers keep working. One warning per layer appears
in the DS4Windows **Log** tab, naming the layer's trigger, for example:

```
Aim layer of profile "Edge Linear" (L2) is off: source profile "Edge Expo": ...
Aim layer of profile "Edge Linear" (R2) is off: source profile "Edge Hipfire": ...
```

The rest of the message says why (no source set, not a valid file name, file not found, or the
file could not be loaded). The warning is shown once, not on every reload. The layer turns on by
itself once the source profile is created or saved in DS4Windows.

If a layer's `SourceProfile` names the base profile itself, that layer is silently ignored.

## Threshold detail

The layer checks the **raw** trigger value. A profile-switch special action checks the trigger
after the trigger's own dead zone, max zone, sensitivity and curve. With default L2/R2 settings
the two are the same, so threshold 100 matches the old switch action.
