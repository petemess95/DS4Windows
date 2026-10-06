# Plan: Fast aim switching for DS4Windows (hbashton fork)

Written 2026-10-05.

**Status (2026-10-05):** Phase 1 is done (results under Task 1.1). The next step is **Phase 2, starting
at Task 2.1**. The kickoff message for that session is under "Next session" at the end of this file.

---

## 1. Goal

The user aims by holding **L2** on a **DualSense Edge**. While L2 is held they want a different
**right-stick output curve** than when hip-firing. Today they do this with a temporary profile
switch, which:

- is slow (~150 ms per switch), so quick aim taps get the wrong curve or no curve, and
- uses ~150 ms of CPU on every press and every release and leaks memory, which can make games stutter.

Deliverables, in order:

- **Phase 2, faster profile switching.** Make any profile switch cost a few ms instead of ~150 ms
  and stop the leak. Small and low risk, and a good candidate to send upstream as a pull request.
- **Phase 3, aim layer.** A per-profile setting: "while L2 is held, the right stick uses the stick
  settings from profile X". No profile switch at all; the curve changes on the next input report.

## 2. Context (already established; do not re-investigate)

### Repository
- `P:\codex\DS4Windows Branch` is a clone of https://github.com/hbashton/DS4Windows, based on `main`
  @ `3650240` ("Prepare RC4.6.6…"). Version 5.0.12.0 / `VIIPERRC4.6.6`.
