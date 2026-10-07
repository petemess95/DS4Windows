# Aim macros

Aim macros add two small automatic movements to the right stick while an [aim layer](aim-layer.md)
is active:

- **Recoil compensation**: a steady pull on the right stick while you aim and fire.
- **Rotational circle**: tiny right-stick circles while you aim, so the game's rotational aim
  assist keeps re-engaging.

Both are off until you **arm** them with a button combo, and they are always disarmed when
DS4Windows starts and whenever a profile loads.

There is no editor UI. You add them by hand to the profile's XML file, the same way as the aim
layer.

## Read this first: Fortnite rules

**Recoil compensation and an automatic aim-assist circle break Fortnite's rules in public matches.**
Using them there can get your account banned. They are meant for **private matches, custom matches
and Creative only**. Keep them disarmed (the default) everywhere else.

These macros do nothing to hide themselves: there is no randomisation or "humanising", and none
will be added.

### What they can and cannot do in Fortnite

- Most of Fortnite's gun inaccuracy is random **bloom**. No stick pull can cancel it. Recoil
  compensation only helps against the steady vertical kick.
- The circle only matters if **aim assist is on** in Fortnite's controller settings.
- Test and tune in **Creative** or a **private match**.

## Scope limits

- Only the right stick is changed. There is no left-stick strafe jitter and no L2 re-tap.
- Nothing runs unless the profile has the elements below **and** you have armed the macros.
- Macros only exist in a profile that has at least one enabled aim layer whose source profile
  loads. Without one, `<AimMacros>`, `<Recoil>` and `<Rotate>` do nothing.
- They are not applied to joined (merged) controllers.

## How it works: game space

Fortnite has its own right-stick dead zone. DS4Windows sends the stick with no dead zone of its
own, so a small pull would vanish inside the game's dead zone. To avoid that, every size below is a
**percent of the game's live range**: the part of the stick travel outside the game's dead zone.
DS4Windows takes the final right-stick output (after your curve, gyro and everything else),
converts it into that game range using `GameDeadZone`, adds the macro offset there, clamps the
result to full deflection, and converts it back. So `PullY 6` means "6% of what the game can
actually see", whatever the dead zone is. When no macro is adding anything on a report, the stick
is passed through untouched.

**Down is +Y.** In the controller's stick values, down is the positive Y direction (byte 255 =
full down), so a positive `PullY` pulls the aim down. `DriftX` is positive to the right.

## Arming and disarming

- Press the full `Toggle` combo to arm; press it again to disarm. It flips once per press (when the
  last button of the combo goes down), so holding it does not flicker.
- The macros are **disarmed** when DS4Windows starts, on every profile load (including switching to
  a temporary profile and reloading the same profile), and when the controller disconnects. A combo
  that is already held when the profile loads or the controller reconnects does not count: release
  it and press it again.
- Arming needs at least one working aim layer in the profile. If no `<AimLayer>` block loads (for
  example its source profile is missing), the macros cannot arm and the armed colour never shows.
- The Log tab shows `Aim macros armed` or `Aim macros disarmed` with the profile name and the
  controller number, for example `Aim macros armed (profile "Edge Linear", controller 1)`. When a
  profile load or a disconnect disarms them, the line ends with `: profile loaded` or
  `: controller removed`.
- The toggle buttons are **not** removed from what the game sees. `FnL+FnR` (the DualSense Edge's
  Fn buttons) is recommended because the virtual controller has no Fn buttons, so the game never
  sees them. Any other combo **also reaches the game**.

### Lightbar

