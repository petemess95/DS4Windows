using System;
using System.Collections.Generic;
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
            string sourceProfile, bool useSourceLightbar)
        {
            Enabled = enabled;
            Trigger = BackingStore.NormalizeAimLayerTrigger(trigger);
            Threshold = threshold;
            Delay = BackingStore.NormalizeAimLayerDelay(delay);
            SourceProfile = sourceProfile?.Trim() ?? string.Empty;
            UseSourceLightbar = useSourceLightbar;
        }

        public bool Enabled { get; }
        public DS4Controls Trigger { get; }
        public byte Threshold { get; }
        // Milliseconds the trigger must be held before the layer comes on.
        public int Delay { get; }
        public string SourceProfile { get; }
        public bool UseSourceLightbar { get; }

        // Unconfigured blocks are not saved.
        public bool IsDefault => !Enabled &&
            Trigger == BackingStore.DEFAULT_AIM_LAYER_TRIGGER &&
            Threshold == BackingStore.DEFAULT_AIM_LAYER_THRESHOLD &&
            Delay == BackingStore.DEFAULT_AIM_LAYER_DELAY &&
            SourceProfile.Length == 0 && !UseSourceLightbar;
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

        private AimLayerSet(AimLayerStickSettings[] layers) => this.layers = layers;

        internal int Count => layers.Length;

        internal AimLayerStickSettings this[int index] => layers[index];

        // Null when nothing built, so "no layer" stays a null check.
        internal static AimLayerSet From(IReadOnlyList<AimLayerStickSettings> built)
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
            return new AimLayerSet(layers);
        }

        // Same layers under a new reference, so publishing it restarts the
        // hold timers even when nothing was rebuilt (resume, re-apply).
        internal AimLayerSet Renewed() => new(layers);

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
            int delay = BackingStore.DEFAULT_AIM_LAYER_DELAY, int layerIndex = 0)
        {
            BaseProfile = baseProfile;
            SourceProfile = sourceProfile;
            Trigger = trigger;
            Threshold = threshold;
            UseSourceLightbar = useSourceLightbar;
            Delay = delay;
            LayerIndex = layerIndex;
        }

        internal string BaseProfile { get; }
        internal string SourceProfile { get; }
        internal DS4Controls Trigger { get; }
        internal byte Threshold { get; }
        internal bool UseSourceLightbar { get; }
        internal int Delay { get; }
        internal int LayerIndex { get; }

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
            IReadOnlyList<AimLayerStickSettings> built, long saveSequence)
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
            Set = AimLayerSet.From(this.built);
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
                requests.Add(new AimLayerRequest(baseName, config.SourceProfile,
                    config.Trigger, config.Threshold, config.UseSourceLightbar,
                    config.Delay, index));
            }
            if (requests == null)
                return null;

            var built = new AimLayerStickSettings[requests.Count];
            for (int index = 0; index < built.Length; index++)
                built[index] = Build(requests[index], device);
            return new AimLayerPreparation(requests, built, sequence);
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
                    entry.Set = AimLayerSet.From(built);
                    entry.Generation++;
                    if (!entry.Suspended)
                        Volatile.Write(ref published[device], entry.Set);
                    return;
                }
            }
        }
    }
}
