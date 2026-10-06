# Plan: Fast aim switching for DS4Windows (hbashton fork)

Written 2026-10-05.

**Status (2026-10-06):** Phases 1–4 are done (results under Tasks 1.1, 2.1, 2.2, 3.1–3.5, 4.1 and
4.2; Task 2.3 was dropped, see its note). Phase 2 and the aim layer both passed on the user's DualSense
Edge. The upstream pull request branch is prepared (section 5). **Phase 5 (hipfire layer): Tasks 5.1–5.4
done 2026-10-06; 5.5 (user hardware test) is next.**

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
  - `Color`: `0,0,255` (Edge Linear) vs `255,255,255` (Edge Expo). *As of 2026-10-06 the user has
    changed Edge Expo to `255,0,0` (red).*
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
| 2. Faster profile switching | Cut each switch from ~150 ms to a few ms; stop the leak | 2.1–2.3 | Done 2026-10-05 (2.3 dropped; warm `TryPrepare` ~105 ms → ~0.9 ms) |
| 3. Aim layer | Swap right-stick settings while L2 is held, with no profile switch | 3.1–3.5 | Done 2026-10-05 (`749f3d8`…`c2db58a`; guide in `docs/aim-layer.md`) |
| 4. Testing | Full test run, then you test on the controller | 4.1, 4.2 | Done 2026-10-06 (all pass; no fixes needed) |
| 5. Hipfire layer | Several aim layers per profile, first match wins, with an optional hold delay (R2 → Edge Hipfire after 100 ms, L2 still wins) | 5.1–5.5 | 5.1–5.4 done 2026-10-06 (`9da1d4b`, `0ece443`, `e321d2f`); 5.5 pending |

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
  - **Dropped 2026-10-05 (user decision).** After 2.2, warm `TryPrepare` is ~0.9 ms, already under
    this task's 10 ms target, and the leak is gone. The cache would save at most ~0.5 ms more per
    switch, and its invalidation (save, delete, rename, timestamps) risks loading a stale curve. Revisit
    only if the 4.2 hardware test shows switching is still slow.

### Phase 3: Aim layer (right-stick settings swap while L2 is held)

Design decisions (already made):

- **Borrow the curve from a source profile.** The base profile names another profile, e.g. "Edge
  Expo", and borrows its right-stick settings. The user keeps editing curves in the normal profile
  editor, so no curve-editing UI is needed.
- **What gets swapped:** the right stick's `StickDeadZoneInfo` (dead zone, anti-dead zone, max zone,
  max output, dead-zone type, and so on), the RS sensitivity, the square-stick RS mode and
  roundness, the RS output-curve mode, and the RS bezier curve.
- **What does not get swapped:** RS rotation, anti-snapback, calibration and (decided in 3.3) the RS
  fuzz value. They feed filters that keep state between reports, so switching them mid-stream could
  make the stick jump.
- **When the layer is on:** decided from the **raw** `cState.L2` captured at the top of
  `SetCurveAndDeadzone`, *before* `cState` is reassigned at `Mapping.cs:2107`. The default threshold
  matches today's special-action L2 trigger (`> 100`, confirm in `getBoolSpecialActionMapping`), so
  it feels the same as the user's current setup. v1 supports L2 and R2 only.
