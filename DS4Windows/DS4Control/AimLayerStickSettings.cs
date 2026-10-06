using System;
using System.IO;
using System.Threading;
using DS4Windows.DS4Control;

namespace DS4Windows
{
    /// <summary>
    /// Right-stick settings borrowed from an aim layer's source profile, plus
    /// the base profile's trigger rule. Immutable after construction: every
    /// reference it holds is a private copy, so the input thread can read it
    /// without locks. Never mutate <see cref="RSModInfo"/> or
    /// <see cref="RSOutBezierCurve"/>.
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
    /// What a base profile asks to borrow. Kept even when the source cannot
    /// be built, so saving the source later can still bring the layer up.
    /// </summary>
    internal sealed class AimLayerRequest
    {
        internal AimLayerRequest(string baseProfile, string sourceProfile,
            DS4Controls trigger, byte threshold, bool useSourceLightbar)
        {
            BaseProfile = baseProfile;
            SourceProfile = sourceProfile;
            Trigger = trigger;
            Threshold = threshold;
            UseSourceLightbar = useSourceLightbar;
        }

        internal string BaseProfile { get; }
        internal string SourceProfile { get; }
        internal DS4Controls Trigger { get; }
        internal byte Threshold { get; }
        internal bool UseSourceLightbar { get; }

        internal bool Borrows(string profileName) =>
            string.Equals(SourceProfile, profileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Result of preparing a base profile's aim layer.</summary>
    internal sealed class AimLayerPreparation
    {
        internal AimLayerPreparation(AimLayerRequest request,
            AimLayerStickSettings settings, long saveSequence)
        {
            Request = request;
            Settings = settings;
            SaveSequence = saveSequence;
        }

        internal AimLayerRequest Request { get; }
        internal AimLayerStickSettings Settings { get; }
        internal long SaveSequence { get; }
    }

    /// <summary>
    /// Per-device published aim layer. The input thread only calls
    /// <see cref="Current"/>; everything else runs on profile load/save paths.
    /// </summary>
    internal static class AimLayerState
    {
        private sealed class Entry
        {
            internal AimLayerRequest Request;
            internal AimLayerStickSettings Built;
            internal long Generation;
            internal bool Suspended;
        }

        private const int SaveRebuildAttempts = 4;

        // Test seam: runs between a save-triggered build and its publish.
        internal static Action<int> RebuildBuiltForTests;

        private static readonly AimLayerStickSettings[] published =
            new AimLayerStickSettings[Global.TEST_PROFILE_ITEM_COUNT];
        private static readonly Entry[] entries = CreateEntries();
        // Last warning per slot, so repeated loads of a broken setup (temp
        // switches back to the base profile) log once, not every time.
        private static readonly string[] lastWarning =
            new string[Global.TEST_PROFILE_ITEM_COUNT];
        private static long saveSequence;

        private static Entry[] CreateEntries()
        {
            var result = new Entry[Global.TEST_PROFILE_ITEM_COUNT];
            for (int index = 0; index < result.Length; index++)
                result[index] = new Entry();
            return result;
        }

        /// <summary>Null = layer off. Lock- and allocation-free.</summary>
        internal static AimLayerStickSettings Current(int device) =>
            Volatile.Read(ref published[device]);

        internal static long ReadSaveSequence() => Interlocked.Read(ref saveSequence);

        // Called on the cold prepare path with the base profile mapped into its
        // private validation store. Never throws: the aim layer must not stop
        // the base profile from loading.
        internal static AimLayerPreparation Prepare(BackingStore baseStore, int device,
            string basePath, long sequence)
        {
            if (!baseStore.aimLayerEnabled[device])
                return null;

            string baseName = Path.GetFileNameWithoutExtension(basePath) ?? string.Empty;
            string sourceName = baseStore.aimLayerSourceProfile[device]?.Trim() ?? string.Empty;
            if (string.Equals(sourceName, baseName, StringComparison.OrdinalIgnoreCase))
                return null; // Borrowing from itself would change nothing.

            var request = new AimLayerRequest(baseName, sourceName,
                baseStore.aimLayerTrigger[device], baseStore.aimLayerThreshold[device],
                baseStore.aimLayerUseSourceLightbar[device]);
            return new AimLayerPreparation(request, Build(request, device), sequence);
        }

        internal static AimLayerStickSettings Build(AimLayerRequest request, int device)
        {
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
                        Volatile.Write(ref lastWarning[device], null);
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

            string message = $"Aim layer of profile \"{request.BaseProfile}\" is off: " +
                $"source profile \"{request.SourceProfile}\": {failure}";
            if (Interlocked.Exchange(ref lastWarning[device], message) != message)
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
                entry.Request = preparation?.Request;
                entry.Built = preparation?.Settings;
                entry.Suspended = false;
                entry.Generation++;
                Volatile.Write(ref published[device], entry.Built);
                stale = entry.Request != null &&
                    preparation.SaveSequence != ReadSaveSequence();
            }

            // A profile was saved after this one was prepared; it may have been
            // the source. Re-read it off the pause (generation-checked).
            if (stale)
                ThreadPool.QueueUserWorkItem(static state =>
                    RebuildIfBorrowing((int)state, null), device);
        }

        internal static void Clear(int device)
        {
            lock (entries[device])
            {
                Entry entry = entries[device];
                entry.Request = null;
                entry.Built = null;
                entry.Suspended = false;
                entry.Generation++;
                Volatile.Write(ref published[device], null);
            }
        }

        // Controller removed: stop publishing, but keep the request so a
        // reconnect that keeps the slot's profile (temp/auto profile, Joy-Con
        // handoff) can resume without reloading it.
        internal static void Suspend(int device)
        {
            lock (entries[device])
            {
                entries[device].Suspended = true;
                Volatile.Write(ref published[device], null);
            }
        }

        internal static void Resume(int device)
        {
            lock (entries[device])
            {
                Entry entry = entries[device];
                entry.Suspended = false;
                Volatile.Write(ref published[device], entry.Built);
            }
        }

        // SaveProfileNew wrote profileName. Rebuild every slot borrowing it.
        // Cheap when nothing borrows it; ~1 ms per borrowing slot otherwise.
        internal static void OnProfileSaved(string profileName)
        {
            Interlocked.Increment(ref saveSequence);
            for (int device = 0; device < entries.Length; device++)
                RebuildIfBorrowing(device, profileName);
        }

        // profileName null = rebuild whatever the slot borrows.
        private static void RebuildIfBorrowing(int device, string profileName)
        {
            for (int attempt = 0; attempt < SaveRebuildAttempts; attempt++)
            {
                AimLayerRequest request;
                long generation;
                lock (entries[device])
                {
                    request = entries[device].Request;
                    generation = entries[device].Generation;
                }
                if (request == null || (profileName != null && !request.Borrows(profileName)))
                    return;

                AimLayerStickSettings built = Build(request, device);
                RebuildBuiltForTests?.Invoke(device);
                lock (entries[device])
                {
                    Entry entry = entries[device];
                    // A newer apply or rebuild won; retry so the last file
                    // write is what ends up published.
                    if (entry.Generation != generation)
                        continue;
                    entry.Built = built;
                    entry.Generation++;
                    if (!entry.Suspended)
                        Volatile.Write(ref published[device], built);
                    return;
                }
            }
        }
    }
}
