using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using DS4Windows.DS4Control;

namespace DS4Windows
{
    /// <summary>
    /// One &lt;AimLayer&gt; block as stored in a profile slot. Immutable, so a
    /// slot's list can be shared with a preparation without copying.
    /// </summary>
    public sealed class AimLayerConfig
    {
        public AimLayerConfig(bool enabled, DS4Controls trigger, byte threshold, int delay,
            string sourceProfile, bool useSourceLightbar,
            AimRecoilConfig recoil = null, AimRotateConfig rotate = null)
        {
            Enabled = enabled;
            Trigger = BackingStore.NormalizeAimLayerTrigger(trigger);
            Threshold = threshold;
            Delay = BackingStore.NormalizeAimLayerDelay(delay);
            SourceProfile = sourceProfile?.Trim() ?? string.Empty;
            UseSourceLightbar = useSourceLightbar;
            Recoil = recoil;
            Rotate = rotate;
        }

        public bool Enabled { get; }
        public DS4Controls Trigger { get; }
        public byte Threshold { get; }
        // Milliseconds the trigger must be held before the layer comes on.
        public int Delay { get; }
        public string SourceProfile { get; }
        public bool UseSourceLightbar { get; }
        // Optional macro elements of this block, raw; null = absent.
        public AimRecoilConfig Recoil { get; }
        public AimRotateConfig Rotate { get; }

        // Unconfigured blocks are not saved.
        public bool IsDefault => !Enabled &&
            Trigger == BackingStore.DEFAULT_AIM_LAYER_TRIGGER &&
            Threshold == BackingStore.DEFAULT_AIM_LAYER_THRESHOLD &&
            Delay == BackingStore.DEFAULT_AIM_LAYER_DELAY &&
            SourceProfile.Length == 0 && !UseSourceLightbar &&
            Recoil == null && Rotate == null;
    }

    /// <summary>
    /// Right-stick settings borrowed from an aim layer's source profile, plus
    /// the base profile's trigger rule for that layer. Immutable after
    /// construction: every reference it holds is a private copy, so the input
    /// thread can read it without locks. Never mutate <see cref="RSModInfo"/>
    /// or <see cref="RSOutBezierCurve"/>.
    /// </summary>
    internal sealed class AimLayerStickSettings
    {
        internal AimLayerStickSettings(AimLayerRequest request, StickDeadZoneInfo rsModInfo,
            double rsSens, bool rsSquareStick, double rsSquareStickRoundness,
            int rsOutCurveMode, BezierCurve rsOutBezierCurve, DS4Color lightbarColor)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(rsModInfo);
            ArgumentNullException.ThrowIfNull(rsOutBezierCurve);
            BaseProfile = request.BaseProfile;
            SourceProfile = request.SourceProfile;
            Trigger = request.Trigger;
            Threshold = request.Threshold;
            Delay = request.Delay;
            LayerIndex = request.LayerIndex;
            UseSourceLightbar = request.UseSourceLightbar;
            Recoil = request.Recoil;
            Rotate = request.Rotate;
            RSModInfo = CopyDeadZoneInfo(rsModInfo);
            RSSens = rsSens;
            RSSquareStick = rsSquareStick;
            RSSquareStickRoundness = rsSquareStickRoundness;
            RSOutCurveMode = rsOutCurveMode;
            RSOutBezierCurve = rsOutBezierCurve.CloneBuilt();
            LightbarColor = lightbarColor;
        }

        // From the base profile's <AimLayer> block.
        internal string BaseProfile { get; }
        internal string SourceProfile { get; }
        internal DS4Controls Trigger { get; }
        internal byte Threshold { get; }
        internal int Delay { get; }
        // Position of the block in the base profile (0 = first). Stable while
        // the base profile stays applied, even when other layers fail or
        // rebuild, so per-layer runtime state can be keyed by it.
        internal int LayerIndex { get; }
        internal bool UseSourceLightbar { get; }
        // The block's own macros (never the source profile's); null = none.
        internal AimMacroRecoil Recoil { get; }
        internal AimMacroRotate Rotate { get; }

        // From the source profile (what GetRSDeadInfo, getRSSens,
        // GetSquareStickInfo and getRsOutCurveMode/rsOutBezierCurveObj return).
        internal StickDeadZoneInfo RSModInfo { get; }
        internal double RSSens { get; }
        internal bool RSSquareStick { get; }
        internal double RSSquareStickRoundness { get; }
        internal int RSOutCurveMode { get; }
        internal BezierCurve RSOutBezierCurve { get; }
        // Source profile's main lightbar colour (m_Led).
        internal DS4Color LightbarColor { get; }

        // Raw (pre dead zone) trigger values; matches the special-action
        // digital trigger rule (value > threshold).
        internal bool IsTriggerHeld(byte l2, byte r2) =>
            (Trigger == DS4Controls.R2 ? r2 : l2) > Threshold;

        internal static AimLayerStickSettings FromStore(AimLayerRequest request,
            BackingStore store, int device)
        {
            SquareStickInfo square = store.squStickInfo[device];
            return new AimLayerStickSettings(request, store.rsModInfo[device],
                store.RSSens[device], square.rsMode, square.rsRoundness,
                store.getRsOutCurveMode(device), store.rsOutBezierCurveObj[device],
                store.lightbarSettingInfo[device].ds4winSettings.m_Led);
        }