- **How the borrowed settings are stored:** an immutable `AimLayerStickSettings` object per device,
  published with `Volatile.Write` and read with `Volatile.Read`. It is built off the input thread
  when the base profile loads, by preparing the source profile (~1 ms warm thanks to 2.2) and copying
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
  - **Done 2026-10-05.**
    - `BackingStore` (`ScpUtil.cs`, after the DualSense options): per-device arrays `aimLayerEnabled`,
      `aimLayerTrigger`, `aimLayerThreshold`, `aimLayerSourceProfile`, `aimLayerUseSourceLightbar`;
      constants `DEFAULT_AIM_LAYER_TRIGGER` (`L2`), `DEFAULT_AIM_LAYER_THRESHOLD` (100);
      `NormalizeAimLayerTrigger`; private `ResetAimLayer`, called from `ResetProfile`. Read-only Global
      array accessors `AimLayerEnabled`, `AimLayerTrigger`, `AimLayerThreshold`, `AimLayerSourceProfile`,
      `AimLayerUseSourceLightbar` (like `GameBarProfileName`).
    - XML (`ProfileDTO.cs`: class `AimLayerSettingsDTO`, property `ProfileDTO.AimLayer`, written after `<TriggerLab>`;
      element order does not matter on load):
      ```xml
      <AimLayer>
        <Enabled>True</Enabled>
        <Trigger>L2</Trigger>            <!-- L2 or R2 -->
        <Threshold>100</Threshold>       <!-- 0-255; layer on when trigger > threshold -->
        <SourceProfile>Edge Expo</SourceProfile>
        <UseSourceLightbar>True</UseSourceLightbar>
      </AimLayer>
      ```
      Any child may be left out (default used). Omitted on save while all five values are default, like
      `<AudioHaptics>`/`<TriggerLab>`, so existing profiles save byte-for-byte as before; a disabled block
      that names a source is still written. `MapTo` always writes all five fields (from a default DTO when
      the element is missing), so a profile without `<AimLayer>` clears a slot that had the layer on.
    - Lenient parsing (the block is hand-edited): bools via `StrToBool` (any case; bad → false); trigger is
      case-insensitive, anything other than L2/R2 (or unparsable) → **L2**; threshold not 0–255 → 100;
      `SourceProfile` is trimmed. Bad values never reject the profile.
    - Threshold check: `GetBoolMappingExternal` uses `cState.L2 > 100`; `getBoolSpecialActionMapping` uses
      `fieldMap.triggers[...] > 100`, but on the state *after* `SetCurveAndDeadzone` (L2 dead zone, max
      zone, sensitivity, curve applied). Default 100 matches; with default L2 settings processed == raw.
    - Old `XmlDocument` path: **not used in production**. Instance `LoadProfile` (~6770) has no callers;
      `SaveProfileOld` is only called from it and from `AxisFlickCalibrationSettingsTests`. Not updated.
    - Copy paths: none need changes. The validation store is a fresh `BackingStore` filled by `MapTo`;
      `ApplyPreparedProfileNew` does `ResetProfile` + `MapTo`; preset/blank profiles call `ResetProfile`;
      no slot-to-slot copy helper exists.
    - Tests: `DS4WindowsTests/AimLayerProfileTests.cs`, 22 pass (round trip with R2/180, omitted when
      default, missing element → defaults, missing element clears a previous layer, `MapTo` alone clears,
      `ResetProfile`, the sample block above, trigger/threshold fallbacks). Baseline classes 91/91 (113 with
      the filter as written, since `~ProfileTests` also matches the new class); full suite with
      `TestCategory!=Benchmark` **7154 pass, 12 expected skips, 0 fail**. Build: 12 pre-existing warnings, none new.
