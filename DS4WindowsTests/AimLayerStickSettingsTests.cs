using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;

namespace DS4WindowsTests;

/// <summary>
/// Borrowed aim-layer stick settings (Task 3.2): built from the source
/// profile file, published per slot, rebuilt on save, cleared when off.
/// Uses a temp appdatapath and a private live store; never the real one.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimLayerStickSettingsTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;
    private const string ExpoCurve = "0.72, 0.26, 1.00, 1.00";

    private static readonly FieldInfo StoreField =
        typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);

    private string folder;
    private string oldAppDataPath;
    private object oldStore;
    private string oldTempName;
    private bool oldUseTemp;
    private bool oldDistance;
    private string oldProfilePath;
    private BackingStore live;
    private readonly List<string> warnings = new();

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-borrow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
        oldStore = StoreField.GetValue(null);
        live = new BackingStore();
        StoreField.SetValue(null, live);
        oldTempName = Global.tempprofilename[Slot];
        oldUseTemp = Global.useTempProfile[Slot];
        oldDistance = Global.tempprofileDistance[Slot];
        oldProfilePath = live.profilePath[Slot];
        AimLayerState.Clear(Slot);
        AppLogger.GuiLog += CaptureLog;
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppLogger.GuiLog -= CaptureLog;
        AimLayerState.RebuildBuiltForTests = null;
        AimLayerState.Clear(Slot);
        StoreField.SetValue(null, oldStore);
        Global.appdatapath = oldAppDataPath;
        Global.tempprofilename[Slot] = oldTempName;
        Global.useTempProfile[Slot] = oldUseTemp;
        Global.tempprofileDistance[Slot] = oldDistance;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private void CaptureLog(object sender, DebugEventArgs e)
    {
        if (e.Data.StartsWith("Aim layer", StringComparison.Ordinal))
            lock (warnings) warnings.Add(e.Data);
    }

    private static ControlService UninitializedService() =>
        (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));

    // Edge Expo-like source: custom RS curve plus every other borrowed field
    // set away from its default.
    private static void SaveSource(string name, double sens = 1.7,
        string curve = ExpoCurve, int deadZone = 21)
    {
        var store = new BackingStore();
        StickDeadZoneInfo rs = store.rsModInfo[Slot];
        rs.deadZone = deadZone;
        rs.antiDeadZone = 12;
        rs.maxZone = 93;
        rs.maxOutput = 97.5;
        rs.maxOutputForce = true;
        rs.fuzz = 3;
        rs.verticalScale = 110.0;
        rs.deadzoneType = StickDeadZoneInfo.DeadZoneType.Axial;
        rs.outerBindDeadZone = 60.0;
        rs.outerBindInvert = true;
        rs.xAxisDeadInfo.deadZone = 14;
        rs.xAxisDeadInfo.antiDeadZone = 9;
        rs.xAxisDeadInfo.maxZone = 88;
        rs.xAxisDeadInfo.maxOutput = 95.0;
        rs.yAxisDeadInfo.deadZone = 16;
        rs.yAxisDeadInfo.antiDeadZone = 7;
        rs.yAxisDeadInfo.maxZone = 86;
        rs.yAxisDeadInfo.maxOutput = 94.0;
        store.RSSens[Slot] = sens;
        store.squStickInfo[Slot].rsMode = true;
        store.squStickInfo[Slot].rsRoundness = 3.5;
        store.rsOutBezierCurveObj[Slot].CustomDefinition = curve;
        store.setRsOutCurveMode(Slot, 6);
        store.lightbarSettingInfo[Slot].ds4winSettings.m_Led = new DS4Color(255, 255, 255);
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
    }

    private static void SaveBase(string name, string source, bool enabled = true)
    {
        var store = new BackingStore();
        store.aimLayerEnabled[Slot] = enabled;
        store.aimLayerSourceProfile[Slot] = source;
        store.aimLayerTrigger[Slot] = DS4Controls.L2;
        store.aimLayerThreshold[Slot] = 100;
        store.aimLayerUseSourceLightbar[Slot] = true;
        store.lightbarSettingInfo[Slot].ds4winSettings.m_Led = new DS4Color(0, 0, 255);
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
    }

    private void Load(string name)
    {
        Assert.IsTrue(Global.LoadProfile(Slot, false, UninitializedService(),
            xinputChange: false, postLoad: false, profileName: name));
    }

    private BackingStore LoadReference(string name)
    {
        var store = new BackingStore();
        Assert.IsTrue(store.LoadProfileNew(Slot, false, UninitializedService(),
            out _, Path.Combine(folder, "Profiles", $"{name}.xml"),
            xinputChange: false, postLoad: false));
        return store;
    }

    private static void AssertMatches(BackingStore expected, AimLayerStickSettings actual)
    {
        StickDeadZoneInfo e = expected.rsModInfo[Slot], a = actual.RSModInfo;
        Assert.AreEqual(e.deadZone, a.deadZone);
        Assert.AreEqual(e.antiDeadZone, a.antiDeadZone);
        Assert.AreEqual(e.maxZone, a.maxZone);
        Assert.AreEqual(e.maxOutput, a.maxOutput);
        Assert.AreEqual(e.maxOutputForce, a.maxOutputForce);
        Assert.AreEqual(e.fuzz, a.fuzz);
        Assert.AreEqual(e.verticalScale, a.verticalScale);
        Assert.AreEqual(e.deadzoneType, a.deadzoneType);
        Assert.AreEqual(e.outerBindDeadZone, a.outerBindDeadZone);
        Assert.AreEqual(e.outerBindInvert, a.outerBindInvert);
        foreach (var (ex, ax) in new[] { (e.xAxisDeadInfo, a.xAxisDeadInfo), (e.yAxisDeadInfo, a.yAxisDeadInfo) })
        {
            Assert.AreEqual(ex.deadZone, ax.deadZone);
            Assert.AreEqual(ex.antiDeadZone, ax.antiDeadZone);
            Assert.AreEqual(ex.maxZone, ax.maxZone);
            Assert.AreEqual(ex.maxOutput, ax.maxOutput);
        }
        Assert.AreEqual(expected.RSSens[Slot], actual.RSSens);
        Assert.AreEqual(expected.squStickInfo[Slot].rsMode, actual.RSSquareStick);
        Assert.AreEqual(expected.squStickInfo[Slot].rsRoundness, actual.RSSquareStickRoundness);
        Assert.AreEqual(expected.getRsOutCurveMode(Slot), actual.RSOutCurveMode);
        AssertSameCurve(expected.rsOutBezierCurveObj[Slot], actual.RSOutBezierCurve);
        Assert.AreEqual(expected.lightbarSettingInfo[Slot].ds4winSettings.m_Led, actual.LightbarColor);
    }

    private static void AssertSameCurve(BezierCurve expected, BezierCurve actual)
    {
        Assert.AreEqual(expected.CustomDefinition, actual.CustomDefinition);
        CollectionAssert.AreEqual(expected.arrayBezierLUT, actual.arrayBezierLUT);
        for (int i = 0; i <= 100; i++)
        {
            double input = i / 100.0;
            Assert.AreEqual(expected.TryEvaluateNormalized(input, out double e),
                actual.TryEvaluateNormalized(input, out double a));
            Assert.AreEqual(e, a, 0.0, $"input {input}");
        }
    }

    private static BezierCurve Expo()
    {
        var curve = new BezierCurve();
        Assert.IsTrue(curve.InitBezierCurve(ExpoCurve, BezierCurve.AxisType.LSRS, true));
        return curve;
    }

    private static AimLayerStickSettings WaitFor(Func<AimLayerStickSettings, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AimLayerStickSettings current;
        while (!done(current = AimLayerState.Current(Slot)) && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        return current;
    }

    [TestMethod]
    public void BorrowedSettingsMatchSourceProfile()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");

        AimLayerStickSettings settings = AimLayerState.Current(Slot);
        Assert.IsNotNull(settings);
        AssertMatches(LoadReference("Edge Expo"), settings);
        Assert.AreEqual(21, settings.RSModInfo.deadZone);
        Assert.AreEqual(1.7, settings.RSSens);
        Assert.IsTrue(settings.RSSquareStick);
        Assert.AreEqual(3.5, settings.RSSquareStickRoundness);
        Assert.AreEqual(6, settings.RSOutCurveMode);
        AssertSameCurve(Expo(), settings.RSOutBezierCurve);
        Assert.AreEqual(new DS4Color(255, 255, 255), settings.LightbarColor);

        Assert.AreEqual("Edge Linear", settings.BaseProfile);
        Assert.AreEqual("Edge Expo", settings.SourceProfile);
        Assert.AreEqual(DS4Controls.L2, settings.Trigger);
        Assert.AreEqual((byte)100, settings.Threshold);
        Assert.IsTrue(settings.UseSourceLightbar);
        Assert.IsFalse(settings.IsTriggerHeld(100, 255));
        Assert.IsTrue(settings.IsTriggerHeld(101, 0));

        // The live slot keeps the base profile's own right stick.
        Assert.AreEqual(0, live.getRsOutCurveMode(Slot));
        Assert.AreEqual(new DS4Color(0, 0, 255), live.lightbarSettingInfo[Slot].ds4winSettings.m_Led);
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void R2TriggerAndThresholdComeFromBaseProfile()
    {
        SaveSource("Edge Expo");
        var store = new BackingStore();
        store.aimLayerEnabled[Slot] = true;
        store.aimLayerSourceProfile[Slot] = "Edge Expo";
        store.aimLayerTrigger[Slot] = DS4Controls.R2;
        store.aimLayerThreshold[Slot] = 180;
        Assert.IsTrue(store.SaveProfileNew(Slot, "Base"));
        Load("Base");

        AimLayerStickSettings settings = AimLayerState.Current(Slot);
        Assert.AreEqual(DS4Controls.R2, settings.Trigger);
        Assert.AreEqual((byte)180, settings.Threshold);
        Assert.IsFalse(settings.UseSourceLightbar);
        Assert.IsFalse(settings.IsTriggerHeld(255, 180));
        Assert.IsTrue(settings.IsTriggerHeld(0, 181));
    }

    [TestMethod]
    public void SavingSourceRebuildsBorrowingSlot()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");
        AimLayerStickSettings before = AimLayerState.Current(Slot);
        Assert.AreEqual(1.7, before.RSSens);

        // Unrelated saves leave the published object alone.
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Other"));
        Assert.AreSame(before, AimLayerState.Current(Slot));

        // Edit the source the way the editor does: load, change, save.
        BackingStore editor = LoadReference("Edge Expo");
        editor.RSSens[Slot] = 2.5;
        editor.rsOutBezierCurveObj[Slot].CustomDefinition = "0.40, 0.10, 0.90, 0.80";
        editor.setRsOutCurveMode(Slot, 6);
        Assert.IsTrue(editor.SaveProfileNew(Slot, "Edge Expo"));

        AimLayerStickSettings after = AimLayerState.Current(Slot);
        Assert.AreNotSame(before, after);
        Assert.AreEqual(2.5, after.RSSens);
        AssertMatches(LoadReference("Edge Expo"), after);
        Assert.AreEqual(1.7, before.RSSens, "The old object must not change.");
        AssertSameCurve(Expo(), before.RSOutBezierCurve);
    }

    [TestMethod]
    public void SourceNameMatchesCaseInsensitively()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "edge expo");
        Load("Edge Linear");
        Assert.IsNotNull(AimLayerState.Current(Slot));

        SaveSource("EDGE EXPO", sens: 3.0);
        Assert.AreEqual(3.0, AimLayerState.Current(Slot).RSSens);
    }

    [TestMethod]
    public void MissingSourceLeavesLayerOffAndWarnsOnce()
    {
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");
        Assert.IsNull(AimLayerState.Current(Slot));
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "Edge Expo");

        // Re-applying the same broken setup (e.g. back from a temp profile)
        // does not log again.
        Load("Edge Linear");
        Assert.IsNull(AimLayerState.Current(Slot));
        Assert.AreEqual(1, warnings.Count);

        // Creating the source later brings the layer up without a reload.
        SaveSource("Edge Expo");
        Assert.IsNotNull(AimLayerState.Current(Slot));
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void InvalidSourceLeavesLayerOff()
    {
        File.WriteAllText(Path.Combine(folder, "Profiles", "Broken.xml"), "<DS4Windows>");
        SaveBase("Edge Linear", "Broken");
        Load("Edge Linear");
        Assert.IsNull(AimLayerState.Current(Slot));
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));

        SaveBase("Bad Name", "..\\Edge Expo");
        Load("Bad Name");
        Assert.IsNull(AimLayerState.Current(Slot));
        Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void SourceEqualToBaseIsIgnored()
    {
        SaveBase("Edge Linear", "edge linear");
        Load("Edge Linear");
        Assert.IsNull(AimLayerState.Current(Slot));
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void DisabledLayerPublishesNothing()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo", enabled: false);
        Load("Edge Linear");
        Assert.IsNull(AimLayerState.Current(Slot));

        // Saving the source does not turn a disabled layer on.
        SaveSource("Edge Expo");
        Assert.IsNull(AimLayerState.Current(Slot));
    }

    [TestMethod]
    public void ProfileWithoutLayerClearsAndBaseBringsItBack()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));
        Load("Edge Linear");
        Assert.IsNotNull(AimLayerState.Current(Slot));

        // Temporary profile without an aim layer: layer off.
        Assert.IsTrue(Global.LoadTempProfile(Slot, "Plain", false,
            UninitializedService(), xinputChange: false));
        Assert.IsNull(AimLayerState.Current(Slot));
        // Saving the source while the temp profile is active must not
        // resurrect the base profile's layer.
        SaveSource("Edge Expo");
        Assert.IsNull(AimLayerState.Current(Slot));

        // Back to the base profile: layer on again.
        Load("Edge Linear");
        Assert.IsNotNull(AimLayerState.Current(Slot));

        // Missing base profile: the legacy reset fallback clears it too.
        Assert.IsFalse(Global.LoadProfile(Slot, false, UninitializedService(),
            xinputChange: false, postLoad: false, profileName: "Gone"));
        Assert.IsNull(AimLayerState.Current(Slot));
    }

    [TestMethod]
    public void PublishedObjectIsIndependentOfStores()
    {
        SaveSource("Edge Expo");
        BackingStore scratch = LoadReference("Edge Expo");
        var request = new AimLayerRequest("Edge Linear", "Edge Expo", DS4Controls.L2, 100, true);
        AimLayerStickSettings settings = AimLayerStickSettings.FromStore(request, scratch, Slot);

        Assert.AreNotSame(scratch.rsModInfo[Slot], settings.RSModInfo);
        Assert.AreNotSame(scratch.rsModInfo[Slot].xAxisDeadInfo, settings.RSModInfo.xAxisDeadInfo);
        Assert.AreNotSame(scratch.rsModInfo[Slot].yAxisDeadInfo, settings.RSModInfo.yAxisDeadInfo);
        Assert.AreNotSame(scratch.rsOutBezierCurveObj[Slot], settings.RSOutBezierCurve);
        Assert.AreNotSame(scratch.rsOutBezierCurveObj[Slot].arrayBezierLUT,
            settings.RSOutBezierCurve.arrayBezierLUT);

        // Mutate everything in the scratch store, including re-initialising
        // its curve (which rewrites the LUT in place).
        scratch.rsModInfo[Slot].Reset();
        scratch.RSSens[Slot] = 4.0;
        scratch.squStickInfo[Slot].Reset();
        scratch.rsOutBezierCurveObj[Slot].CustomDefinition = "0.10, 0.90, 0.20, 0.95";
        scratch.setRsOutCurveMode(Slot, 6);
        scratch.setRsOutCurveMode(Slot, 0);
        scratch.lightbarSettingInfo[Slot].ds4winSettings.m_Led = new DS4Color(1, 2, 3);

        Assert.AreEqual(21, settings.RSModInfo.deadZone);
        Assert.AreEqual(14, settings.RSModInfo.xAxisDeadInfo.deadZone);
        Assert.AreEqual(16, settings.RSModInfo.yAxisDeadInfo.deadZone);
        Assert.AreEqual(StickDeadZoneInfo.DeadZoneType.Axial, settings.RSModInfo.deadzoneType);
        Assert.AreEqual(1.7, settings.RSSens);
        Assert.IsTrue(settings.RSSquareStick);
        Assert.AreEqual(3.5, settings.RSSquareStickRoundness);
        Assert.AreEqual(6, settings.RSOutCurveMode);
        AssertSameCurve(Expo(), settings.RSOutBezierCurve);
        Assert.AreEqual(new DS4Color(255, 255, 255), settings.LightbarColor);
    }

    [TestMethod]
    public void PublishedObjectIsIndependentOfLiveSlot()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");
        AimLayerStickSettings settings = AimLayerState.Current(Slot);

        Assert.AreNotSame(live.rsModInfo[Slot], settings.RSModInfo);
        Assert.AreNotSame(live.rsOutBezierCurveObj[Slot], settings.RSOutBezierCurve);
        live.rsModInfo[Slot].deadZone = 99;
        live.RSSens[Slot] = 4.0;
        live.rsOutBezierCurveObj[Slot].CustomDefinition = "0.10, 0.90, 0.20, 0.95";
        live.setRsOutCurveMode(Slot, 6);

        Assert.AreSame(settings, AimLayerState.Current(Slot));
        Assert.AreEqual(21, settings.RSModInfo.deadZone);
        Assert.AreEqual(1.7, settings.RSSens);
        AssertSameCurve(Expo(), settings.RSOutBezierCurve);
    }

    [TestMethod]
    public void SuspendHidesLayerAndResumeRestoresIt()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");
        AimLayerStickSettings settings = AimLayerState.Current(Slot);

        AimLayerState.Suspend(Slot); // controller removed
        Assert.IsNull(AimLayerState.Current(Slot));
        // A source save while removed is kept for later but not published.
        SaveSource("Edge Expo", sens: 2.2);
        Assert.IsNull(AimLayerState.Current(Slot));

        AimLayerState.Resume(Slot); // reconnect that keeps the profile
        Assert.AreEqual(2.2, AimLayerState.Current(Slot).RSSens);
        Assert.AreNotSame(settings, AimLayerState.Current(Slot));
    }

    [TestMethod]
    public void RebuildLosingToNewerApplyDoesNotPublish()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));
        Load("Edge Linear");

        // A profile without a layer is applied while the save-triggered
        // rebuild is between its build and its publish.
        int calls = 0;
        AimLayerState.RebuildBuiltForTests = device =>
        {
            if (device == Slot && calls++ == 0)
                Load("Plain");
        };
        SaveSource("Edge Expo", sens: 2.2);
        Assert.AreEqual(1, calls);
        Assert.IsNull(AimLayerState.Current(Slot));
    }

    [TestMethod]
    public void RebuildLosingToNewerBorrowingApplyRetriesWithFreshFile()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        SaveBase("Edge Linear 2", "Edge Expo");
        Load("Edge Linear");

        int calls = 0;
        AimLayerState.RebuildBuiltForTests = device =>
        {
            if (device != Slot || calls++ != 0)
                return;
            // Another borrowing base is applied, then the source changes again.
            Load("Edge Linear 2");
            AimLayerState.RebuildBuiltForTests = null;
            SaveSource("Edge Expo", sens: 3.3);
        };
        SaveSource("Edge Expo", sens: 2.2);
        AimLayerStickSettings settings = AimLayerState.Current(Slot);
        Assert.AreEqual("Edge Linear 2", settings.BaseProfile);
        Assert.AreEqual(3.3, settings.RSSens);
    }

    [TestMethod]
    public void SaveBetweenPrepareAndApplyIsPickedUp()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        string path = Path.Combine(folder, "Profiles", "Edge Linear.xml");
        Assert.IsTrue(PreparedProfileLoad.TryPrepare(path, Slot, out var prepared, out _, out _));
        Assert.AreEqual(1.7, prepared.AimLayer.Settings.RSSens);

        // Nothing borrows yet, so this save alone rebuilds nothing.
        SaveSource("Edge Expo", sens: 2.2);
        Assert.IsTrue(live.ApplyPreparedProfileNew(prepared, false, UninitializedService(),
            out _, xinputChange: false, postLoad: false));

        AimLayerStickSettings settings = WaitFor(s => s?.RSSens == 2.2);
        Assert.AreEqual(2.2, settings?.RSSens);
    }
}
