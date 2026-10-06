# Aim layer

The aim layer swaps the right stick's settings while a trigger is held. For example, a profile
with a linear curve can use the "Edge Expo" profile's curve only while you hold L2 to aim.

There is no editor UI for it yet. You add it by hand to the profile's XML file.

## What it does

- While the trigger is held past the threshold, the right stick uses the **source profile's**
  right-stick settings. When you let go, the base profile's settings come back.
- No profile switch happens. Buttons, left stick, triggers, gyro and everything else stay on the
  base profile.
- The change applies on the next input report, so there is no delay.
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
| `SourceProfile` | Profile name without `.xml`. Spaces around it are trimmed. | empty | layer stays off (see below) |
| `UseSourceLightbar` | `True` / `False`, any case | `False` | `False` |

Any element can be left out; its default is used. A bad value never stops the profile loading.

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

### Remove the old profile-switch action

If the base profile has a special action that switches profile while the same trigger is held
(for example "Switch 2 Edge Expo" on L2 with automatic untrigger), delete or disable it. Otherwise
both run: the profile switches and the layer also turns on.

## Keeping it working

- **Saving the base profile in DS4Windows keeps the block.** It is only left out on save when every
  value is the default. A disabled layer that still names a source profile is kept.
- **Editing the source profile in DS4Windows** (for example changing Edge Expo's curve) and saving
  it updates the layer straight away.
- **Editing the source file by hand** takes effect the next time the base profile is loaded.

## Lightbar cue

With `UseSourceLightbar` on, the lightbar shows the source profile's main colour while the layer
is on. It only replaces the plain main colour. These still take priority as usual: custom colour,
rainbow, LED as battery indicator, low-battery flash, charging modes, and other lightbar overrides.

## If the source profile is missing or invalid

The layer stays off and one warning appears in the DS4Windows **Log** tab, starting with:

```
Aim layer of profile "Edge Linear" is off: source profile "Edge Expo": ...
```

The rest of the message says why (no source set, not a valid file name, file not found, or the
file could not be loaded). The warning is shown once, not on every reload. The layer turns on by
itself once the source profile is created or saved in DS4Windows.

If `SourceProfile` names the base profile itself, the layer is silently ignored.

## Threshold detail

The layer checks the **raw** trigger value. A profile-switch special action checks the trigger
after the trigger's own dead zone, max zone, sensitivity and curve. With default L2/R2 settings
the two are the same, so threshold 100 matches the old switch action.