- **3.2 (worker-complex): build and publish the borrowed settings.**
  - Add `AimLayerStickSettings` (immutable) and a per-device published reference.
  - Build it during profile apply as described above, without blocking the report-pause window. Keep
    preparation outside `TryHaltReportingRunAction`, the same way `GuardedProfileReload` prepares
    before it pauses.
  - Clear it when the layer is disabled or the controller is removed.
  - Acceptance: tests show the borrowed settings match the source profile's right-stick values;
    editing and saving the source profile rebuilds them; a missing source leaves them empty.
  - **Done 2026-10-05.**
    - New file `DS4Control/AimLayerStickSettings.cs` (namespace `DS4Windows`):
      - `AimLayerStickSettings` (sealed, get-only): `BaseProfile`, `SourceProfile`, `Trigger`, `Threshold`,
        `UseSourceLightbar` (from the base profile); `RSModInfo` (`StickDeadZoneInfo`, deep copy incl. x/y axis
        infos), `RSSens`, `RSSquareStick`, `RSSquareStickRoundness`, `RSOutCurveMode`, `RSOutBezierCurve`
        (`BezierCurve.CloneBuilt()`: own LUT copy + the immutable evaluator, no re-init/log), `LightbarColor`
        (source `m_Led`, a struct). `IsTriggerHeld(byte l2, byte r2)` = raw trigger `> Threshold`.
        `FromStore(request, store, device)` copies from a scratch store.
      - `AimLayerRequest` (what the base asks for; kept even when the build fails), `AimLayerPreparation`.
      - `AimLayerState` (static, per slot, `TEST_PROFILE_ITEM_COUNT`): **`AimLayerState.Current(device)`** =
        `Volatile.Read` of the published reference, null = off. This is the only call for 3.3/3.4.
        Writers: `Publish`, `Clear`, `Suspend`/`Resume`, `OnProfileSaved` (all under a per-slot lock that
        never does I/O; the report path takes no lock).
    - Build: `PreparedProfileLoad.TryPrepare` now also prepares the aim layer from its own validation store
      (`AimLayerState.Prepare`): source via new `TryPrepareSource` (same parse/migrate/`MapTo` into a fresh
      `CreateProfileValidationStore()`; the source's own `<AimLayer>` is ignored) and stores it in
      `prepared.AimLayer`. So the source file is read wherever the base is prepared — always before
      `TryHaltReportingRunAction`/the mutation gate — and never from a live slot. Never throws.
    - Hooks (live store only, `ReferenceEquals(this, Global.store)`):
      - `ApplyPreparedProfileNew`, right after `MapTo(this)`: `Publish(device, prepared.AimLayer)` — only a
        reference swap inside the pause, so base profile and layer change together. Every production load
        (UI/startup `LoadProfile`, `LoadTempProfile`, `GuardedProfileReload`, `GuardedNamedProfileLoad`,
        auto-profile) goes through this. A temp profile without `<AimLayer>` publishes null; returning to
        the base republishes.
      - `ResetAimLayer` (from `ResetProfile`): `Clear`. Covers the missing-file fallback, blank/default
        profiles and `SaveAsNewProfile`.
      - `SaveProfileNew` (any store, after a successful write): `OnProfileSaved(proName)` rebuilds,
        synchronously (~1 ms per borrowing slot; serializer already warm from the save), every slot whose
        request borrows that name (`OrdinalIgnoreCase`). Works with no ControlService.
      - `ControlService.ClearExactControllerSlot`: `Suspend` (publish null, keep request/built). The two
        reconnect paths that keep the profile without reloading it (temp/auto profile, Joy-Con handoff:
        `PrepareConnectedInputControllerProfileMappingOutput` and the Switch2 profile stage) call `Resume`.
        Normal reconnects reload the profile, which republishes.
    - Races: apply order is already serialized by the profile revision/mutation gate. Each slot has a
      generation; a save-triggered rebuild only publishes if no apply/rebuild happened since it started,
      otherwise it re-reads the request and retries (up to 4) while the slot still borrows that name, so the
      last write wins. A global save sequence is read before `TryPrepare` reads the base; if any profile was
      saved between prepare and apply, `Publish` queues one thread-pool rebuild. Test seam:
      `AimLayerState.RebuildBuiltForTests`.
    - Edge cases: disabled → null; source == base (case-insensitive) → null, no log; empty, invalid-file-name,
      missing or invalid source → null plus one warning (`AppLogger.LogToGui(..., true)`, text starts
      "Aim layer of profile ..."), de-duplicated per slot until a build succeeds (so temp switches back to
      the base don't spam). The request is kept, so creating/saving the source later turns the layer on.
    - For 3.3: read `AimLayerState.Current(device)` once per report; if non-null and
      `IsTriggerHeld(rawL2, rawR2)`, use `RSModInfo`, `RSSens`, `RSSquareStick`/`RSSquareStickRoundness`,
      `RSOutCurveMode`, `RSOutBezierCurve`. Use the borrowed `RSModInfo` consistently (it also decides
      Radial-only `RSSens`). `rsMod.fuzz` feeds the stateful `filters.Right.ApplyFuzz` before the dead
      zone — decide whether to keep the base fuzz there. For 3.4: `UseSourceLightbar` + `LightbarColor`.
    - Note: opening a base profile in the editor (slot 8) also builds its layer, so a missing source logs
      the warning there too.
    - Tests: `DS4WindowsTests/AimLayerStickSettingsTests.cs`, 15 pass (match incl. Axial axis infos and
      curve evaluation/LUT vs `0.72, 0.26, 1.00, 1.00`; R2/threshold; save rebuilds, case-insensitive;
      missing/invalid source + single warning + later creation; source == base; disabled; temp profile
      clears and base restores; missing base clears; independence from scratch store and live slot;
      suspend/resume; two generation-race tests; save between prepare and apply). The three race tests
      fail with their guard disabled. `AimLayerProfileTests` 22 pass; baseline classes 91; full suite with
      `TestCategory!=Benchmark` **7169 pass, 12 expected skips, 0 fail**. Build: 12 pre-existing warnings.
- **3.3 (worker-complex): runtime swap in `SetCurveAndDeadzone`.**
  - When the layer is on, take `rsMod`, `rsSens`, the RS square-stick fields, the curve mode and the
    bezier curve from the borrowed settings.
  - Must not allocate or lock in the per-report path.
  - Acceptance: a mapping test (model on `DS4StickProfileTransformTests` / `ProfileMappingLiveInputTests`)
    shows the base curve with L2 at or below the threshold and the source curve above it, switching
    on the very next report.
  - Also: the left stick and triggers are unchanged; L2's output value is unchanged; with no layer,
    the output is byte-for-byte identical to before.
  - **Done 2026-10-05.**
    - `Mapping.SetCurveAndDeadzone` only (`Mapping.cs`, ~2076): first line reads `AimLayerState.Current(device)`
      and drops it to null unless `IsTriggerHeld(cState.L2, cState.R2)` on the **raw** input (before rotation,
      calibration and `cState` reassignment). Null = no layer or trigger not held; the null path is one
      `Volatile.Read` + null check, then the original code with the same reads in the same order.
    - Swapped while held, through locals: `rsMod` (reassigned to `RSModInfo` *after* RS fuzz, so it drives
      `ApplyDeadzoneAndOuter`, the Radial-only `rsSens` check and `ApplyOutputCurve`), `rsSens`, RS square-stick
      mode/roundness, RS curve mode and `RSOutBezierCurve`. The helpers only read the borrowed objects
      (`ApplyOutputCurve` uses `CaptureEvaluator()` = `Volatile.Read` + `arrayBezierLUT`); `outSqrStk` is per-call
      scratch with no state across reports.
    - Kept on the base profile: RS rotation, anti-snapback, calibration drift and **fuzz** (`DS4StickFilter.ApplyFuzz`
      keeps the last sample and *resets when the delta changes*, so a borrowed fuzz would reset it on every
      press/release). No other stateful RS filter reads `rsMod` or the curve. Left stick, triggers (L2 output
      unchanged), gyro: untouched.
    - Other RS consumers, not swapped: RS `Controls` mode (normal output, stick-to-mouse/mouse-joystick
      bindings, delta accel) maps the processed state, so it follows the layer automatically. RS flick stick reads
      the device's raw RS (`getCurrentStateRef()`), so no dead zone/curve applies to it, base or layer. Their
      own settings (`rsOutputSettings`) are not part of the borrowed set. `getMouseMapping` reads `getRSDeadzone(device) == 0`
      to add a 3-unit dead zone for RS-as-mouse buttons; left on the base (cosmetic, only differs if exactly one
      profile has RS dead zone 0). The readings preview calls the same function with the editor slot, so it shows
      the layer while the trigger is held (slot 8 gets a layer when a base profile is opened in the editor).
      UI/editor/XML readers are not per-report.
    - No allocation/lock: by inspection (no LINQ/closures/boxing/new; ternaries on fields), and a test measures
      `GC.GetAllocatedBytesForCurrentThread()` = 0 over 20,000 warm calls each with no layer and with a layer
      alternating on/off every report.
    - Tests: `DS4WindowsTests/AimLayerMappingTests.cs`, 7 pass: linear base / Edge Expo source alternating
      L2 0/100/101/255 report by report; R2 trigger with threshold 180; LS/L2/R2/LS-outer unchanged; no-layer
      equivalence across 6 RS variants (radial/axial, square, modes 0/1/2/5/6, sens, max output, outer bind) x
      LS/trigger settings x 127 byte + high-res inputs (layer published but never held, and a layer borrowing the
      base's own settings, both identical to cleared); layer-on output equals the base with the source RS settings
      for every variant; rotation + calibration, fuzz (both directions, incl. toggling mid-hold) and anti-snapback
      from the base; allocation. Mutation checks: borrowing fuzz fails the fuzz test; deciding from processed
      L2 fails 5 tests. Existing `MappedStickProductionPipelineChainingTests` (frozen legacy oracle) still pass.
      `AimLayer` 44 (37 + 7); baseline 91; full suite `TestCategory!=Benchmark` **7176 pass, 12 expected skips,
      0 fail**. Build: 0 warnings incremental (12 pre-existing on rebuild).
    - For 3.4: the "layer on" decision is a local in `SetCurveAndDeadzone`, not published. The lightbar should
      repeat it from the raw state (`AimLayerState.Current(device)?.IsTriggerHeld(rawL2, rawR2)`) or 3.4 could add
      a per-device `Volatile` bool written there (a store per report, no allocation).
- **3.4 (worker-standard): optional lightbar cue.**
  - When `aimLayerUseSourceLightbar` is set and the layer is on, show the source profile's main
    colour (keep it in the borrowed settings) wherever `DS4LightBar` picks `m_Led`.
  - Don't change flashing or battery-indicator behaviour.
  - **Done 2026-10-05.**
    - "On": `SetCurveAndDeadzone` writes `AimLayerState.SetHeld(device, aimLayer != null)` right after its raw-trigger
      decision (one `Volatile.Write` per report, no alloc/lock). Chosen over re-reading triggers in the lightbar:
      the stick decides from `CurrentState[ind]` / `device.JointState` (copied, joined), which `DS4LightBar` can't
      see; `getCurrentStateRef()` is a different buffer. `updateLightBar` runs later in the same report on the same
      thread, so both agree per report.
    - Colour: non-rainbow base colour moved into `DS4LightBar.StaticBaseColor(info, device, battery)` (custom →
      `m_CustomLed`; LED-as-battery → `m_LowLed`→`m_Led` gradient; else plain `m_Led`). Only the plain branch calls
      `AimLayerState.MainLightbarColor(device, m_Led)`, which returns `LightbarColor` iff the layer is published,
      `UseSourceLightbar` and held (`Current` null wins over a stale held flag).
    - Precedence: custom colour, rainbow and the battery gradient are not swapped; everything applied after the base
      colour still wins (low-battery flash colour/pulse, idle fade, charging modes, forced/macro lightbar,
      default/shutdown light, distance, OpenRGB, flash durations). No shift colour exists in this fork.
    - Prompt update: `updateLightBar` runs every report; `SetLightbarState` marks dirty on any change, so press and
      release show on the next report.
    - Tests: `DS4WindowsTests/AimLayerLightbarTests.cs`, 5 pass (L2 sequence at/below/above threshold, R2 trigger,
      `UseSourceLightbar` off, no layer + clear while held, battery gradient and custom unaffected). `AimLayer` 49
      (44 + 5); full suite `TestCategory!=Benchmark` **7181 pass, 12 expected skips, 0 fail**. Build: 0 warnings
      incremental.
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
  - **Done 2026-10-05.**
    - `docs/aim-layer.md`: behaviour, swapped/kept RS settings (checked against `SetCurveAndDeadzone` and
      `AimLayerStickSettings`; fuzz kept on the base, flick stick unaffected), the `<AimLayer>` table with
      defaults and fallbacks (names checked against `AimLayerSettingsDTO`; sample = `SampleAimLayerXml`),
      placement + Edge Linear example, close-DS4Windows/backup warning, save behaviour, removing the old
      switch action, lightbar precedence, missing-source warning text, raw vs processed threshold.
    - No docs index links individual guides (README only links `docs/getting-started.md`); no link added.

### Phase 4: Testing
- **4.1 (orchestrator):**
  - Run the full test suite and compare with the 1.1 baseline. After Phase 3 (`c2db58a`) the full
    suite with `--filter "TestCategory!=Benchmark"` gives **7181 pass, 12 expected skips, 0 fail**
    (7132 + 49 `AimLayer*` tests). The 1.1 baseline filter now also matches `AimLayerProfileTests`
    (`~ProfileTests` is a substring match); add `&FullyQualifiedName!~AimLayer` to get the 91.
  - Re-run the 2.1 benchmark and record the before and after numbers in the commit message or PR text.
  - **Done 2026-10-05 at `3511ab4`.**
    - Build: 0 warnings, 0 errors (incremental).
    - Full suite `TestCategory!=Benchmark`: **7181 pass, 12 expected skips (same 12 as 1.1), 0 fail**, 1 m 31 s.
    - 1.1 baseline classes with `&FullyQualifiedName!~AimLayer`: **89 pass, 0 fail** (same as 1.1; the "91"
      above also counted the 2 `ProfileSerializerCacheTests`).
    - Benchmark, same session, same machine. "Before" = `f948210` (2.1 commit, pre-2.2) built in a temporary
      worktree; "after" = `3511ab4`:

      | Stage | Before wall / CPU ms | After wall / CPU ms |
      |---|---|---|
      | a. read + `ProfileMigration` | 0.111 / 0.0 | 0.111 / 0.4 |
      | c. `Deserialize`, reused serializer | 0.423 / 0.4 | 0.541 / 0.4 |
      | d. `MapTo(CreateProfileValidationStore())` | 0.291 / 0.4 | 0.258 / 0.0 |
      | **e. full `TryPrepare` (warm)** | **101.1 / 100.0** | **0.885 / 1.2** |
      | Assemblies over 45 `TryPrepare` calls | 186 → 231 (+1/call) | 182 → 182 (+0) |

      (CPU values under ~0.4 ms are timer resolution.) Stage b/bc (a new serializer per call, ~46–47 / ~101 ms)
      still measures the constructor directly and is unchanged, as expected; production no longer calls it per switch.
    - Release DLL for 4.2: `DS4Windows\bin\x64\Release\net8.0-windows10.0.19041.0\DS4Windows.dll`, 5.0.12.0,
      11,013,120 bytes, SHA-256 `b0db97fe…3bebe811`.
- **4.2 (the user, on hardware, using the steps from 1.2):**
  1. **Phase 2 alone.** Keep the existing "Switch 2 Edge Expo" action. The DLL already contains
     Phase 3, but the layer stays off until a profile has an `<AimLayer>` block, so this step
     still tests Phase 2 only.
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
  - **Done 2026-10-05/06 by the user, Release DLL from `3511ab4` (SHA-256 `b0db97fe…`), DLL-swap method from 1.2.**
    - First launch after the swap showed "VIIPER verification failed … usbip-win2 driver integrity could not be
      verified: Timed out". That is the WMI `Win32_SystemDriver` query exceeding its 2 s limit
      (`ViiperSetupManager.EvaluateUsbipDriverIntegrity`, code unchanged from `main`); the same query ran in
      166–425 ms afterwards. Closing the dialog (not Install / Repair) and relaunching worked. Transient, not
      caused by the branch.
    - **Part 1 (Phase 2, "Switch 2 Edge Expo" action): pass.** CPU peaked at ~1.5% during rapid tapping (stock:
      3–5%); one log line per press; curve "feels great" on quick taps.
    - Lightbar did not change on the hold-to-switch, **with the stock DLL too**. Cause (worker-complex trace +
      read of `Profiles.xml`): Controller 1 had **"Use Custom Color"** on (`<CustomLed1>True:255,98,0</CustomLed1>`),
      an app-level per-controller setting that overrides every profile colour by design (`DS4LightBar.StaticBaseColor`,
      same order as `main`). The trace confirmed the profile colour does reach a `DualSenseDevice` every report
      when custom colour is off, including on temp switches. Not a bug; the user switched to "Use Profile Controls".
      Documented consequence: the aim layer's lightbar cue is also hidden while "Use Custom Color" is on (Task 3.4
      decision, covered by `AimLayerLightbarTests`). Making the cue override custom colour was offered as optional
      and not requested.
    - **Part 2 (aim layer): pass.** Edge Linear with the `<AimLayer>` block from `docs/aim-layer.md` and
      "Switch 2 Edge Expo" removed from `<ProfileActions>`: curve changes on L2, ADS behaves as before, lightbar
      changes on L2 (custom colour off). Log: no aim-layer warning and no per-press profile lines.
    - User's backups: `%USERPROFILE%\DS4W-backup-2026-10-05` (original DLL + `%APPDATA%\DS4Windows` copy).

### Phase 5: Hipfire layer (R2 without L2, after a hold delay)

**Goal (user, 2026-10-06).** Linear right stick for movement and shotgun fights; a mild curve while
spraying from the hip; the existing stronger curve while aiming.

| Held (raw triggers) | Right stick uses |
|---|---|
| Nothing, or R2 for under 100 ms | Edge Linear (base) |
| R2 for 100 ms or more, L2 not held | **Edge Hipfire** (new) |
| L2, with or without R2 | Edge Expo (existing layer) |

**The user's profiles (read 2026-10-06, read-only).** `Edge Hipfire.xml` is a copy of `Edge Linear.xml`
except `RSOutputCurveMode custom` and `RSOutputCurveCustom 0.3, 0.11, 1.00, 1.00`. Edge Expo is now
`0.72, 0.26`, colour `0,255,0`. Hipfire still has Edge Linear's blue `0,0,255` colour and a copy of Linear's
`<AimLayer>` block; the block does no harm in a source profile (only RS settings are borrowed). Nothing in
`Actions.xml` uses R2.

**Decisions (user, 2026-10-06).**
- **L2 released while R2 is still held:** if R2 has already been held 100 ms or more, switch straight from
  Expo to Hipfire on that report. Do **not** restart the 100 ms (that would mean two curve jumps mid-spray).
- **No release grace:** any R2 release goes back to Linear on that report; the next press waits the full
  delay again. Tap-fire and shotgun pumps stay linear.
- **Lightbar cue on** for the Hipfire layer. The user recolours Edge Hipfire themselves.
- Format: generalise rather than special-case. A profile may hold **several `<AimLayer>` blocks**. Order in
  the file is priority. Each block gets an optional `<Delay>`. The user's existing single block stays
  valid unchanged.

**Selection rule (exact; the tests in 5.2 pin it).** Per device, per report, from the raw `L2`/`R2` (same
place as now, before `cState` is replaced):
1. Each layer tracks its own *held since* time. It is set on the report where the layer's raw trigger goes
   from `<= Threshold` to `> Threshold`, and cleared on the report where it drops back. It is independent
   of the other layers. So R2's timer runs from the R2 press even while L2 is held, which gives the
   "Hipfire at once on L2 release" decision.
2. A layer is *ready* when its trigger is held and `now - heldSince >= Delay`. `Delay 0` means ready on
   the press report (the current behaviour).
3. The active layer is the **first ready layer in file order**; none ready means the base profile.
4. Timers reset whenever a different layer set is published (profile apply, clear, suspend/resume). A
   trigger already held at that moment starts its timer on the first report after the reset. A rebuild
   after saving a source profile may keep or reset timers (either is fine), but must not break the rule.
5. Clock: `Stopwatch` milliseconds, read once per report and only when a layer set is published (the
   no-layer path stays one volatile read + null check). `SetCurveAndDeadzone` already computes
   `filterTimestamp`; reuse one value. Tests need a deterministic clock seam that costs nothing in
   production (e.g. an internal overload taking `nowMs`, or a static test hook checked only when non-null).
6. Threading: the timer state for slot N is only touched by the thread calling `SetCurveAndDeadzone` for
   slot N. That is the input thread for live slots and the readings-preview timer for the editor slot;
   the preview only calls with `profileIndex != inputIndex`. No locks or allocation in the per-report path.

**Other things considered.**
- **Delay vs. the user's own shotgun pull.** A firm shotgun pull can last over 100 ms. If it does,
  Hipfire comes on briefly at the end of the shot. The lightbar cue shows this on hardware; tune `<Delay>`
  (or `<Threshold>`) from that. Hand-editable, so no rebuild needed.
- **Threshold.** The delay counts from the *layer's* raw threshold, not the game's fire point. With
  `Threshold 100` and a slow squeeze, the game may already be firing before the timer starts. A lower
  R2 threshold (e.g. 30–50) is fine because the delay already filters taps. Document it; default stays 100.
- **Curve jump mid-spray.** Switching at 100 ms changes stick output at the same deflection (e.g. ~0.5 →
  ~0.4 at half tilt with `0.3, 0.11`). This is the same kind of jump L2 already causes and the user is happy
  with. Optional blend over N ms is **not** built. Revisit only if it feels like a hitch on hardware.
- **Per-layer source failure.** A missing or broken source turns off only that layer (one warning per
  layer, worded as now, plus the layer's trigger). The other layers keep working.
- **Self-borrow** is skipped per layer, as now.
- **Saving** a base profile from DS4Windows must keep every block, in order. Saving any source profile
  rebuilds only the layers that borrow it.
- **Cap:** at most 4 layers per profile. Extra blocks are ignored with one log warning; the profile
  still loads.
- **Unchanged:** fuzz, rotation, anti-snapback and calibration stay on the base profile; the L2/R2 output
  the game sees is untouched; flick stick is unaffected.

- **5.1 (worker-complex): several layers in the data model, XML and publish/rebuild.**
  - Files: `DS4Control/DTOXml/ProfileDTO.cs` (`AimLayerSettingsDTO`, the `AimLayer` property,
    `ShouldSerialize`, `MapFrom`/`MapTo`), `DS4Control/ScpUtil.cs` (the `aimLayer*` per-device arrays,
    `ResetAimLayer`, the `Global.AimLayer*` statics), `DS4Control/AimLayerStickSettings.cs`
    (`AimLayerRequest`, `AimLayerPreparation`, `AimLayerState` entries/publish/rebuild/warnings),
    `DS4Control/PreparedProfileLoad.cs`, the existing `DS4WindowsTests/AimLayer*Tests.cs`.
  - XML: `[XmlElement("AimLayer")]` on a list gives repeated `<AimLayer>` siblings, so today's single block
    reads as a list of one. New element `<Delay>`: whole milliseconds 0–1000, default 0; a bad or
    out-of-range value falls back to 0 (never fails the load, like the other fields). Save writes each
    non-default block in order and writes `<Delay>` only when it is not 0, so an unchanged single-layer
    profile saves the same as today.
  - Store: replace the single-value per-device arrays with one per-device list of immutable layer configs
    (or an equivalent design). Keep validation/test stores private, as now. Only the live store publishes.
  - Publish: one immutable per-device *layer set* (ordered built layers, each knowing its trigger, threshold,
    delay, lightbar flag and colour) behind one `Volatile` reference. Keep the generation/stale-save/
    suspend/resume behaviour from 3.2 working for every layer.
  - Acceptance:
    - All existing `AimLayer*` tests still pass. Change them only where an API changed, never to weaken
      a behaviour check.
    - New tests: the user's single block (`SampleAimLayerXml`) loads and saves as before. Two blocks (L2 →
      Expo, R2 → Hipfire, `Delay 100`) round-trip in order. `<Delay>` parse/default/bad/out-of-range.
      The 5th block is ignored with one warning. A missing source for layer 2 leaves layer 1 published, and
      the reverse. Saving the Hipfire source rebuilds only the R2 layer. Self-borrow is skipped per layer.
      No `<AimLayer>` still clears the slot.
    - `SetCurveAndDeadzone` may only be changed as needed to compile (e.g. pick the first layer whose
      trigger is held, which gives the same behaviour as today for one layer). Real selection is 5.2.
    - Full suite `TestCategory!=Benchmark`: 0 fail, same 12 skips.
  - Return: design summary (types, where the list lives), files changed, test counts, anything 5.2 must know.
  - **Done 2026-10-06 at `9da1d4b`.** Immutable `AimLayerConfig` list per device (`BackingStore.aimLayers`, max 4,
    replaced whole); published as an immutable `AimLayerSet` (built layers in file order, each with `LayerIndex`).
    Per-layer warnings include the trigger; save-rebuild touches only layers borrowing the saved source. Single-block
    save checked byte-identical to the previous build. 36 new tests; suite 7217 pass, 12 skips, 0 fail.
- **5.2 (worker-complex): runtime selection with delay, and the lightbar.**
  - Files: `DS4Control/Mapping.cs` (`SetCurveAndDeadzone` only), `DS4Control/AimLayerStickSettings.cs`
    (per-device timer state, the active-layer handoff to the lightbar), `DS4Control/DS4LightBar.cs` only if
    the `MainLightbarColor` signature changes, tests.
  - Implement the selection rule above exactly. Replace `SetHeld(bool)` with publishing the **active layer**
    (a `Volatile` reference write, no allocation), so the lightbar shows the active layer's colour if that
    layer has `UseSourceLightbar`. A published set of `null` still wins over a stale active value.
  - Acceptance (deterministic clock; report-by-report sequences):
    - R2 only: `Delay - 1` ms → base; `Delay` ms → Hipfire; release → base on that report; re-press →
      waits the full delay again.
    - L2 at any time → Expo, including while Hipfire is active.
    - R2 held ≥ 100 ms with L2 held, then L2 released → Hipfire **on that report**. R2 pressed while L2 is
      held, L2 released at 30 ms → base until R2 reaches 100 ms, then Hipfire.
    - Threshold boundaries (`== Threshold` not held, `Threshold + 1` held) for both layers.
    - Timers reset on a new publish. A trigger held across a profile apply waits the full delay from the
      first report after it.
    - `Delay 0` single-layer profiles behave exactly as Phase 3 (the existing mapping tests unchanged).
    - No layer: output byte-for-byte identical (existing equivalence tests).
    - Zero allocation over 20,000 warm calls with the two-layer set, cycling through all states.
    - Lightbar follows the active layer: Hipfire colour while Hipfire is active, Expo colour while L2 is
      held, base otherwise; `UseSourceLightbar False` on one layer only affects that layer; custom colour /
      battery gradient unaffected.
    - Mutation check (report it): restarting R2's timer on L2 release, or deciding from processed triggers,
      must fail at least one test.
    - Full suite: 0 fail, same 12 skips.
  - Return: what changed, test names/counts, mutation-check results.
  - **Done 2026-10-06 at `0ece443`.** `AimLayerState.Select` keeps preallocated per-slot held-since timers (by
    position in the set) and a last-seen set reference; any new set reference resets them (`Resume`/re-publish now
    publish a renewed wrapper so reconnects also reset). The active layer is a `Volatile` write read by
    `MainLightbarColor` (null set still wins); `SetHeld` removed. Clock: one Stopwatch read per report, forwarded to an
    internal `SetCurveAndDeadzone(..., long nowMs)` overload. 14 new tests (`AimLayerSelectionTests` 10, lightbar 4);
    0 bytes over 20,000 warm calls. Mutations: restarting R2's timer on L2 release failed 7 tests; deciding from
    processed triggers failed 5. Suite 7231 pass, 12 skips, 0 fail.
- **5.3 (worker-standard): update `docs/aim-layer.md`.**
  - Several blocks, file order = priority (first ready wins), `<Delay>` (row in the table, default 0,
    0–1000 ms, bad → 0), the 4-layer cap, per-layer source warnings, the threshold-vs-game-fire-point note,
    the shotgun-pull tuning note.
  - The user's full example: Edge Linear with the L2 → Edge Expo block first, then R2 → Edge Hipfire,
    `Threshold 100`, `Delay 100`, `UseSourceLightbar True`, and the selection table from this phase.
  - The two-block sample must be loaded by a test (add it next to `SampleAimLayerXml` if 5.1 didn't).
  - **Done 2026-10-06 at `e321d2f`.** Doc samples match `SampleAimLayerXml` / `SampleTwoAimLayersXml`.
- **5.4 (orchestrator):** full suite `TestCategory!=Benchmark` (record counts), Release build, record DLL
  path/size/SHA-256 for 5.5. Push `fast-aim-switching` to `fork`.
  - **Done 2026-10-06.** Full suite `TestCategory!=Benchmark`: **7231 pass, 12 expected skips, 0 fail** (1 m 38 s).
    Release build 0 errors, no new warnings. DLL `DS4Windowsind\Release
et8.0-windows10.0.19041.0\DS4Windows.dll`,
    11,017,728 bytes, SHA-256 `e0d61d4be263fde54495bf18165cc4031c2d7d9fdb2f54c681c307747511087f`.
- **5.5 (the user, on hardware, DLL-swap method from 1.2):**
  1. In DS4Windows, give **Edge Hipfire** its own lightbar colour and save it. Close DS4Windows. Back up
     `Profiles\Edge Linear.xml`.
  2. Add the second `<AimLayer>` block from `docs/aim-layer.md` to Edge Linear, **after** the L2 block.
  3. Check, with the lightbar as the guide: quick shotgun taps stay blue (Linear); holding R2 changes to
     the Hipfire colour after ~0.1 s; L2 at any time shows Expo; ADS + spray, then release L2 while still
     firing, goes straight to Hipfire; letting go of R2 goes back to blue.
  4. In game: movement and shotgun fights feel linear; sprays get the mild curve; ADS as before. If
     deliberate shotgun shots flash the Hipfire colour, raise `<Delay>` (e.g. 150) by hand.

## 5. Decisions
Made 2026-10-05:
- Quieter per-press logging (formerly task A3): **dropped**. Keep the "using Profile" log line; it costs about one line per press and is useful evidence if switching misbehaves.
- Task 3.5: **hand-editing guide only**; editor UI deferred.
- Task 2.3 (prepared-profile cache): **dropped**; 2.2 alone met its target.
- Pushing: **yes**, to the user's fork (`fork` remote, `petemess95/DS4Windows`).

Upstream pull request (asked 2026-10-06): **yes, without the benchmark.** Branch `fast-profile-switch`
(pushed to `fork`) = `origin/main` + one commit `12dae41` (Task 2.2 fix + self-contained
`ProfileSerializerCacheTests`; full suite 7132 pass, 12 skips, 0 fail). `gh` is not installed, so the user
opens the PR from the GitHub compare page with the prefilled title/body. Original question:
- Offer the Phase 2 speed-up to `hbashton/DS4Windows` as a pull request? Recommended: upstream changes this area
  often, and a merged fix avoids redoing it every release. Leave this plan file out of that pull
  request.

## 6. Next session

Phases 1–4 and Tasks 5.1–5.4 are done. Next is **Task 5.5** (the user's hardware test). After it, record the
result under 5.5. Kickoff message:

> Phase 5 of `PLAN-fast-aim-switching.md`: I ran the 5.5 hardware test. Here are the results: <results>.
> Record them and fix anything that failed, as orchestrator.