- Remotes: `origin` = hbashton/DS4Windows (upstream); `fork` = https://github.com/petemess95/DS4Windows
  (the user's fork).
- Work happens on branch `fast-aim-switching`, which already exists, tracks `fork`, and contains this plan.
- The user's installed copy (`C:\Program Files\DS4Windows`) is the **same version**, RC4.6.6.
- Target is `net8.0-windows10.0.19041.0`, x64. .NET SDK 10.0.301 is installed and builds it.
- Build (known to work, a few minutes):
  `dotnet build DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 -v q -nologo`
- Run selected tests:
  `dotnet test DS4WindowsTests/DS4WindowsTests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~<Name>" -nologo`
- `DS4Windows/Properties/AssemblyInfo.cs` has `InternalsVisibleTo("DS4WindowsTests")`, so tests can call internals.
- The upstream branch `origin/fix-profile-switch-lag` (June 2026) is **stale and unmerged**. Do not
  use it; `main` has since rewritten this area.

### The user's setup (in `%APPDATA%\DS4Windows`)
- Base profile: `Profiles\Edge Linear.xml`. Aim profile: `Profiles\Edge Expo.xml`.
- The two files differ only in:
  - `RSOutputCurveMode`: `linear` (Edge Linear) vs `custom` with `RSOutputCurveCustom` `0.72, 0.26, 1.00, 1.00` (Edge Expo)
  - `Color`: `0,0,255` (Edge Linear) vs `255,255,255` (Edge Expo)
  - Edge Linear's `ProfileActions` and touchpad-click mappings
- The switch action, in `Actions.xml`:
  ```xml
  <Action Name="Switch 2 Edge Expo">
    <Trigger>L2</Trigger>
    <Type>Profile</Type>
    <Details>Edge Expo</Details>
    <UnloadTrigger>AutomaticUntrigger</UnloadTrigger>
  </Action>
  ```
- **Never modify the user's real `%APPDATA%\DS4Windows` files** during development. Copy them to a
  scratch or test folder if a test needs them.

### Evidence
- **Logs from RC4.6.2 (Sept 17–21):** ~1.47M lines of `Controller 1 is using Profile "Edge Expo"`,
  at ~507 per second while L2 was held. The switch action re-fired on every input report. **This is
  already fixed in RC4.6.3+**: logs since RC4.6.6 show one line per press. Do not re-fix it, but
  don't regress it either (see the tests in Phase 4).
- **Measured on RC4.6.6 code with the user's two profiles** (temporary MSTest probe, since deleted):

  | Measurement | Result |
  |---|---|
  | `PreparedProfileLoad.TryPrepare` (runs on every press and every release) | ~154 ms wall time, ~147 ms CPU, average of 40 runs |
  | `new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides())` | ~80 ms per call; the loaded-assembly count grows by 1 per call and never goes back down |
  | `Deserialize` using one reused serializer | ~2.5 ms |

  The remaining ~70 ms is most likely `ProfileMigration` (an XML document parse) plus
  `candidate.MapTo(BackingStore.CreateProfileValidationStore())`, which builds an entire
  `BackingStore` each time. That split has not been measured yet; Task 2.1 measures it.

### Key code locations
| What | Where |
|---|---|
| Serializer rebuilt on every load | `DS4Windows/DS4Control/PreparedProfileLoad.cs:88` |
| Serializer rebuilt on every save | `DS4Windows/DS4Control/ScpUtil.cs:5577` (`SaveProfileNew`) |
| Profile preparation (read file, migrate, deserialize, test-map to a scratch store) | `PreparedProfileLoad.TryPrepare` (`PreparedProfileLoad.cs:51`) |
| Applying a prepared profile to live settings | `BackingStore.ApplyPreparedProfileNew` (`ScpUtil.cs:6572`); `CompletePreparedProfileLoad` (`ScpUtil.cs:6675`) |
| Queued temp/regular switch worker | `Mapping.RequestProfileSwitch` / `RunProfileSwitchRequests` (`Mapping.cs:1273`, `:1491`); `GuardedProfileReload.Execute` (`GuardedProfileReload.cs`) |
| Hold-to-switch (automatic untrigger) logic | `Mapping.cs:317–407`, special-action handling at `Mapping.cs:5134–5210` |
| Per-press log line and tray toast | `Mapping.cs:5182–5186` |
| Validation store | `BackingStore.CreateProfileValidationStore()` (`ScpUtil.cs:5208`) |
| **Stick pipeline (only caller in the input path)** | `Mapping.SetCurveAndDeadzone` (`Mapping.cs:2076`), called from `ControlService.cs:6224` and the readings preview `DS4Forms/ControllerReadingsControl.xaml.cs:364` |
| Right-stick settings read on every report | `GetRSDeadInfo` (`Mapping.cs:2099`), `getRSSens` (`:2240`), `GetSquareStickInfo` (`:2256`, `rsMode`/`rsRoundness`), `getRsOutCurveMode` + `rsOutBezierCurveObj` (`:2268`) |
| Right-stick settings storage | `BackingStore` in `ScpUtil.cs` (`rsModInfo`, `_rsOutCurveMode` at ~4851, `rsOutBezierCurveObj` at 4836); `StickDeadZoneInfo` in `ProfilePropGroups.cs:44` |
| Profile XML format (the DTO in use) | `DS4Control/DTOXml/ProfileDTO.cs`: `MapFrom` at 2301, `MapTo` at 3015, right-stick curve at 1911/2650/3543, `GetAttributeOverrides` at 3979 |
| Output-curve helper | `DS4StickProfileTransform.ApplyOutputCurve` (`DS4StickProfileTransform.cs:256`) |
| L2 as a digital trigger | `Mapping.getBoolSpecialActionMapping`; `GetBoolMappingExternal` uses `cState.L2 > 100` (`Mapping.cs:6918`) |
| Lightbar main colour | `DS4LightBar.cs:~120` (`lightModeInfo.m_Led`) |
| Tests to model on or keep green | `DS4WindowsTests/`: `ProfileLoadPreparationTests`, `ProfileMigrationTests`, `ProfileTests`, `TemporaryProfileIntentTests`, `ProfileSwitchInputContinuityTests`, `ProfileSwitchRevisionTests`, `DS4StickProfileTransformTests`, `ProfileMappingLiveInputTests` |

## 3. Working rules for the implementing session

- Act as an **orchestrator**, per the user's global CLAUDE.md. Delegate each task below to the tier
  suggested. **User preference (2026-10-05): this work is sensitive, so use mainly worker-complex
  and worker-standard. Use the Sonnet workers (worker-rocket, worker-mechanical) only for the most
  menial tasks.** The tiers below already reflect this. Each delegation names this file and
  section, the exact files, the acceptance criteria, and what to return. Never pass a `model` parameter.
- Tasks in Phases 2 and 3 both touch `ScpUtil.cs`. **Run them in order, not in parallel**, unless
  a task's files are clearly separate.
- Match the surrounding code's style. The fork uses short "why" comments, `Volatile`/`Interlocked`
  for state shared between threads, and per-device arrays sized `Global.TEST_PROFILE_ITEM_COUNT`.
- Never allocate or take a lock in `SetCurveAndDeadzone`; it runs on every input report (up to ~1000/s).
- Commit after each task that passes its criteria.
- The user approved pushing `fast-aim-switching` to `fork`. Never push to `origin`, and don't open
  pull requests without asking the user.

## 4. Phases and tasks

| Phase | What | Tasks | Status |
|---|---|---|---|
| 1. Setup | Baseline build and tests; how to test on hardware | 1.1, 1.2 | Done 2026-10-05 |
| 2. Faster profile switching | Cut each switch from ~150 ms to a few ms; stop the leak | 2.1–2.3 | Next |
| 3. Aim layer | Swap right-stick settings while L2 is held, with no profile switch | 3.1–3.5 | Not started |
| 4. Testing | Full test run, then you test on the controller | 4.1, 4.2 | Not started |

Do the phases in order. Tasks are numbered `<phase>.<step>`.

### Phase 1: Setup
- **1.1 (orchestrator, inline).**
  - Confirm `fast-aim-switching` is checked out and clean.
  - Build, then run the baseline tests: `ProfileLoadPreparationTests`, `ProfileMigrationTests`,
    `ProfileTests`, `TemporaryProfileIntentTests`, `ProfileSwitchInputContinuityTests`,
    `DS4StickProfileTransformTests`.
  - Record which tests already fail on clean `main`, so they aren't blamed on our changes later.
  - **Done 2026-10-05 at `46b25cb` (plan-only commits on top of `3650240`, so the code is the same as `main`).**
    - Build: succeeded, 0 warnings, 0 errors.
    - The six baseline classes: **89 passed, 0 failed, 0 skipped.**
      Filter used: `--filter "FullyQualifiedName~ProfileLoadPreparationTests|FullyQualifiedName~ProfileMigrationTests|FullyQualifiedName~ProfileTests|FullyQualifiedName~TemporaryProfileIntentTests|FullyQualifiedName~ProfileSwitchInputContinuityTests|FullyQualifiedName~DS4StickProfileTransformTests"`
    - Full suite (`dotnet test ... --no-build`): **7130 passed, 0 failed, 12 skipped**, about 1 m 42 s.
      The 12 skips are opt-in or live-hardware tests and are expected: 3 × `LiveProcessCapture*`,
      `OptInInstalledBundleResolvesWithoutMutatingRegistryCacheOrLaunchingCode`, and 8 cases of
      `RealGoApiAndDs4ClientRetireExactXboxActivation`.
    - **No test fails on clean `main`.** Any failure from here on was caused by our changes.
- **1.2 (already investigated): how to run a local build on real hardware.**
  - DS4Windows only accepts the `viiper.exe` whose SHA-256 is compiled into it
    (`ViiperSetupManager.SupportedViiperSha256`, `DS4Control/Viiper/ViiperSetupManager.cs:170`, value `9392A49E…892B`).
    The installed `C:\Program Files\DS4Windows\VIIPER\viiper.exe` matches it exactly (checked
    2026-10-05). **Never change the VIIPER, USBip or HidHide constants or files on this branch.**
  - The install is self-contained (its runtimeconfig has `includedFrameworks`), and nothing checks
    the integrity of `DS4Windows.dll`.
  - Test method: swap only `DS4Windows.dll`. The user, as admin:
    1. Close DS4Windows.
    2. Back up `%APPDATA%\DS4Windows` and `C:\Program Files\DS4Windows\DS4Windows.dll`.
    3. Copy `DS4Windows\bin\x64\Release\net8.0-windows10.0.19041.0\DS4Windows.dll` over the
       installed one.
    4. Start DS4Windows.
    5. To revert, restore the backed-up DLL.
  - Don't run the build from `bin\`; it may offer VIIPER setup or repair, or clash with the running
    VIIPER.
  - After any upstream update, rebase the branch onto the new release before swapping the DLL again.
    A DLL built from an older commit won't match the new VIIPER version or the new dependencies.

### Phase 2: Faster profile switching

- **2.1 (worker-standard): measure where the remaining time goes.**
  - Add a benchmark test class, `ProfileSwitchCostBenchmark`, marked
    `[TestCategory("Benchmark")]` so normal runs can exclude it.
  - Time these separately: file read + `ProfileMigration`, `Deserialize`, and
    `MapTo(CreateProfileValidationStore())`. Also time the full `TryPrepare`, and check the
    loaded-assembly count before and after.
  - Use small test profiles under `DS4WindowsTests` (copy the right-stick curve values above), not
    the user's files.
  - Return the numbers.
  - **Done 2026-10-05** (`DS4WindowsTests/ProfileSwitchCostBenchmark.cs`, no production changes).
    - Run: `dotnet test ... --no-build --filter "FullyQualifiedName~ProfileSwitchCostBenchmark" -nologo --logger "console;verbosity=detailed"`.
      Exclude from normal runs with `--filter "TestCategory!=Benchmark"`.
    - Profiles: no fixture XML exists, so the test saves two full profiles (~17.8 KB each) the way
      `SaveProfileNew` does, one RS `linear`, one RS `custom` `0.72, 0.26, 1.00, 1.00`, in a `%TEMP%`
      folder, and alternates between them. 5 warm-up + 40 timed calls per stage.
    - `MeasureTryPrepare(paths)` returns average wall/CPU ms and assembly counts, for 2.2/2.3 checks.
    - Results before Phase 2 (two stable runs; two earlier runs under machine load were ~30 ms higher on e):

      | Stage | Avg wall ms | Avg CPU ms |
      |---|---|---|
      | a. file read + `ProfileMigration` | 0.11–0.13 | < 0.4 (timer resolution) |
      | b. `new XmlSerializer(ProfileDTO, overrides)` | 48.7–50.4 | 52.7–53.9 |
      | c. `Deserialize`, reused serializer | 0.45–0.51 | < 0.4 |
      | bc. new serializer + its first `Deserialize` | 104.6–106.2 | 103.5–105.1 |
      | d. `MapTo(CreateProfileValidationStore())` | 0.26–0.27 | — |
      | e. full `TryPrepare` (warm) | 104.9–106.0 | 102.7–103.5 |

    - Assembly count grows by exactly **+1 per `TryPrepare`** (186 → 231 over 45 calls).
    - Finding: almost all the cost is the per-call serializer. The constructor is about half; the first
      `Deserialize` on each new serializer (generated-code compile/JIT) is the other half. Migration,
      warm deserialize and the validation `MapTo` together are under 1.5 ms, so 2.2 alone should bring
      `TryPrepare` to ~1–2 ms; 2.3 mainly removes the remaining file read and validation work.
- **2.2 (worker-standard): build the serializer once and reuse it.**
  - Add one shared, thread-safe instance (for example `ProfileDTO.Serializer`, a
    `static readonly Lazy<XmlSerializer>` built with `GetAttributeOverrides()`).
  - Use it at `PreparedProfileLoad.cs:88` and `ScpUtil.cs:5577`.
  - Grep for any other `new XmlSerializer(` that passes `XmlAttributeOverrides` and treat it the same way.
  - Acceptance: a test shows repeated `TryPrepare` calls no longer increase the loaded-assembly
    count. Average warm `TryPrepare` drops by about 80 ms. Baseline tests are no worse than in 1.1.
  - **Done 2026-10-05.** `ProfileDTO.Serializer` (internal, `Lazy<XmlSerializer>`,
    `ExecutionAndPublication`) is used at `PreparedProfileLoad.cs:88` and in `SaveProfileNew`.
    - Grep found no other production `new XmlSerializer(` with overrides; the rest are plain
      `XmlSerializer(Type)`, which .NET caches. No call site attaches `Unknown*` events, so sharing is safe.
      About 30 test files still build their own serializer with overrides; that only costs test time.
    - New tests (`ProfileSerializerCacheTests`, regular suite): the same instance is returned each
      time, and 20 repeated `TryPrepare` calls leave the assembly count unchanged.
    - Benchmark: warm `TryPrepare` **0.90–0.93 ms** wall (was ~105 ms); assemblies 182 → 182 over 45
      calls (was +1 per call). The first profile load or save in a process still pays ~100 ms once.
    - Tests: baseline classes + new class 91/91; full suite with `TestCategory!=Benchmark`
      **7132 pass, 12 expected skips, 0 fail**.
    - Note: a full rebuild reports 12 warnings, which also appear without these changes and are in
      untouched files (1.1's "0 warnings" was likely an incremental build).
- **2.3 (worker-complex): cache prepared profiles.**
  - Cache per full path, keyed on `(LastWriteTimeUtc, Length)`. Store the text after migration, the
    `Migrated` flag, and a "passed validation" flag.
  - On a cache hit, skip the file read, the migration, and the `MapTo(validation store)` check. Only
    deserialize into a **new** `ProfileDTO` (`Claim()` consumes it, so DTOs are never shared).
  - Keep it small: about 16 entries, least recently used dropped first. Make it thread-safe; the
    profile worker, auto-profile and the UI all call `TryPrepare`.
  - Clear the entry explicitly in `SaveProfileNew` and wherever profiles are deleted or renamed
    (grep the profile list and editor code), in addition to the timestamp/size check.
  - Never cache a failed preparation.
  - Acceptance:
    - Warm `TryPrepare` averages under 10 ms in the 2.1 benchmark.
    - New tests cover: editing the file (new timestamp or size) is picked up; a hit returns a fresh
      DTO each time; a migrated profile is re-read after the auto-save rewrites it; a missing or
      invalid file still fails the same way as before (`ProfilePreparationFailure` values unchanged).
    - Baseline tests are no worse than in 1.1.

### Phase 3: Aim layer (right-stick settings swap while L2 is held)

Design decisions (already made):

- **Borrow the curve from a source profile.** The base profile names another profile, e.g. "Edge
  Expo", and borrows its right-stick settings. The user keeps editing curves in the normal profile
  editor, so no curve-editing UI is needed.
- **What gets swapped:** the right stick's `StickDeadZoneInfo` (dead zone, anti-dead zone, max zone,
  max output, fuzz, dead-zone type, and so on), the RS sensitivity, the square-stick RS mode and
  roundness, the RS output-curve mode, and the RS bezier curve.
- **What does not get swapped:** RS rotation, anti-snapback and calibration. They feed filters that
  keep state between reports, so switching them mid-stream could make the stick jump.
- **When the layer is on:** decided from the **raw** `cState.L2` captured at the top of
  `SetCurveAndDeadzone`, *before* `cState` is reassigned at `Mapping.cs:2107`. The default threshold
  matches today's special-action L2 trigger (`> 100`, confirm in `getBoolSpecialActionMapping`), so
  it feels the same as the user's current setup. v1 supports L2 and R2 only.
- **How the borrowed settings are stored:** an immutable `AimLayerStickSettings` object per device,
  published with `Volatile.Write` and read with `Volatile.Read`. It is built off the input thread
  when the base profile loads, by preparing the source profile (fast thanks to 2.3) and copying
  values out of a scratch store. **Never** copy them out of a live device slot.
- **Edge cases:**
  - Missing or invalid source profile: log one warning and leave the layer off.
  - Source is the base profile itself: ignore it.
  - Rebuild the borrowed settings whenever the base profile reloads, and when `SaveProfileNew` saves
    a profile that some active device is borrowing from.
- **Game output:** L2's own output is unchanged; the game still sees L2 for aim-down-sights.

Tasks:

- **3.1 (worker-complex): data model and XML.**
  - Add per-device settings to `BackingStore`: `aimLayerEnabled` (bool), `aimLayerTrigger`
    (`DS4Controls`, default `L2`), `aimLayerThreshold` (byte, default 100), `aimLayerSourceProfile`
    (string), `aimLayerUseSourceLightbar` (bool).
  - Reset them in `ResetProfile`. Save and load them through `ProfileDTO` (`MapFrom`/`MapTo`) as a
    nested `<AimLayer>` element, following the existing DTO patterns.
  - Check whether the old `XmlDocument` load/save path (`ScpUtil.cs` ~5893 and 6729+) is still used;
    update it only if it is.
  - Acceptance: a save-then-load test keeps every field. A profile without `<AimLayer>` loads with the
    layer off. Existing `ProfileTests` and `ProfileMigrationTests` still pass.
- **3.2 (worker-complex): build and publish the borrowed settings.**
  - Add `AimLayerStickSettings` (immutable) and a per-device published reference.
  - Build it during profile apply as described above, without blocking the report-pause window. Keep
    preparation outside `TryHaltReportingRunAction`, the same way `GuardedProfileReload` prepares
    before it pauses.
  - Clear it when the layer is disabled or the controller is removed.
  - Acceptance: tests show the borrowed settings match the source profile's right-stick values;
    editing and saving the source profile rebuilds them; a missing source leaves them empty.
- **3.3 (worker-complex): runtime swap in `SetCurveAndDeadzone`.**
  - When the layer is on, take `rsMod`, `rsSens`, the RS square-stick fields, the curve mode and the
    bezier curve from the borrowed settings.
  - Must not allocate or lock in the per-report path.
  - Acceptance: a mapping test (model on `DS4StickProfileTransformTests` / `ProfileMappingLiveInputTests`)
    shows the base curve with L2 at or below the threshold and the source curve above it, switching
    on the very next report.
  - Also: the left stick and triggers are unchanged; L2's output value is unchanged; with no layer,
    the output is byte-for-byte identical to before.
- **3.4 (worker-standard): optional lightbar cue.**
  - When `aimLayerUseSourceLightbar` is set and the layer is on, show the source profile's main
    colour (keep it in the borrowed settings) wherever `DS4LightBar` picks `m_Led`.
  - Don't change flashing or battery-indicator behaviour.
- **3.5 (worker-standard): hand-editing guide. No editor UI (user decision, 2026-10-05).**
  - Write `docs/aim-layer.md`. It should cover:
    - what the aim layer does, and which right-stick settings it swaps and which it keeps;
    - the exact `<AimLayer>` XML block from 3.1, with every field, its allowed values and its default;
    - where to put it in a profile, with the user's own example (Edge Linear borrowing from
      "Edge Expo", L2, threshold 100, source lightbar on);
    - **Close DS4Windows before editing a profile file**, or it may overwrite the change;
    - once 3.1 is in, saving the profile from the normal editor keeps the block;
    - remove any old hold-to-switch profile action for the same trigger;
    - what happens if the source profile is missing, and where the warning appears.
  - The element names in the doc must exactly match what 3.1 implemented. Check against
    `ProfileDTO.cs`, and include a test-backed sample if one exists.
  - Editor UI is deferred. Revisit it only if the user asks after trying the aim layer.