        private static StickDeadZoneInfo CopyDeadZoneInfo(StickDeadZoneInfo source)
        {
            return new StickDeadZoneInfo
            {
                deadZone = source.deadZone,
                antiDeadZone = source.antiDeadZone,
                maxZone = source.maxZone,
                maxOutput = source.maxOutput,
                maxOutputForce = source.maxOutputForce,
                fuzz = source.fuzz,
                verticalScale = source.verticalScale,
                deadzoneType = source.deadzoneType,
                outerBindDeadZone = source.outerBindDeadZone,
                outerBindInvert = source.outerBindInvert,
                xAxisDeadInfo = CopyAxis(source.xAxisDeadInfo),
                yAxisDeadInfo = CopyAxis(source.yAxisDeadInfo),
            };
        }

        private static StickDeadZoneInfo.AxisDeadZoneInfo CopyAxis(
            StickDeadZoneInfo.AxisDeadZoneInfo source) => new()
            {
                deadZone = source.deadZone,
                antiDeadZone = source.antiDeadZone,
                maxZone = source.maxZone,
                maxOutput = source.maxOutput,
            };
    }

    /// <summary>
    /// The layers a device uses right now: every layer that built, in file
    /// (priority) order, never empty and at most MAX_AIM_LAYERS. Immutable
    /// and published as one reference, so the input thread sees one
    /// consistent set per report. Each publish uses a new reference, which is
    /// what restarts the hold timers.
    /// </summary>
    internal sealed class AimLayerSet
    {
        private readonly AimLayerStickSettings[] layers;

        private AimLayerSet(AimLayerStickSettings[] layers, AimMacroSettings macros)
        {
            this.layers = layers;
            Macros = macros;
        }

        internal int Count => layers.Length;

        // The base profile's <AimMacros>; null = none (macros never arm).
        internal AimMacroSettings Macros { get; }

        internal AimLayerStickSettings this[int index] => layers[index];

        // Null when nothing built, so "no layer" stays a null check.
        internal static AimLayerSet From(IReadOnlyList<AimLayerStickSettings> built,
            AimMacroSettings macros = null)
        {
            int count = 0;
            for (int index = 0; index < built.Count; index++)
            {
                if (built[index] != null)
                    count++;
            }
            if (count == 0)
                return null;
            // The per-slot hold timers have one entry per layer.
            if (count > BackingStore.MAX_AIM_LAYERS)
                throw new ArgumentException(
                    $"At most {BackingStore.MAX_AIM_LAYERS} layers are allowed.", nameof(built));

            var layers = new AimLayerStickSettings[count];
            count = 0;
            for (int index = 0; index < built.Count; index++)
            {
                if (built[index] != null)
                    layers[count++] = built[index];
            }
            return new AimLayerSet(layers, macros);
        }

        // Same layers under a new reference, so publishing it restarts the
        // hold timers even when nothing was rebuilt (resume, re-apply).
        internal AimLayerSet Renewed() => new(layers, Macros);

        // First layer (file order) whose raw trigger is held, ignoring
        // Delay; null = none. Lock- and allocation-free.
        internal AimLayerStickSettings FirstHeld(byte l2, byte r2)
        {
            AimLayerStickSettings[] items = layers;
            for (int index = 0; index < items.Length; index++)
            {
                if (items[index].IsTriggerHeld(l2, r2))
                    return items[index];
            }
            return null;
        }
    }

    /// <summary>
    /// What one base-profile block asks to borrow. Kept even when the source
    /// cannot be built, so saving the source later can still bring the layer
    /// up.
    /// </summary>
    internal sealed class AimLayerRequest
    {
        internal AimLayerRequest(string baseProfile, string sourceProfile,
            DS4Controls trigger, byte threshold, bool useSourceLightbar,
            int delay = BackingStore.DEFAULT_AIM_LAYER_DELAY, int layerIndex = 0,
            AimMacroRecoil recoil = null, AimMacroRotate rotate = null)
        {
            BaseProfile = baseProfile;
            SourceProfile = sourceProfile;
            Trigger = trigger;
            Threshold = threshold;
            UseSourceLightbar = useSourceLightbar;
            Delay = delay;
            LayerIndex = layerIndex;
            Recoil = recoil;
            Rotate = rotate;
        }

        internal string BaseProfile { get; }
        internal string SourceProfile { get; }
        internal DS4Controls Trigger { get; }
        internal byte Threshold { get; }
        internal bool UseSourceLightbar { get; }
        internal int Delay { get; }
        internal int LayerIndex { get; }
        // Parsed when the base is prepared, so a source rebuild keeps them.
        internal AimMacroRecoil Recoil { get; }
        internal AimMacroRotate Rotate { get; }

        internal bool Borrows(string profileName) =>
            string.Equals(SourceProfile, profileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Result of preparing a base profile's aim layers: one request per
    /// enabled block that does not borrow from the base itself, and what
    /// each built (null = that layer failed).
    /// </summary>
    internal sealed class AimLayerPreparation
    {
        private readonly AimLayerRequest[] requests;
        private readonly AimLayerStickSettings[] built;

        internal AimLayerPreparation(IReadOnlyList<AimLayerRequest> requests,
            IReadOnlyList<AimLayerStickSettings> built, long saveSequence,
            AimMacroSettings macros = null)
        {
            ArgumentNullException.ThrowIfNull(requests);
            ArgumentNullException.ThrowIfNull(built);
            if (requests.Count == 0 || requests.Count != built.Count)
                throw new ArgumentException("One built entry per request is required.", nameof(built));
            this.requests = new AimLayerRequest[requests.Count];
            this.built = new AimLayerStickSettings[built.Count];
            for (int index = 0; index < this.requests.Length; index++)
            {
                this.requests[index] = requests[index] ??
                    throw new ArgumentException("Requests cannot be null.", nameof(requests));
                this.built[index] = built[index];
            }
            SaveSequence = saveSequence;
            Macros = macros;
            Set = AimLayerSet.From(this.built, macros);
        }

        // Single layer.
        internal AimLayerPreparation(AimLayerRequest request,
            AimLayerStickSettings settings, long saveSequence)
            : this(new[] { request }, new[] { settings }, saveSequence)
        {
        }

        internal IReadOnlyList<AimLayerRequest> Requests => requests;
        internal IReadOnlyList<AimLayerStickSettings> Built => built;
        // What Publish makes current; null when no layer built.
        internal AimLayerSet Set { get; }
        internal long SaveSequence { get; }
        internal AimMacroSettings Macros { get; }

        internal AimLayerRequest[] CopyRequests() => (AimLayerRequest[])requests.Clone();
        internal AimLayerStickSettings[] CopyBuilt() => (AimLayerStickSettings[])built.Clone();
    }

    /// <summary>
    /// Per-device published aim layers. The input thread only calls
    /// <see cref="Current"/>; everything else runs on profile load/save paths.
    /// </summary>
    internal static class AimLayerState
    {
        private sealed class Entry
        {
            // Parallel arrays, one slot per request; replaced, never shared
            // with a published set.
            internal AimLayerRequest[] Requests;
            internal AimLayerStickSettings[] Built;
            internal AimLayerSet Set;
            // Rebuilt sets carry the same profile macros.
            internal AimMacroSettings Macros;
            internal long Generation;
            internal bool Suspended;
        }

        private const int SaveRebuildAttempts = 4;

        // Test seam: runs between a save-triggered build and its publish.
        internal static Action<int> RebuildBuiltForTests;

        private static readonly AimLayerSet[] published =
            new AimLayerSet[Global.TEST_PROFILE_ITEM_COUNT];
        private static readonly Entry[] entries = CreateEntries();
        // Last warning per slot and layer, so repeated loads of a broken setup
        // (temp switches back to the base profile) log once, not every time.
        private static readonly string[][] lastWarning = CreateWarnings();
        // Last "too many blocks" warning per slot, same reason.
        private static readonly string[] lastCapWarning =
            new string[Global.TEST_PROFILE_ITEM_COUNT];
        // Last macro warnings, same reason: <AimMacros> (or its Toggle) per
        // slot, and <Recoil>/<Rotate> per slot and layer.
        private static readonly string[] lastMacrosWarning =
            new string[Global.TEST_PROFILE_ITEM_COUNT];
        private static readonly string[][] lastRecoilWarning = CreateWarnings();
        private static readonly string[][] lastRotateWarning = CreateWarnings();
        // Active layer as last decided by SetCurveAndDeadzone (null = base),
        // so the lightbar follows the exact state the stick used (joined and
        // copied input states included) without re-reading triggers.
        private static readonly AimLayerStickSettings[] active =
            new AimLayerStickSettings[Global.TEST_PROFILE_ITEM_COUNT];
        // Hold timers, per slot and per position in the set: the report time
        // (ms) the layer's trigger went above its threshold, or NotHeld.
        // Only the thread calling SetCurveAndDeadzone for that slot touches
        // these and lastSeen (input thread for live slots, readings preview
        // for the editor slot), so they need no locks.
        private const long NotHeld = long.MinValue;
        private static readonly long[][] heldSince = CreateHeldSince();
        // The set the timers belong to; a different published set resets them.
        private static readonly AimLayerSet[] lastSeen =
            new AimLayerSet[Global.TEST_PROFILE_ITEM_COUNT];
        private static long saveSequence;

        private static Entry[] CreateEntries()
        {
            var result = new Entry[Global.TEST_PROFILE_ITEM_COUNT];
            for (int index = 0; index < result.Length; index++)
                result[index] = new Entry();
            return result;
        }

        private static string[][] CreateWarnings()
        {
            var result = new string[Global.TEST_PROFILE_ITEM_COUNT][];
            for (int index = 0; index < result.Length; index++)
                result[index] = new string[BackingStore.MAX_AIM_LAYERS];
            return result;
        }

        private static long[][] CreateHeldSince()
        {
            var result = new long[Global.TEST_PROFILE_ITEM_COUNT][];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = new long[BackingStore.MAX_AIM_LAYERS];
                Array.Fill(result[index], NotHeld);
            }
            return result;
        }

        /// <summary>Null = no layer. Lock- and allocation-free.</summary>
        internal static AimLayerSet Current(int device) =>
            Volatile.Read(ref published[device]);

        /// <summary>
        /// Input thread, once per report while a set is published
        /// (<paramref name="layers"/> = <see cref="Current"/>, not null).
        /// Updates every layer's hold timer from the raw triggers and returns
        /// the active layer: the first in file order whose trigger is held and
        /// has been for at least its Delay; null = base profile. Lock- and
        /// allocation-free.
        /// </summary>
        internal static AimLayerStickSettings Select(int device, AimLayerSet layers,
            byte l2, byte r2, long nowMs)
        {
            long[] since = heldSince[device];
            if (!ReferenceEquals(layers, lastSeen[device]))
            {
                // New set (apply, clear, suspend/resume, rebuild): every timer
                // starts again; a trigger already held counts from this report.
                for (int index = 0; index < since.Length; index++)
                    since[index] = NotHeld;
                lastSeen[device] = layers;
            }

            AimLayerStickSettings result = null;
            int count = layers.Count;
            // No early exit: a lower-priority layer's timer keeps running
            // while a higher one is active (R2 held under L2).
            for (int index = 0; index < count; index++)
            {
                AimLayerStickSettings layer = layers[index];
                if (!layer.IsTriggerHeld(l2, r2))
                {
                    since[index] = NotHeld;
                    continue;
                }
                if (since[index] == NotHeld)
                    since[index] = nowMs;
                if (result == null && nowMs - since[index] >= layer.Delay)
                    result = layer;
            }

            Volatile.Write(ref active[device], result);
            return result;
        }

        // Last active layer; null = base. Only meaningful while a set is
        // published (see MainLightbarColor).
        internal static AimLayerStickSettings Active(int device) =>
            Volatile.Read(ref active[device]);

        // Test seam: forget the last decision and the timers. Not safe while
        // SetCurveAndDeadzone runs for the same slot.
        internal static void ResetSelectionForTests(int device)
        {
            Volatile.Write(ref active[device], null);
            lastSeen[device] = null;
            Array.Fill(heldSince[device], NotHeld);
        }

        // Lightbar: the colour to show where the plain main colour would be.
        // Only swaps when a set is published and its active layer wants its
        // source colour; otherwise returns baseColor. A null set wins over a
        // stale active layer.
        internal static DS4Color MainLightbarColor(int device, DS4Color baseColor)
        {
            if (Current(device) == null)
                return baseColor;
            AimLayerStickSettings layer = Active(device);
            return layer != null && layer.UseSourceLightbar ? layer.LightbarColor : baseColor;
        }

        internal static long ReadSaveSequence() => Interlocked.Read(ref saveSequence);

        // Called on the cold prepare path with the base profile mapped into its
        // private validation store. blockCount is how many <AimLayer> blocks
        // the file had (the store keeps at most MAX_AIM_LAYERS). Never throws:
        // an aim layer must not stop the base profile from loading.
        internal static AimLayerPreparation Prepare(BackingStore baseStore, int device,
            string basePath, long sequence, int blockCount)
        {
            string baseName = Path.GetFileNameWithoutExtension(basePath) ?? string.Empty;
            IReadOnlyList<AimLayerConfig> configs = baseStore.aimLayers[device];
            WarnIgnoredBlocks(device, baseName, blockCount - configs.Count);
            AimMacroSettings macros = PrepareMacros(baseStore.aimMacros[device], device, baseName);

            List<AimLayerRequest> requests = null;
            for (int index = 0; index < configs.Count; index++)
            {
                AimLayerConfig config = configs[index];
                if (!config.Enabled)
                    continue;
                // Borrowing from itself would change nothing; skip this layer only.
                if (string.Equals(config.SourceProfile, baseName, StringComparison.OrdinalIgnoreCase))
                    continue;
                requests ??= new List<AimLayerRequest>(configs.Count);
                PrepareLayerMacros(config, device, baseName, index,
                    out AimMacroRecoil recoil, out AimMacroRotate rotate);
                requests.Add(new AimLayerRequest(baseName, config.SourceProfile,
                    config.Trigger, config.Threshold, config.UseSourceLightbar,
                    config.Delay, index, recoil, rotate));
            }
            if (requests == null)
                return null;

            var built = new AimLayerStickSettings[requests.Count];
            for (int index = 0; index < built.Length; index++)
                built[index] = Build(requests[index], device);
            return new AimLayerPreparation(requests, built, sequence, macros);
        }

        // A bad element is dropped with one warning; never throws.
        private static AimMacroSettings PrepareMacros(AimMacrosConfig config, int device,
            string baseName)
        {
            string message = null;
            if (!AimMacroParser.TryBuildSettings(config, baseName, out AimMacroSettings macros,
                    out string error, out string toggleError))
                message = $"Aim macros of profile \"{baseName}\": <AimMacros> ignored: {error}";
            else if (toggleError != null)
                message = $"Aim macros of profile \"{baseName}\": no valid toggle, " +
                    $"so they can never arm: {toggleError}";
            WarnOnce(lastMacrosWarning, device, message);
            return macros;
        }

        private static void PrepareLayerMacros(AimLayerConfig config, int device,
            string baseName, int layerIndex, out AimMacroRecoil recoil, out AimMacroRotate rotate)
        {
            string prefix = $"Aim macros of profile \"{baseName}\", aim layer {layerIndex + 1} " +
                $"({config.Trigger}): ";
            WarnOnce(lastRecoilWarning[device], layerIndex,
                AimMacroParser.TryBuildRecoil(config.Recoil, out recoil, out string error) ?
                    null : prefix + "<Recoil> ignored: " + error);
            WarnOnce(lastRotateWarning[device], layerIndex,
                AimMacroParser.TryBuildRotate(config.Rotate, out rotate, out error) ?
                    null : prefix + "<Rotate> ignored: " + error);
        }

        // null message = fine now; the next failure logs again.
        private static void WarnOnce(string[] last, int index, string message)
        {
            if (Interlocked.Exchange(ref last[index], message) != message && message != null)
                AppLogger.LogToGui(message, true);
        }

        private static void WarnIgnoredBlocks(int device, string baseName, int ignored)
        {
            if (ignored <= 0)
            {
                Volatile.Write(ref lastCapWarning[device], null);
                return;
            }

            string message = $"Aim layer of profile \"{baseName}\": {ignored} extra " +
                $"<AimLayer> block(s) ignored; at most {BackingStore.MAX_AIM_LAYERS} are used";
            if (Interlocked.Exchange(ref lastCapWarning[device], message) != message)
                AppLogger.LogToGui(message, true);
        }

        // One layer. A failure turns off only this layer.
        internal static AimLayerStickSettings Build(AimLayerRequest request, int device)
        {
            // LayerIndex is below MAX_AIM_LAYERS for prepared requests; clamp
            // for hand-built ones.
            int warningSlot = (uint)request.LayerIndex < BackingStore.MAX_AIM_LAYERS ?
                request.LayerIndex : 0;
            string failure;
            if (request.SourceProfile.Length == 0)
                failure = "no source profile is set";
            else if (request.SourceProfile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                failure = "the source profile name is not a valid file name";
            else
            {
                string path = Path.Combine(Global.appdatapath, "Profiles",
                    $"{request.SourceProfile}{Global.XML_EXTENSION}");
                try
                {
                    if (PreparedProfileLoad.TryPrepareSource(path, device,
                            out BackingStore source, out ProfilePreparationFailure kind,
                            out string error))
                    {
                        Volatile.Write(ref lastWarning[device][warningSlot], null);
                        return AimLayerStickSettings.FromStore(request, source, device);
                    }
                    failure = kind == ProfilePreparationFailure.Missing ?
                        $"{path} was not found" : $"{path} could not be loaded ({kind}). {error}";
                }
                catch (Exception ex)
                {
                    failure = $"{path} could not be loaded. {ex.Message}";
                }
            }

            string message = $"Aim layer of profile \"{request.BaseProfile}\" " +
                $"({request.Trigger}) is off: " +
                $"source profile \"{request.SourceProfile}\": {failure}";
            if (Interlocked.Exchange(ref lastWarning[device][warningSlot], message) != message)
                AppLogger.LogToGui(message, true);
            return null;
        }

        // Applying a profile: publish what its preparation built. Runs inside
        // the report pause, so it only swaps references.
        internal static void Publish(int device, AimLayerPreparation preparation)
        {
            bool stale;
            lock (entries[device])
            {
                Entry entry = entries[device];
                entry.Requests = preparation?.CopyRequests();
                entry.Built = preparation?.CopyBuilt();
                // New reference even for a re-applied preparation, so the
                // hold timers restart.
                entry.Set = preparation?.Set?.Renewed();
                entry.Macros = preparation?.Macros;
                entry.Suspended = false;
                entry.Generation++;
                Volatile.Write(ref active[device], null);
                Volatile.Write(ref published[device], entry.Set);
                stale = entry.Requests != null &&
                    preparation.SaveSequence != ReadSaveSequence();
            }

            // A profile was saved after this one was prepared; it may have been
            // a source. Re-read every layer off the pause (generation-checked).
            if (stale)
                ThreadPool.QueueUserWorkItem(static state =>
                    RebuildIfBorrowing((int)state, null), device);
        }

        internal static void Clear(int device)
        {
            lock (entries[device])
            {
                Entry entry = entries[device];
                entry.Requests = null;
                entry.Built = null;
                entry.Set = null;
                entry.Macros = null;
                entry.Suspended = false;
                entry.Generation++;
                Volatile.Write(ref active[device], null);
                Volatile.Write(ref published[device], null);
            }
        }

        // Controller removed: stop publishing, but keep the requests so a
        // reconnect that keeps the slot's profile (temp/auto profile, Joy-Con
        // handoff) can resume without reloading it.
        internal static void Suspend(int device)
        {
            lock (entries[device])
            {
                entries[device].Suspended = true;
                Volatile.Write(ref active[device], null);
                Volatile.Write(ref published[device], null);
            }
        }

        internal static void Resume(int device)
        {
            lock (entries[device])
            {
                Entry entry = entries[device];
                entry.Suspended = false;
                // Same layers, new reference: the hold timers restart.
                entry.Set = entry.Set?.Renewed();
                Volatile.Write(ref active[device], null);
                Volatile.Write(ref published[device], entry.Set);
            }
        }

        // SaveProfileNew wrote profileName. Rebuild the layers borrowing it, in
        // every slot. Cheap when nothing borrows it; ~1 ms per borrowing layer.
        internal static void OnProfileSaved(string profileName)
        {
            Interlocked.Increment(ref saveSequence);
            for (int device = 0; device < entries.Length; device++)
                RebuildIfBorrowing(device, profileName);
        }

        // profileName null = rebuild every layer of the slot. Layers that do
        // not borrow profileName keep their built object.
        private static void RebuildIfBorrowing(int device, string profileName)
        {
            for (int attempt = 0; attempt < SaveRebuildAttempts; attempt++)
            {
                AimLayerRequest[] requests;
                long generation;
                lock (entries[device])
                {
                    requests = entries[device].Requests;
                    generation = entries[device].Generation;
                }
                if (requests == null)
                    return;

                AimLayerStickSettings[] rebuilt = null;
                bool[] changed = null;
                for (int index = 0; index < requests.Length; index++)
                {
                    if (profileName != null && !requests[index].Borrows(profileName))
                        continue;
                    rebuilt ??= new AimLayerStickSettings[requests.Length];
                    changed ??= new bool[requests.Length];
                    rebuilt[index] = Build(requests[index], device);
                    changed[index] = true;
                }
                if (rebuilt == null)
                    return;

                RebuildBuiltForTests?.Invoke(device);
                lock (entries[device])
                {
                    Entry entry = entries[device];
                    // A newer apply or rebuild won; retry so the last file
                    // write is what ends up published.
                    if (entry.Generation != generation)
                        continue;
                    var built = (AimLayerStickSettings[])entry.Built.Clone();
                    for (int index = 0; index < built.Length; index++)
                    {
                        if (changed[index])
                            built[index] = rebuilt[index];
                    }
                    entry.Built = built;
                    entry.Set = AimLayerSet.From(built, entry.Macros);
                    entry.Generation++;
                    if (!entry.Suspended)
                        Volatile.Write(ref published[device], entry.Set);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Profile-level &lt;AimMacros&gt; exactly as written in the file (null =
    /// child absent). Kept raw so a hand-edited block saves as it was; parsed
    /// and checked by <see cref="AimMacroParser"/> when the profile is prepared.
    /// </summary>
    public sealed class AimMacrosConfig
    {
        public AimMacrosConfig(string toggle, string gameDeadZone,
            string gameDeadZoneShape, string armedColor)
        {
            Toggle = toggle;
            GameDeadZone = gameDeadZone;
            GameDeadZoneShape = gameDeadZoneShape;
            ArmedColor = armedColor;
        }

        public string Toggle { get; }
        public string GameDeadZone { get; }
        public string GameDeadZoneShape { get; }
        public string ArmedColor { get; }
    }

    /// <summary>&lt;Recoil&gt; inside an &lt;AimLayer&gt; block, raw (see <see cref="AimMacrosConfig"/>).</summary>
    public sealed class AimRecoilConfig
    {
        public AimRecoilConfig(string fireThreshold, string delay, string ramp,
            string pullY, string driftX, string pattern)
        {
            FireThreshold = fireThreshold;
            Delay = delay;
            Ramp = ramp;
            PullY = pullY;
            DriftX = driftX;
            Pattern = pattern;
        }

        public string FireThreshold { get; }
        public string Delay { get; }
        public string Ramp { get; }
        public string PullY { get; }
        public string DriftX { get; }
        public string Pattern { get; }
    }

    /// <summary>&lt;Rotate&gt; inside an &lt;AimLayer&gt; block, raw (see <see cref="AimMacrosConfig"/>).</summary>
    public sealed class AimRotateConfig
    {
        public AimRotateConfig(string radius, string radiusY, string period,
            string direction, string whenFiring, string fadeAbove)
        {
            Radius = radius;
            RadiusY = radiusY;
            Period = period;
            Direction = direction;
            WhenFiring = whenFiring;
            FadeAbove = fadeAbove;
        }

        public string Radius { get; }
        public string RadiusY { get; }
        public string Period { get; }
        public string Direction { get; }
        public string WhenFiring { get; }
        public string FadeAbove { get; }
    }

    internal enum AimMacroDeadZoneShape { Radial, Axial }

    internal enum AimMacroWhenFiring { Any, FiringOnly, NotFiring }

    // DS4State bool buttons a Toggle may name (L2/R2 are analog, so not here).
    internal enum AimMacroButton
    {
        Square, Triangle, Circle, Cross, DpadUp, DpadDown, DpadLeft, DpadRight,
        L1, L3, R1, R3, Share, Options, PS, Mute, TouchButton, Capture,
        SideL, SideR, FnL, FnR, BLP, BRP,
    }

    /// <summary>
    /// Built profile-level macro settings. Immutable; published on the
    /// <see cref="AimLayerSet"/> so the input thread reads it with the layers.
    /// </summary>
    internal sealed class AimMacroSettings
    {
        private readonly AimMacroButton[] toggle;

        internal AimMacroSettings(string profileName, AimMacroButton[] toggle,
            double gameDeadZone, AimMacroDeadZoneShape shape, DS4Color? armedColor)
        {
            ProfileName = profileName ?? string.Empty;
            this.toggle = toggle ?? Array.Empty<AimMacroButton>();
            GameDeadZone = gameDeadZone;
            GameDeadZoneShape = shape;
            HasArmedColor = armedColor.HasValue;
            ArmedColor = armedColor.GetValueOrDefault();
        }

        // For the arm/disarm log line.
        internal string ProfileName { get; }
        // Empty = Toggle missing or bad: the macros can never arm.
        internal ReadOnlySpan<AimMacroButton> Toggle => toggle;
        internal bool HasToggle => toggle.Length > 0;
        // Fraction 0..0.9 of full deflection (the XML is percent).
        internal double GameDeadZone { get; }
        internal AimMacroDeadZoneShape GameDeadZoneShape { get; }
        internal bool HasArmedColor { get; }
        internal DS4Color ArmedColor { get; }

        // All toggle buttons down in this state; false without a toggle.
        // Lock- and allocation-free.
        internal bool IsToggleDown(DS4State state)
        {
            AimMacroButton[] buttons = toggle;
            if (buttons.Length == 0)
                return false;
            for (int index = 0; index < buttons.Length; index++)
            {
                if (!AimMacroParser.IsDown(state, buttons[index]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Built &lt;Recoil&gt; of one layer. Fractions are of the game's live
    /// stick range (XML percent / 100); +Y = down.
    /// </summary>
    internal sealed class AimMacroRecoil
    {
        private readonly int[] patternTimesMs;
        private readonly double[] patternPullY;

        internal AimMacroRecoil(byte fireThreshold, int delayMs, int rampMs,
            double pullY, double driftX, int[] patternTimesMs, double[] patternPullY)
        {
            FireThreshold = fireThreshold;
            DelayMs = delayMs;
            RampMs = rampMs;
            PullY = pullY;
            DriftX = driftX;
            this.patternTimesMs = patternTimesMs ?? Array.Empty<int>();
            this.patternPullY = patternPullY ?? Array.Empty<double>();
        }

        // Raw R2 above this = firing.
        internal byte FireThreshold { get; }
        internal int DelayMs { get; }
        internal int RampMs { get; }
        internal double PullY { get; }
        internal double DriftX { get; }
        // Pattern points (ms since firing started, strictly ascending; PullY
        // fraction). Empty = use PullY. When set it overrides PullY.
        internal bool HasPattern => patternTimesMs.Length > 0;
        internal ReadOnlySpan<int> PatternTimesMs => patternTimesMs;
        internal ReadOnlySpan<double> PatternPullY => patternPullY;
    }

    /// <summary>Built &lt;Rotate&gt; of one layer; radii are game-range fractions.</summary>
    internal sealed class AimMacroRotate
    {
        internal AimMacroRotate(double radius, double radiusY, int periodMs,
            bool clockwise, AimMacroWhenFiring whenFiring, double fadeAbove)
        {
            Radius = radius;
            RadiusY = radiusY;
            PeriodMs = periodMs;
            Clockwise = clockwise;
            WhenFiring = whenFiring;
            FadeAbove = fadeAbove;
        }

        internal double Radius { get; }
        internal double RadiusY { get; }
        internal int PeriodMs { get; }
        internal bool Clockwise { get; }
        internal AimMacroWhenFiring WhenFiring { get; }
        // Fraction of the user's own stick deflection; 0 = never fade.
        internal double FadeAbove { get; }
    }

    /// <summary>
    /// Cold-path parsing of the hand-edited macro XML. Each TryBuild* returns
    /// false with a reason when any value is bad or out of range; the caller
    /// drops that element and logs the reason once.
    /// </summary>
    internal static class AimMacroParser
    {
        internal const int MaxToggleButtons = 4;
        internal const int MaxPatternPoints = 16;
        // Also the firing rule for Rotate.WhenFiring when the layer has no
        // <Recoil> (plan: "else R2 > 30").
        internal const byte DefaultFireThreshold = 30;
        internal const int DefaultRotatePeriodMs = 60;

        private static readonly Dictionary<string, AimMacroButton> ButtonNames = CreateButtonNames();

        private static Dictionary<string, AimMacroButton> CreateButtonNames()
        {
            var result = new Dictionary<string, AimMacroButton>(StringComparer.OrdinalIgnoreCase);
            foreach (AimMacroButton button in Enum.GetValues<AimMacroButton>())
                result[button.ToString()] = button;
            // The DS4State field is TouchButton; "Touch" reads better in XML.
            result["Touch"] = AimMacroButton.TouchButton;
            return result;
        }

        internal static bool IsDown(DS4State s, AimMacroButton button) => button switch
        {
            AimMacroButton.Square => s.Square,
            AimMacroButton.Triangle => s.Triangle,
            AimMacroButton.Circle => s.Circle,
            AimMacroButton.Cross => s.Cross,
            AimMacroButton.DpadUp => s.DpadUp,
            AimMacroButton.DpadDown => s.DpadDown,
            AimMacroButton.DpadLeft => s.DpadLeft,
            AimMacroButton.DpadRight => s.DpadRight,
            AimMacroButton.L1 => s.L1,
            AimMacroButton.L3 => s.L3,
            AimMacroButton.R1 => s.R1,
            AimMacroButton.R3 => s.R3,
            AimMacroButton.Share => s.Share,
            AimMacroButton.Options => s.Options,
            AimMacroButton.PS => s.PS,
            AimMacroButton.Mute => s.Mute,
            AimMacroButton.TouchButton => s.TouchButton,
            AimMacroButton.Capture => s.Capture,
            AimMacroButton.SideL => s.SideL,
            AimMacroButton.SideR => s.SideR,
            AimMacroButton.FnL => s.FnL,
            AimMacroButton.FnR => s.FnR,
            AimMacroButton.BLP => s.BLP,
            AimMacroButton.BRP => s.BRP,
            _ => false,
        };

        // "FnL+FnR": 1-4 distinct names joined by '+', case-insensitive.
        internal static bool TryParseToggle(string text, out AimMacroButton[] buttons,
            out string error)
        {
            buttons = Array.Empty<AimMacroButton>();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "<Toggle> is missing or empty";
                return false;
            }
            string[] parts = text.Split('+');
            if (parts.Length > MaxToggleButtons)
            {
                error = $"<Toggle> \"{text.Trim()}\" names {parts.Length} buttons; at most {MaxToggleButtons}";
                return false;
            }
            var result = new AimMacroButton[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                string name = parts[index].Trim();
                if (!ButtonNames.TryGetValue(name, out AimMacroButton button))
                {
                    error = $"<Toggle> \"{text.Trim()}\": unknown button \"{name}\"";
                    return false;
                }
                if (Array.IndexOf(result, button, 0, index) >= 0)
                {
                    error = $"<Toggle> \"{text.Trim()}\": \"{name}\" is repeated";
                    return false;
                }
                result[index] = button;
            }
            buttons = result;
            error = null;
            return true;
        }

        // Null config = no <AimMacros>: settings null, no error. A bad
        // GameDeadZone/shape/colour drops the whole element; a bad Toggle
        // keeps it with no toggle (toggleError set) so it can never arm.
        internal static bool TryBuildSettings(AimMacrosConfig config, string profileName,
            out AimMacroSettings settings, out string error, out string toggleError)
        {
            settings = null;
            toggleError = null;
            if (config == null)
            {
                error = null;
                return true;
            }
            if (!TryPercent(config.GameDeadZone, "GameDeadZone", 0, 90, 0, out double deadZone, out error))
                return false;
            AimMacroDeadZoneShape shape = AimMacroDeadZoneShape.Radial;
            if (config.GameDeadZoneShape != null &&
                !TryName(config.GameDeadZoneShape, "GameDeadZoneShape", out shape, out error))
                return false;
            DS4Color? armed = null;
            if (config.ArmedColor != null)
            {
                if (!TryColor(config.ArmedColor, out DS4Color color))
                {
                    error = $"<ArmedColor> \"{config.ArmedColor.Trim()}\" is not R,G,B (0-255 each)";
                    return false;
                }
                armed = color;
            }
            TryParseToggle(config.Toggle, out AimMacroButton[] toggle, out toggleError);
            settings = new AimMacroSettings(profileName, toggle, deadZone, shape, armed);
            return true;
        }

        internal static bool TryBuildRecoil(AimRecoilConfig config,
            out AimMacroRecoil recoil, out string error)
        {
            recoil = null;
            if (config == null)
            {
                error = null;
                return true;
            }
            if (!TryInt(config.FireThreshold, "FireThreshold", 0, 255, DefaultFireThreshold, out int threshold, out error) ||
                !TryInt(config.Delay, "Delay", 0, 2000, 0, out int delay, out error) ||
                !TryInt(config.Ramp, "Ramp", 0, 1000, 0, out int ramp, out error) ||
                !TryPercent(config.PullY, "PullY", 0, 50, 0, out double pullY, out error) ||
                !TryPercent(config.DriftX, "DriftX", -50, 50, 0, out double driftX, out error) ||
                !TryPattern(config.Pattern, out int[] times, out double[] pulls, out error))
                return false;
            recoil = new AimMacroRecoil((byte)threshold, delay, ramp, pullY, driftX, times, pulls);
            return true;
        }

        internal static bool TryBuildRotate(AimRotateConfig config,
            out AimMacroRotate rotate, out string error)
        {
            rotate = null;
            if (config == null)
            {
                error = null;
                return true;
            }
            // Required: a circle without a radius does nothing.
            if (config.Radius == null)
            {
                error = "<Radius> is missing";
                return false;
            }
            if (!TryPercent(config.Radius, "Radius", 0, 50, 0, out double radius, out error) ||
                !TryPercent(config.RadiusY, "RadiusY", 0, 50, radius * 100, out double radiusY, out error) ||
                !TryInt(config.Period, "Period", 10, 1000, DefaultRotatePeriodMs, out int period, out error) ||
                !TryPercent(config.FadeAbove, "FadeAbove", 0, 100, 0, out double fadeAbove, out error))
                return false;
            bool clockwise = true;
            if (config.Direction != null)
            {
                string direction = config.Direction.Trim();
                if (string.Equals(direction, "CCW", StringComparison.OrdinalIgnoreCase))
                    clockwise = false;
                else if (!string.Equals(direction, "CW", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"<Direction> \"{direction}\" is not CW or CCW";
                    return false;
                }
            }
            AimMacroWhenFiring whenFiring = AimMacroWhenFiring.Any;
            if (config.WhenFiring != null &&
                !TryName(config.WhenFiring, "WhenFiring", out whenFiring, out error))
                return false;
            rotate = new AimMacroRotate(radius, radiusY, period, clockwise, whenFiring, fadeAbove);
            return true;
        }

        // Absent (null) = fallback; present must be a whole number in range.
        private static bool TryInt(string text, string name, int min, int max, int fallback,
            out int value, out string error)
        {
            error = null;
            value = fallback;
            if (text == null)
                return true;
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                value >= min && value <= max)
                return true;
            error = $"<{name}> \"{text.Trim()}\" is not a whole number from {min} to {max}";
            return false;
        }

        // Percent in the XML, fraction (value / 100) out. Fallback is percent.
        private static bool TryPercent(string text, string name, double min, double max,
            double fallback, out double fraction, out string error)
        {
            error = null;
            fraction = fallback / 100.0;
            if (text == null)
                return true;
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double percent) && double.IsFinite(percent) &&
                percent >= min && percent <= max)
            {
                fraction = percent / 100.0;
                return true;
            }
            error = $"<{name}> \"{text.Trim()}\" is not a number from {min} to {max}";
            return false;
        }

        private static bool TryName<T>(string text, string name, out T value, out string error)
            where T : struct, Enum
        {
            string trimmed = text.Trim();
            // Names only; Enum.TryParse would also accept numbers.
            foreach (T candidate in Enum.GetValues<T>())
            {
                if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    error = null;
                    return true;
                }
            }
            value = default;
            error = $"<{name}> \"{trimmed}\" is not one of {string.Join(", ", Enum.GetNames<T>())}";
            return false;
        }

        private static bool TryColor(string text, out DS4Color color)
        {
            color = default;
            string[] parts = text.Split(',');
            if (parts.Length != 3)
                return false;
            var rgb = new byte[3];
            for (int index = 0; index < 3; index++)
            {
                if (!byte.TryParse(parts[index].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out rgb[index]))
                    return false;
            }
            color = new DS4Color(rgb[0], rgb[1], rgb[2]);
            return true;
        }

        // "0:8,300:5,1200:4": ms:PullY percent, 1-16 points, ms strictly
        // ascending. Absent = no pattern.
        private static bool TryPattern(string text, out int[] times, out double[] pulls,
            out string error)
        {
            times = null;
            pulls = null;
            error = null;
            if (text == null)
                return true;
            string[] points = text.Split(',');
            if (string.IsNullOrWhiteSpace(text) || points.Length > MaxPatternPoints)
            {
                error = $"<Pattern> \"{text.Trim()}\" needs 1 to {MaxPatternPoints} ms:PullY points";
                return false;
            }
            var t = new int[points.Length];
            var p = new double[points.Length];
            for (int index = 0; index < points.Length; index++)
            {
                string[] pair = points[index].Split(':');
                if (pair.Length != 2 ||
                    !int.TryParse(pair[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out t[index]) ||
                    t[index] < 0 || (index > 0 && t[index] <= t[index - 1]) ||
                    !double.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double percent) ||
                    !double.IsFinite(percent) || percent < 0 || percent > 50)
                {
                    error = $"<Pattern> \"{text.Trim()}\": point {index + 1} is not ms:PullY " +
                        "with ms ascending and PullY from 0 to 50";
                    return false;
                }
                p[index] = percent / 100.0;
            }
            times = t;
            pulls = p;
            return true;
        }
    }
}
