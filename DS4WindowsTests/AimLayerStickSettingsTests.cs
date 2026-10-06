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
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(enabled, DS4Controls.L2, 100, 0, source, true),
        };
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

    // The one published layer of a single-block profile; null = none.
    private static AimLayerStickSettings Layer()
    {
        AimLayerSet layers = AimLayerState.Current(Slot);
        if (layers == null)
            return null;
        Assert.AreEqual(1, layers.Count, "Single-block profile publishes one layer.");
        return layers[0];
    }

    private static AimLayerStickSettings WaitFor(Func<AimLayerStickSettings, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AimLayerStickSettings current;
        while (!done(current = Layer()) && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        return current;
    }

    [TestMethod]
    public void BorrowedSettingsMatchSourceProfile()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");

        AimLayerStickSettings settings = Layer();
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
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(true, DS4Controls.R2, 180, 0, "Edge Expo", false),
        };
        Assert.IsTrue(store.SaveProfileNew(Slot, "Base"));
        Load("Base");

        AimLayerStickSettings settings = Layer();
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
        AimLayerStickSettings before = Layer();
        Assert.AreEqual(1.7, before.RSSens);

        // Unrelated saves leave the published object alone.
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Other"));
        Assert.AreSame(before, Layer());

        // Edit the source the way the editor does: load, change, save.
        BackingStore editor = LoadReference("Edge Expo");
        editor.RSSens[Slot] = 2.5;
        editor.rsOutBezierCurveObj[Slot].CustomDefinition = "0.40, 0.10, 0.90, 0.80";
        editor.setRsOutCurveMode(Slot, 6);
        Assert.IsTrue(editor.SaveProfileNew(Slot, "Edge Expo"));

        AimLayerStickSettings after = Layer();
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
        Assert.IsNotNull(Layer());

        SaveSource("EDGE EXPO", sens: 3.0);
        Assert.AreEqual(3.0, Layer().RSSens);
    }

    [TestMethod]
    public void MissingSourceLeavesLayerOffAndWarnsOnce()
    {
        SaveBase("Edge Linear", "Edge Expo");
        Load("Edge Linear");
        Assert.IsNull(Layer());
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "Edge Expo");

        // Re-applying the same broken setup (e.g. back from a temp profile)
        // does not log again.
        Load("Edge Linear");
        Assert.IsNull(Layer());
        Assert.AreEqual(1, warnings.Count);

        // Creating the source later brings the layer up without a reload.
        SaveSource("Edge Expo");
        Assert.IsNotNull(Layer());
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void InvalidSourceLeavesLayerOff()
    {
        File.WriteAllText(Path.Combine(folder, "Profiles", "Broken.xml"), "<DS4Windows>");
        SaveBase("Edge Linear", "Broken");
        Load("Edge Linear");
        Assert.IsNull(Layer());
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));

        SaveBase("Bad Name", "..\\Edge Expo");
        Load("Bad Name");
        Assert.IsNull(Layer());
        Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void SourceEqualToBaseIsIgnored()
    {
        SaveBase("Edge Linear", "edge linear");
        Load("Edge Linear");
        Assert.IsNull(Layer());
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void DisabledLayerPublishesNothing()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo", enabled: false);
        Load("Edge Linear");
        Assert.IsNull(Layer());

        // Saving the source does not turn a disabled layer on.
        SaveSource("Edge Expo");
        Assert.IsNull(Layer());
    }

    [TestMethod]
    public void ProfileWithoutLayerClearsAndBaseBringsItBack()
    {
        SaveSource("Edge Expo");
        SaveBase("Edge Linear", "Edge Expo");
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));
        Load("Edge Linear");
        Assert.IsNotNull(Layer());

        // Temporary profile without an aim layer: layer off.
        Assert.IsTrue(Global.LoadTempProfile(Slot, "Plain", false,
            UninitializedService(), xinputChange: false));
        Assert.IsNull(Layer());
        // Saving the source while the temp profile is active must not
        // resurrect the base profile's layer.
        SaveSource("Edge Expo");
        Assert.IsNull(Layer());

        // Back to the base profile: layer on again.
        Load("Edge Linear");
        Assert.IsNotNull(Layer());

        // Missing base profile: the legacy reset fallback clears it too.
        Assert.IsFalse(Global.LoadProfile(Slot, false, UninitializedService(),
            xinputChange: false, postLoad: false, profileName: "Gone"));
        Assert.IsNull(Layer());
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
        AimLayerStickSettings settings = Layer();

        Assert.AreNotSame(live.rsModInfo[Slot], settings.RSModInfo);
        Assert.AreNotSame(live.rsOutBezierCurveObj[Slot], settings.RSOutBezierCurve);
        live.rsModInfo[Slot].deadZone = 99;
        live.RSSens[Slot] = 4.0;
        live.rsOutBezierCurveObj[Slot].CustomDefinition = "0.10, 0.90, 0.20, 0.95";
        live.setRsOutCurveMode(Slot, 6);

        Assert.AreSame(settings, Layer());
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
        AimLayerStickSettings settings = Layer();

        AimLayerState.Suspend(Slot); // controller removed
        Assert.IsNull(Layer());
        // A source save while removed is kept for later but not published.
        SaveSource("Edge Expo", sens: 2.2);
        Assert.IsNull(Layer());

        AimLayerState.Resume(Slot); // reconnect that keeps the profile
        Assert.AreEqual(2.2, Layer().RSSens);
        Assert.AreNotSame(settings, Layer());
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
        Assert.IsNull(Layer());
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
        AimLayerStickSettings settings = Layer();
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
        Assert.AreEqual(1.7, prepared.AimLayer.Built[0].RSSens);

        // Nothing borrows yet, so this save alone rebuilds nothing.
        SaveSource("Edge Expo", sens: 2.2);
        Assert.IsTrue(live.ApplyPreparedProfileNew(prepared, false, UninitializedService(),
            out _, xinputChange: false, postLoad: false));

        AimLayerStickSettings settings = WaitFor(s => s?.RSSens == 2.2);
        Assert.AreEqual(2.2, settings?.RSSens);
    }

    // ---- Several layers (Task 5.1) ----

    private const string HipfireCurve = "0.30, 0.11, 1.00, 1.00";

    private static AimLayerConfig L2Layer(string source) =>
        new(true, DS4Controls.L2, 100, 0, source, true);

    private static AimLayerConfig R2Layer(string source) =>
        new(true, DS4Controls.R2, 100, 100, source, true);

    private static void SaveLayers(string name, params AimLayerConfig[] layers)
    {
        var store = new BackingStore();
        store.aimLayers[Slot] = layers;
        store.lightbarSettingInfo[Slot].ds4winSettings.m_Led = new DS4Color(0, 0, 255);
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
    }

    private static void SaveHipfire(double sens = 1.2) =>
        SaveSource("Edge Hipfire", sens: sens, curve: HipfireCurve, deadZone: 8);

    // The user's setup: Edge Linear with L2 -> Edge Expo, then R2 -> Edge
    // Hipfire after 100 ms.
    private void LoadUserSetup()
    {
        SaveSource("Edge Expo");
        SaveHipfire();
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("Edge Hipfire"));
        Load("Edge Linear");
    }

    private static AimLayerSet Layers() => AimLayerState.Current(Slot);

    [TestMethod]
    public void TwoLayersArePublishedInFileOrder()
    {
        LoadUserSetup();
        AimLayerSet layers = Layers();
        Assert.IsNotNull(layers);
        Assert.AreEqual(2, layers.Count);

        AimLayerStickSettings aim = layers[0], hip = layers[1];
        Assert.AreEqual("Edge Expo", aim.SourceProfile);
        Assert.AreEqual(DS4Controls.L2, aim.Trigger);
        Assert.AreEqual((byte)100, aim.Threshold);
        Assert.AreEqual(0, aim.Delay);
        Assert.AreEqual(0, aim.LayerIndex);
        Assert.IsTrue(aim.UseSourceLightbar);
        AssertMatches(LoadReference("Edge Expo"), aim);

        Assert.AreEqual("Edge Hipfire", hip.SourceProfile);
        Assert.AreEqual(DS4Controls.R2, hip.Trigger);
        Assert.AreEqual((byte)100, hip.Threshold);
        Assert.AreEqual(100, hip.Delay);
        Assert.AreEqual(1, hip.LayerIndex);
        Assert.IsTrue(hip.UseSourceLightbar);
        Assert.AreEqual("Edge Linear", hip.BaseProfile);
        AssertMatches(LoadReference("Edge Hipfire"), hip);
        Assert.AreEqual(1.2, hip.RSSens);

        // First held layer in file order (delay is Task 5.2's runtime rule).
        Assert.IsNull(layers.FirstHeld(100, 100));
        Assert.AreSame(aim, layers.FirstHeld(101, 0));
        Assert.AreSame(hip, layers.FirstHeld(0, 101));
        Assert.AreSame(aim, layers.FirstHeld(101, 255));
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void MissingSecondSourceLeavesFirstLayerPublished()
    {
        SaveSource("Edge Expo");
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("Edge Hipfire"));
        Load("Edge Linear");

        AimLayerSet layers = Layers();
        Assert.AreEqual(1, layers.Count);
        Assert.AreEqual("Edge Expo", layers[0].SourceProfile);
        Assert.AreEqual(0, layers[0].LayerIndex);
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "\"Edge Hipfire\"");
        StringAssert.Contains(warnings[0], "(R2)");
        StringAssert.StartsWith(warnings[0], "Aim layer of profile \"Edge Linear\"");

        // Re-applying logs nothing new; creating the source adds the layer
        // after the first, which keeps its object.
        Load("Edge Linear");
        Assert.AreEqual(1, warnings.Count);
        AimLayerStickSettings aim = Layers()[0];
        SaveHipfire();
        layers = Layers();
        Assert.AreEqual(2, layers.Count);
        Assert.AreSame(aim, layers[0]);
        Assert.AreEqual("Edge Hipfire", layers[1].SourceProfile);
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void MissingFirstSourceLeavesSecondLayerPublished()
    {
        SaveHipfire();
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("Edge Hipfire"));
        Load("Edge Linear");

        AimLayerSet layers = Layers();
        Assert.AreEqual(1, layers.Count);
        Assert.AreEqual("Edge Hipfire", layers[0].SourceProfile);
        Assert.AreEqual(1, layers[0].LayerIndex);
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "\"Edge Expo\"");
        StringAssert.Contains(warnings[0], "(L2)");

        // Creating the missing source puts it back in front.
        AimLayerStickSettings hip = layers[0];
        SaveSource("Edge Expo");
        layers = Layers();
        Assert.AreEqual(2, layers.Count);
        Assert.AreEqual("Edge Expo", layers[0].SourceProfile);
        Assert.AreSame(hip, layers[1]);
    }

    [TestMethod]
    public void BothSourcesMissingPublishesNothingAndWarnsPerLayer()
    {
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("Edge Hipfire"));
        Load("Edge Linear");
        Assert.IsNull(Layers());
        Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
        Load("Edge Linear");
        Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void SavingHipfireSourceRebuildsOnlyR2Layer()
    {
        LoadUserSetup();
        AimLayerSet before = Layers();
        AimLayerStickSettings aim = before[0], hip = before[1];

        BackingStore editor = LoadReference("Edge Hipfire");
        editor.RSSens[Slot] = 0.9;
        Assert.IsTrue(editor.SaveProfileNew(Slot, "Edge Hipfire"));

        AimLayerSet after = Layers();
        Assert.AreNotSame(before, after);
        Assert.AreEqual(2, after.Count);
        Assert.AreSame(aim, after[0], "The L2 layer must not be rebuilt.");
        Assert.AreNotSame(hip, after[1]);
        Assert.AreEqual(0.9, after[1].RSSens);
        Assert.AreEqual(1, after[1].LayerIndex);
        Assert.AreEqual(100, after[1].Delay);
        AssertMatches(LoadReference("Edge Hipfire"), after[1]);
        Assert.AreEqual(1.2, hip.RSSens, "The old object must not change.");

        // And the other way round.
        SaveSource("Edge Expo", sens: 2.4);
        AimLayerSet third = Layers();
        Assert.AreEqual(2.4, third[0].RSSens);
        Assert.AreSame(after[1], third[1], "The R2 layer must not be rebuilt.");
    }

    [TestMethod]
    public void TwoLayersBorrowingOneSourceBothRebuild()
    {
        SaveSource("Edge Expo");
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("edge expo"));
        Load("Edge Linear");
        Assert.AreEqual(2, Layers().Count);

        SaveSource("Edge Expo", sens: 2.6);
        Assert.AreEqual(2.6, Layers()[0].RSSens);
        Assert.AreEqual(2.6, Layers()[1].RSSens);
    }

    [TestMethod]
    public void SelfBorrowIsSkippedPerLayer()
    {
        SaveHipfire();
        SaveLayers("Edge Linear", L2Layer("edge linear"), R2Layer("Edge Hipfire"));
        Load("Edge Linear");

        AimLayerSet layers = Layers();
        Assert.AreEqual(1, layers.Count);
        Assert.AreEqual("Edge Hipfire", layers[0].SourceProfile);
        Assert.AreEqual(1, layers[0].LayerIndex);
        Assert.AreEqual(0, warnings.Count, string.Join("\n", warnings));

        // Saving the base itself does not turn the self layer on.
        SaveLayers("Edge Linear", L2Layer("edge linear"), R2Layer("Edge Hipfire"));
        Assert.AreEqual(1, Layers().Count);
    }

    [TestMethod]
    public void DisabledLayerIsSkippedOthersPublished()
    {
        SaveSource("Edge Expo");
        SaveHipfire();
        SaveLayers("Edge Linear",
            new AimLayerConfig(false, DS4Controls.L2, 100, 0, "Edge Expo", true),
            R2Layer("Edge Hipfire"));
        Load("Edge Linear");
        Assert.AreEqual(1, Layers().Count);
        Assert.AreEqual("Edge Hipfire", Layers()[0].SourceProfile);
        // Saving the disabled layer's source does not turn it on.
        SaveSource("Edge Expo");
        Assert.AreEqual(1, Layers().Count);
    }

    [TestMethod]
    public void FifthBlockIsIgnoredWithOneWarning()
    {
        SaveSource("Edge Expo");
        SaveHipfire();
        static string Block(string trigger, string source) =>
            $"<AimLayer><Enabled>True</Enabled><Trigger>{trigger}</Trigger>" +
            $"<SourceProfile>{source}</SourceProfile></AimLayer>";
        string body = Block("L2", "Edge Expo") + Block("R2", "Edge Hipfire") +
            Block("L2", "Edge Hipfire") + Block("R2", "Edge Expo") +
            Block("R2", "Fifth Source");
        File.WriteAllText(Path.Combine(folder, "Profiles", "Five Layers.xml"),
            $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");

        Load("Five Layers");
        AimLayerSet layers = Layers();
        Assert.AreEqual(4, layers.Count);
        for (int i = 0; i < 4; i++)
            Assert.AreEqual(i, layers[i].LayerIndex);
        Assert.AreEqual("Edge Expo", layers[3].SourceProfile);
        // One warning about the extra block; the fifth source is never read
        // (it does not exist, so reading it would warn too).
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        StringAssert.Contains(warnings[0], "Five Layers");
        StringAssert.Contains(warnings[0], "ignored");
        Assert.IsFalse(warnings[0].Contains("Fifth Source"));

        // Re-applying the same profile does not log it again.
        Load("Five Layers");
        Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
    }

    [TestMethod]
    public void ProfileWithoutAimLayerClearsAllLayers()
    {
        LoadUserSetup();
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));
        Assert.AreEqual(2, Layers().Count);

        Assert.IsTrue(Global.LoadTempProfile(Slot, "Plain", false,
            UninitializedService(), xinputChange: false));
        Assert.IsNull(Layers());
        // Saving either source while the temp profile is active must not
        // bring a layer back.
        SaveHipfire(sens: 0.5);
        SaveSource("Edge Expo");
        Assert.IsNull(Layers());

        Load("Edge Linear");
        Assert.AreEqual(2, Layers().Count);
        Assert.AreEqual(0.5, Layers()[1].RSSens);

        Load("Plain");
        Assert.IsNull(Layers());
    }

    [TestMethod]
    public void SuspendAndResumeKeepEveryLayer()
    {
        LoadUserSetup();
        AimLayerStickSettings aim = Layers()[0];

        AimLayerState.Suspend(Slot);
        Assert.IsNull(Layers());
        SaveHipfire(sens: 0.7);
        Assert.IsNull(Layers());

        AimLayerState.Resume(Slot);
        Assert.AreEqual(2, Layers().Count);
        Assert.AreSame(aim, Layers()[0]);
        Assert.AreEqual(0.7, Layers()[1].RSSens);
    }

    [TestMethod]
    public void SaveBetweenPrepareAndApplyRebuildsTheLayers()
    {
        SaveSource("Edge Expo");
        SaveHipfire();
        SaveLayers("Edge Linear", L2Layer("Edge Expo"), R2Layer("Edge Hipfire"));
        string path = Path.Combine(folder, "Profiles", "Edge Linear.xml");
        Assert.IsTrue(PreparedProfileLoad.TryPrepare(path, Slot, out var prepared, out _, out _));
        Assert.AreEqual(2, prepared.AimLayer.Requests.Count);
        Assert.AreEqual(1.2, prepared.AimLayer.Built[1].RSSens);

        SaveHipfire(sens: 0.8);
        Assert.IsTrue(live.ApplyPreparedProfileNew(prepared, false, UninitializedService(),
            out _, xinputChange: false, postLoad: false));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Layers()?[1].RSSens != 0.8 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.AreEqual(2, Layers().Count);
        Assert.AreEqual(0.8, Layers()[1].RSSens);
        Assert.AreEqual(1.7, Layers()[0].RSSens);
    }

    [TestMethod]
    public void RebuildLosingToNewerApplyDoesNotPublishEitherLayer()
    {
        LoadUserSetup();
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));

        int calls = 0;
        AimLayerState.RebuildBuiltForTests = device =>
        {
            if (device == Slot && calls++ == 0)
                Load("Plain");
        };
        SaveHipfire(sens: 0.6);
        Assert.AreEqual(1, calls);
        Assert.IsNull(Layers());
    }
}