### Phase 4: Testing
- **4.1 (orchestrator):**
  - Run the full test suite and compare with the 1.1 baseline.
  - Re-run the 2.1 benchmark and record the before and after numbers in the commit message or PR text.
- **4.2 (the user, on hardware, using the steps from 1.2):**
  1. **Phase 2 alone.** Keep the existing "Switch 2 Edge Expo" action.
     - Rapid L2 taps (under 150 ms), holds of 1–2 s, and alternating quickly for 30 s.
     - In Task Manager, DS4Windows CPU should stay near idle.
     - The log should show one line per press.
     - The curve should feel correct on quick taps.
  2. **Phase 3.** With DS4Windows closed, follow `docs/aim-layer.md` to add the `<AimLayer>` block to
     Edge Linear: source "Edge Expo", trigger L2, threshold 100, source lightbar on. **Remove** the
     "Switch 2 Edge Expo" action from Edge Linear.
     - Repeat the same tests: the curve should change instantly and the lightbar should switch to
       white while aiming.
     - The game's ADS should behave exactly as before.

## 5. Decisions
Made 2026-10-05:
- Quieter per-press logging (formerly task A3): **dropped**. Keep the "using Profile" log line; it costs about one line per press and is useful evidence if switching misbehaves.
- Task 3.5: **hand-editing guide only**; editor UI deferred.
- Pushing: **yes**, to the user's fork (`fork` remote, `petemess95/DS4Windows`).

Still open (ask the user once Phase 2 passes the Phase 4 tests):
- Offer the Phase 2 speed-up to `hbashton/DS4Windows` as a pull request? Recommended: upstream changes this area
  often, and a merged fix avoids redoing it every release. Leave this plan file out of that pull
  request.

## 6. Next session

Paste this to start Phase 2:

> Read `PLAN-fast-aim-switching.md` in `P:\codex\DS4Windows Branch` and implement Phase 2
> (Tasks 2.1, 2.2, 2.3, in that order). Phase 1 is done: the baseline is under Task 1.1
> (the 6 baseline classes: 89 pass; full suite: 7130 pass, 12 expected skips, 0 fail).
>
> Orchestrate it, and use mainly worker-complex and worker-standard agents. This work is too
> sensitive to use the Sonnet workers for all but the most menial tasks.
>
> Commit after each task that passes its acceptance criteria. Once Phase 2 is complete, stop, make
> sure all documentation is updated (including the status table and Task 2.1's benchmark numbers),
> and write a handoff message like this one for Phase 3.
