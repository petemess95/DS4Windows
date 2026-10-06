using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

/// <summary>
/// Per-profile aim layer settings (Task 3.1): defaults, reset, and the
/// &lt;AimLayer&gt; profile XML block. All files live in a temp folder.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AimLayerProfileTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    // The block documented for hand-editing (docs/aim-layer.md).
    internal const string SampleAimLayerXml =
        "<AimLayer>\r\n" +
        "  <Enabled>True</Enabled>\r\n" +
        "  <Trigger>L2</Trigger>\r\n" +
        "  <Threshold>100</Threshold>\r\n" +
        "  <SourceProfile>Edge Expo</SourceProfile>\r\n" +
        "  <UseSourceLightbar>True</UseSourceLightbar>\r\n" +
        "</AimLayer>";

    private string folder;
    private string oldAppDataPath;

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-aim-layer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
        // SaveProfileNew and migration saves write under appdatapath; keep
        // them away from the user's real profiles.
        oldAppDataPath = Global.appdatapath;
        Global.appdatapath = folder;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Global.appdatapath = oldAppDataPath;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static ControlService UninitializedService() =>
        (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));

    private string WriteProfile(string name, string body)
    {
        string path = Path.Combine(folder, "Profiles", $"{name}.xml");
        File.WriteAllText(path, $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");
        return path;
    }

    private static void Load(BackingStore store, string path)
    {
        Assert.IsTrue(store.LoadProfileNew(Slot, false, UninitializedService(),
            out bool changed, path, xinputChange: false, postLoad: false));
        Assert.IsTrue(changed);
    }

    private static void SetNonDefaults(BackingStore store)
    {
        store.aimLayerEnabled[Slot] = true;
        store.aimLayerTrigger[Slot] = DS4Controls.R2;
        store.aimLayerThreshold[Slot] = 180;
        store.aimLayerSourceProfile[Slot] = "Edge Expo";
        store.aimLayerUseSourceLightbar[Slot] = true;
    }

    private static void AssertDefaults(BackingStore store)
    {
        Assert.IsFalse(store.aimLayerEnabled[Slot]);
        Assert.AreEqual(DS4Controls.L2, store.aimLayerTrigger[Slot]);
        Assert.AreEqual((byte)100, store.aimLayerThreshold[Slot]);
        Assert.AreEqual(string.Empty, store.aimLayerSourceProfile[Slot]);
        Assert.IsFalse(store.aimLayerUseSourceLightbar[Slot]);
    }

    [TestMethod]
    public void NewStoreHasLayerOffWithDefaults()
    {
        var store = new BackingStore();
        AssertDefaults(store);
        Assert.AreEqual(DS4Controls.L2, BackingStore.DEFAULT_AIM_LAYER_TRIGGER);
        Assert.AreEqual((byte)100, BackingStore.DEFAULT_AIM_LAYER_THRESHOLD);
    }

    [TestMethod]
    public void SaveThenLoadKeepsEveryField()
    {
        var source = new BackingStore();
        SetNonDefaults(source);
        Assert.IsTrue(source.SaveProfileNew(Slot, "AimBase"));

        string path = Path.Combine(folder, "Profiles", "AimBase.xml");
        string xml = File.ReadAllText(path);
        StringAssert.Contains(xml, "<AimLayer>");
        StringAssert.Contains(xml, "<Enabled>True</Enabled>");
        StringAssert.Contains(xml, "<Trigger>R2</Trigger>");
        StringAssert.Contains(xml, "<Threshold>180</Threshold>");
        StringAssert.Contains(xml, "<SourceProfile>Edge Expo</SourceProfile>");
        StringAssert.Contains(xml, "<UseSourceLightbar>True</UseSourceLightbar>");

        var loaded = new BackingStore();
        Load(loaded, path);
        Assert.IsTrue(loaded.aimLayerEnabled[Slot]);
        Assert.AreEqual(DS4Controls.R2, loaded.aimLayerTrigger[Slot]);
        Assert.AreEqual((byte)180, loaded.aimLayerThreshold[Slot]);
        Assert.AreEqual("Edge Expo", loaded.aimLayerSourceProfile[Slot]);
        Assert.IsTrue(loaded.aimLayerUseSourceLightbar[Slot]);
    }

    [TestMethod]
    public void DisabledLayerWithSourceIsStillSaved()
    {
        var source = new BackingStore();
        source.aimLayerSourceProfile[Slot] = "Edge Expo";
        Assert.IsTrue(source.SaveProfileNew(Slot, "Disabled"));

        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Disabled.xml"));
        Assert.IsFalse(loaded.aimLayerEnabled[Slot]);
        Assert.AreEqual("Edge Expo", loaded.aimLayerSourceProfile[Slot]);
    }

    [TestMethod]
    public void UnconfiguredLayerIsOmittedFromSavedProfile()
    {
        Assert.IsTrue(new BackingStore().SaveProfileNew(Slot, "Plain"));
        string xml = File.ReadAllText(Path.Combine(folder, "Profiles", "Plain.xml"));
        Assert.IsFalse(xml.Contains("<AimLayer"), xml);
    }

    [TestMethod]
    public void ProfileWithoutAimLayerLoadsWithLayerOff()
    {
        string path = WriteProfile("NoLayer", "<RumbleBoost>77</RumbleBoost>");
        var store = new BackingStore();
        Load(store, path);
        Assert.AreEqual(77, (int)store.rumble[Slot]);
        AssertDefaults(store);
    }

    [TestMethod]
    public void ProfileWithoutAimLayerClearsPreviouslyLoadedLayer()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("WithLayer", SampleAimLayerXml));
        Assert.IsTrue(store.aimLayerEnabled[Slot]);
        Assert.AreEqual("Edge Expo", store.aimLayerSourceProfile[Slot]);

        Load(store, WriteProfile("NoLayer", "<RumbleBoost>77</RumbleBoost>"));
        AssertDefaults(store);
    }

    [TestMethod]
    public void MapToWithoutAimLayerClearsSlotEvenWithoutReset()
    {
        // MapTo alone (as used for the validation store) must not rely on
        // ResetProfile having run first.
        var store = new BackingStore();
        SetNonDefaults(store);
        new ProfileDTO { DeviceIndex = Slot }.MapTo(store);
        AssertDefaults(store);
    }

    [TestMethod]
    public void ResetProfileResetsAimLayer()
    {
        var store = new BackingStore();
        SetNonDefaults(store);
        MethodInfo reset = typeof(BackingStore).GetMethod("ResetProfile",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(reset);
        reset.Invoke(store, new object[] { Slot });
        AssertDefaults(store);
    }

    [TestMethod]
    public void DocumentedSampleBlockLoads()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Sample", SampleAimLayerXml));
        Assert.IsTrue(store.aimLayerEnabled[Slot]);
        Assert.AreEqual(DS4Controls.L2, store.aimLayerTrigger[Slot]);
        Assert.AreEqual((byte)100, store.aimLayerThreshold[Slot]);
        Assert.AreEqual("Edge Expo", store.aimLayerSourceProfile[Slot]);
        Assert.IsTrue(store.aimLayerUseSourceLightbar[Slot]);
    }

    [DataTestMethod]
    [DataRow("R2", DS4Controls.R2)]
    [DataRow("r2", DS4Controls.R2)]
    [DataRow(" L2 ", DS4Controls.L2)]
    [DataRow("Cross", DS4Controls.L2)]
    [DataRow("RYNeg", DS4Controls.L2)]
    [DataRow("NotAControl", DS4Controls.L2)]
    [DataRow("", DS4Controls.L2)]
    public void UnsupportedTriggerFallsBackToL2(string text, DS4Controls expected)
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Trigger",
            $"<AimLayer><Enabled>true</Enabled><Trigger>{text}</Trigger></AimLayer>"));
        Assert.IsTrue(store.aimLayerEnabled[Slot]);
        Assert.AreEqual(expected, store.aimLayerTrigger[Slot]);
    }

    [DataTestMethod]
    [DataRow("0", (byte)0)]
    [DataRow("255", (byte)255)]
    [DataRow("256", (byte)100)]
    [DataRow("-1", (byte)100)]
    [DataRow("high", (byte)100)]
    public void InvalidThresholdFallsBackToDefault(string text, byte expected)
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Threshold",
            $"<AimLayer><Threshold>{text}</Threshold></AimLayer>"));
        Assert.AreEqual(expected, store.aimLayerThreshold[Slot]);
    }

    [TestMethod]
    public void SourceProfileWhitespaceIsTrimmed()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Spaces",
            "<AimLayer><SourceProfile>\r\n    Edge Expo\r\n  </SourceProfile></AimLayer>"));
        Assert.AreEqual("Edge Expo", store.aimLayerSourceProfile[Slot]);
    }
}