With `ArmedColor` set, the lightbar shows that colour while the macros are armed **and no aim layer
is active**. While a layer is active, its own lightbar cue is shown as usual (or the base colour, if
that layer's `UseSourceLightbar` is off). Like the aim-layer cue, the armed colour only replaces the
plain main colour: it is hidden while the app-level "Use Custom Color" is on, and the battery
gradient, rainbow and low-battery flash are not changed.

## The XML

### `<AimMacros>` (once per profile)

Put it as a direct child of `<DS4Windows>`, after the last `<AimLayer>` block.

```xml
<AimMacros>
  <Toggle>FnL+FnR</Toggle>
  <GameDeadZone>10</GameDeadZone>
  <GameDeadZoneShape>Radial</GameDeadZoneShape>
  <ArmedColor>255,0,255</ArmedColor>
</AimMacros>
```

| Element | Values | Default |
| --- | --- | --- |
| `Toggle` | 1 to 4 different button names joined by `+`, any case. **Required**; without a valid toggle the macros can never arm. | none |
| `GameDeadZone` | Percent 0-90 (decimals allowed). Set it to Fortnite's right-stick dead zone. | `0` |
| `GameDeadZoneShape` | `Radial` (on the stick's distance from centre) or `Axial` (per axis), any case. | `Radial` |
| `ArmedColor` | `R,G,B`, each 0-255. Optional. | none (no armed colour) |

Toggle button names: `Cross`, `Circle`, `Square`, `Triangle`, `DpadUp`, `DpadDown`, `DpadLeft`,
`DpadRight`, `L1`, `L3`, `R1`, `R3`, `Share`, `Options`, `PS`, `Mute`, `TouchButton` (or `Touch`),
`Capture`, `SideL`, `SideR`, `FnL`, `FnR`, `BLP`, `BRP`. L2 and R2 are not allowed.

### `<Recoil>` (inside an `<AimLayer>` block)

```xml
<Recoil>
  <FireThreshold>30</FireThreshold>
  <Delay>0</Delay>
  <Ramp>60</Ramp>
  <PullY>6</PullY>
  <DriftX>0</DriftX>
  <Pattern>0:8,300:5,1200:4</Pattern>
</Recoil>
```

| Element | Values | Default |
| --- | --- | --- |
| `FireThreshold` | Whole number 0-255. You are firing while raw R2 is **above** this. | `30` |
| `Delay` | Whole milliseconds 0-2000 after firing starts before the pull begins. | `0` |
| `Ramp` | Whole milliseconds 0-1000 to ease the pull in from 0 to full. | `0` (full at once) |
| `PullY` | Percent 0-50 of the game's live range, downwards. | `0` |
| `DriftX` | Percent -50 to 50, `+` = right. | `0` |
| `Pattern` | Optional. Up to 16 `ms:PullY` points, comma-separated, ms strictly ascending, PullY 0-50. Linear between points; the last value is held. Overrides `PullY`. | none |

The recoil timer starts on the report where R2 goes above `FireThreshold` while this layer is
active. It resets when R2 drops back or the active layer changes. Recoil belongs to the layer
block: with Edge Linear, aiming and firing means the **L2** layer is active, so recoil goes in the
L2 block (it checks R2 itself). Hip-fire gets no recoil unless its own block has a `<Recoil>`.

### `<Rotate>` (inside an `<AimLayer>` block)

```xml
<Rotate>
  <Radius>10</Radius>
  <RadiusY>10</RadiusY>
  <Period>60</Period>
  <Direction>CW</Direction>
  <WhenFiring>Any</WhenFiring>
  <FadeAbove>0</FadeAbove>
</Rotate>
```

| Element | Values | Default |
| --- | --- | --- |
| `Radius` | Percent 0-50 of the game's live range. **Required**. | none |
| `RadiusY` | Percent 0-50; makes an ellipse. | same as `Radius` |
| `Period` | Whole milliseconds 10-1000 per full circle. | `60` |
| `Direction` | `CW` or `CCW`, any case. | `CW` |
| `WhenFiring` | `Any`, `FiringOnly` or `NotFiring`, any case. Firing uses the block's `<Recoil>` `FireThreshold`, or R2 above 30 without one. | `Any` |
| `FadeAbove` | Percent 0-100 of your own stick. Above it the circle shrinks, reaching 0 at full stick. `0` = never fade. | `0` |

The circle starts at angle 0 when it starts and resets when it stops or the active layer changes.

Each block may have at most one `<Recoil>` and one `<Rotate>`. On save, `<Recoil>` then `<Rotate>`
are written after `<UseSourceLightbar>`. Every element is optional except where marked, and
only elements you wrote are saved, so a profile without macros saves exactly as before.

### Bad values

A bad value never stops the profile loading. One warning appears in the Log tab (once, not on
every reload), for example:

```
Aim macros of profile "Edge Linear", aim layer 1 (L2): <Recoil> ignored: <PullY> "60" is not a number from 0 to 50
```

- A bad value in `<Recoil>` or `<Rotate>` drops only that element.
- A bad `GameDeadZone`, `GameDeadZoneShape` or `ArmedColor` drops the whole `<AimMacros>`.
- A missing or bad `Toggle` keeps `<AimMacros>`, but the macros can never arm. The warning says
  `no valid toggle, so they can never arm`.

## Example: Edge Linear

`Edge Linear.xml` keeps its two aim layers. Recoil and the circle go inside the L2 block, and
`<AimMacros>` follows the last block:

```xml
<AimLayer>
  <Enabled>True</Enabled>
  <Trigger>L2</Trigger>
  <Threshold>100</Threshold>
  <SourceProfile>Edge Expo</SourceProfile>
  <UseSourceLightbar>True</UseSourceLightbar>
  <Recoil>
    <FireThreshold>30</FireThreshold>
    <Ramp>60</Ramp>
    <PullY>6</PullY>
  </Recoil>
  <Rotate>
    <Radius>10</Radius>
    <Period>60</Period>
    <Direction>CW</Direction>
  </Rotate>
</AimLayer>
<AimLayer>
  <Enabled>True</Enabled>
  <Trigger>R2</Trigger>
  <Threshold>250</Threshold>
  <Delay>180</Delay>
  <SourceProfile>Edge Hipfire</SourceProfile>
  <UseSourceLightbar>True</UseSourceLightbar>
</AimLayer>
<AimMacros>
  <Toggle>FnL+FnR</Toggle>
  <GameDeadZone>10</GameDeadZone>
  <GameDeadZoneShape>Radial</GameDeadZoneShape>
  <ArmedColor>255,0,255</ArmedColor>
</AimMacros>
```

Every XML sample in this guide is loaded by a test (`DS4WindowsTests/AimMacrosGuideTests.cs`).

To add it: close DS4Windows, back up the profile, edit
`%APPDATA%\DS4Windows\Profiles\Edge Linear.xml`, save, and start DS4Windows (see
[Adding it to a profile](aim-layer.md#adding-it-to-a-profile)).

## Tuning

1. **Dead zone.** Set `GameDeadZone` to Fortnite's right-stick dead zone value (and the same shape
   if you know it). If small pulls feel uneven across directions, try `Axial`.
2. **Recoil.** In Creative, face a wall at a fixed range and spray. Raise `PullY` in steps of 2
   until the spray stays level. Use `Pattern` only if the first shots kick harder than the rest.
3. **Circle.** Start with `Radius 10`, `Period 60`. If the wobble is too visible, use a smaller
   radius. If aim assist is not "sticking", try a shorter period or a larger radius. If fast flicks
   feel wobbly, try `FadeAbove 40`.

Close DS4Windows before each edit, and re-arm after starting it again.
