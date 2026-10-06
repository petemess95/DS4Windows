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
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(true, DS4Controls.R2, 180, 0, "Edge Expo", true),
        };
    }

    // No layer configured (the slot's list is empty).
    private static void AssertDefaults(BackingStore store)
    {
        Assert.AreEqual(0, store.aimLayers[Slot].Count);
    }

    // The only block of a single-block profile.
    private static AimLayerConfig Single(BackingStore store)
    {
        Assert.AreEqual(1, store.aimLayers[Slot].Count);
        return store.aimLayers[Slot][0];
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
        AimLayerConfig layer = Single(loaded);
        Assert.IsTrue(layer.Enabled);
        Assert.AreEqual(DS4Controls.R2, layer.Trigger);
        Assert.AreEqual((byte)180, layer.Threshold);
        Assert.AreEqual("Edge Expo", layer.SourceProfile);
        Assert.IsTrue(layer.UseSourceLightbar);
        Assert.AreEqual(0, layer.Delay);
    }

    [TestMethod]
    public void DisabledLayerWithSourceIsStillSaved()
    {
        var source = new BackingStore();
        source.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(false, DS4Controls.L2, 100, 0, "Edge Expo", false),
        };
        Assert.IsTrue(source.SaveProfileNew(Slot, "Disabled"));

        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Disabled.xml"));
        Assert.IsFalse(Single(loaded).Enabled);
        Assert.AreEqual("Edge Expo", Single(loaded).SourceProfile);
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
        Assert.IsTrue(Single(store).Enabled);
        Assert.AreEqual("Edge Expo", Single(store).SourceProfile);

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
        AimLayerConfig layer = Single(store);
        Assert.IsTrue(layer.Enabled);
        Assert.AreEqual(DS4Controls.L2, layer.Trigger);
        Assert.AreEqual((byte)100, layer.Threshold);
        Assert.AreEqual(0, layer.Delay);
        Assert.AreEqual("Edge Expo", layer.SourceProfile);
        Assert.IsTrue(layer.UseSourceLightbar);
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
        Assert.IsTrue(Single(store).Enabled);
        Assert.AreEqual(expected, Single(store).Trigger);
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
        Assert.AreEqual(expected, Single(store).Threshold);
    }

    [TestMethod]
    public void SourceProfileWhitespaceIsTrimmed()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Spaces",
            "<AimLayer><SourceProfile>\r\n    Edge Expo\r\n  </SourceProfile></AimLayer>"));
        Assert.AreEqual("Edge Expo", Single(store).SourceProfile);
    }

    // ---- Several layers (Task 5.1) ----

    // The user's two-layer setup: L2 -> Edge Expo first, R2 -> Edge Hipfire
    // after a 100 ms hold.
    internal const string SampleTwoAimLayersXml =
        SampleAimLayerXml + "\r\n" +
        "<AimLayer>\r\n" +
        "  <Enabled>True</Enabled>\r\n" +
        "  <Trigger>R2</Trigger>\r\n" +
        "  <Threshold>100</Threshold>\r\n" +
        "  <Delay>100</Delay>\r\n" +
        "  <SourceProfile>Edge Hipfire</SourceProfile>\r\n" +
        "  <UseSourceLightbar>True</UseSourceLightbar>\r\n" +
        "</AimLayer>";

    private static string Block(string body) => $"<AimLayer>{body}</AimLayer>";

    private string SavedXml(BackingStore store, string name)
    {
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
        return File.ReadAllText(Path.Combine(folder, "Profiles", $"{name}.xml"));
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [TestMethod]
    public void SampleBlockSavesAsBefore()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Sample", SampleAimLayerXml));
        string xml = SavedXml(store, "Sample Saved");

        // Same element sequence the single-block format always wrote; no
        // <Delay> because it is 0.
        string expected =
            "<AimLayer>\r\n" +
            "    <Enabled>True</Enabled>\r\n" +
            "    <Trigger>L2</Trigger>\r\n" +
            "    <Threshold>100</Threshold>\r\n" +
            "    <SourceProfile>Edge Expo</SourceProfile>\r\n" +
            "    <UseSourceLightbar>True</UseSourceLightbar>\r\n" +
            "  </AimLayer>";
        StringAssert.Contains(xml, expected);
        Assert.AreEqual(1, Count(xml, "<AimLayer>"), xml);
        Assert.AreEqual(0, Count(xml, "<Delay>"), xml);
        // The block sits where the single property always wrote it.
        Assert.IsTrue(xml.IndexOf("<AimLayer>", StringComparison.Ordinal) >
            xml.IndexOf("<OutputContDevice>", StringComparison.Ordinal), xml);

        // Load-save is stable.
        var again = new BackingStore();
        Load(again, Path.Combine(folder, "Profiles", "Sample Saved.xml"));
        Assert.AreEqual(xml, SavedXml(again, "Sample Saved"));
    }

    [TestMethod]
    public void TwoBlocksRoundTripInOrder()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Two", SampleTwoAimLayersXml));
        AssertTwoLayers(store);

        string xml = SavedXml(store, "Two Saved");
        Assert.AreEqual(2, Count(xml, "<AimLayer>"), xml);
        Assert.AreEqual(1, Count(xml, "<Delay>100</Delay>"), xml);
        int expo = xml.IndexOf("Edge Expo", StringComparison.Ordinal);
        int delay = xml.IndexOf("<Delay>", StringComparison.Ordinal);
        int hipfire = xml.IndexOf("Edge Hipfire", StringComparison.Ordinal);
        Assert.IsTrue(expo >= 0 && expo < delay && delay < hipfire, xml);
        // Delay follows Threshold inside its block.
        Assert.IsTrue(xml.LastIndexOf("<Threshold>", delay, StringComparison.Ordinal) > expo, xml);

        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Two Saved.xml"));
        AssertTwoLayers(loaded);
        Assert.AreEqual(xml, SavedXml(loaded, "Two Saved"));
    }

    private static void AssertTwoLayers(BackingStore store)
    {
        Assert.AreEqual(2, store.aimLayers[Slot].Count);
        AimLayerConfig aim = store.aimLayers[Slot][0], hip = store.aimLayers[Slot][1];
        Assert.IsTrue(aim.Enabled);
        Assert.AreEqual(DS4Controls.L2, aim.Trigger);
        Assert.AreEqual((byte)100, aim.Threshold);
        Assert.AreEqual(0, aim.Delay);
        Assert.AreEqual("Edge Expo", aim.SourceProfile);
        Assert.IsTrue(aim.UseSourceLightbar);
        Assert.IsTrue(hip.Enabled);
        Assert.AreEqual(DS4Controls.R2, hip.Trigger);
        Assert.AreEqual((byte)100, hip.Threshold);
        Assert.AreEqual(100, hip.Delay);
        Assert.AreEqual("Edge Hipfire", hip.SourceProfile);
        Assert.IsTrue(hip.UseSourceLightbar);
    }

    [TestMethod]
    public void TwoLayersSavedFromStoreKeepOrder()
    {
        var store = new BackingStore();
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(true, DS4Controls.R2, 40, 250, "B Second", false),
            new AimLayerConfig(true, DS4Controls.L2, 100, 0, "A First", true),
        };
        string xml = SavedXml(store, "Order");
        Assert.IsTrue(xml.IndexOf("B Second", StringComparison.Ordinal) <
            xml.IndexOf("A First", StringComparison.Ordinal), xml);

        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Order.xml"));
        Assert.AreEqual(2, loaded.aimLayers[Slot].Count);
        Assert.AreEqual("B Second", loaded.aimLayers[Slot][0].SourceProfile);
        Assert.AreEqual(250, loaded.aimLayers[Slot][0].Delay);
        Assert.AreEqual((byte)40, loaded.aimLayers[Slot][0].Threshold);
        Assert.AreEqual("A First", loaded.aimLayers[Slot][1].SourceProfile);
    }

    [TestMethod]
    public void UnconfiguredBlockIsDroppedOnSaveOthersKept()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Gap", Block("<Enabled>True</Enabled><SourceProfile>X</SourceProfile>") +
            Block("") + Block("<Trigger>R2</Trigger>")));
        Assert.AreEqual(3, store.aimLayers[Slot].Count);
        Assert.IsTrue(store.aimLayers[Slot][1].IsDefault);

        string xml = SavedXml(store, "Gap Saved");
        Assert.AreEqual(2, Count(xml, "<AimLayer>"), xml);
        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Gap Saved.xml"));
        Assert.AreEqual("X", loaded.aimLayers[Slot][0].SourceProfile);
        Assert.AreEqual(DS4Controls.R2, loaded.aimLayers[Slot][1].Trigger);
    }

    [TestMethod]
    public void DelayDefaultsToZero()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("NoDelay", Block("<Enabled>True</Enabled>")));
        Assert.AreEqual(0, Single(store).Delay);
        Assert.AreEqual(0, BackingStore.DEFAULT_AIM_LAYER_DELAY);
        Assert.AreEqual(1000, BackingStore.MAX_AIM_LAYER_DELAY);
    }

    [DataTestMethod]
    [DataRow("0", 0)]
    [DataRow("1", 1)]
    [DataRow("100", 100)]
    [DataRow(" 150 ", 150)]
    [DataRow("1000", 1000)]
    [DataRow("1001", 0)]
    [DataRow("-1", 0)]
    [DataRow("99999999999", 0)]
    [DataRow("1.5", 0)]
    [DataRow("slow", 0)]
    [DataRow("", 0)]
    public void DelayParsesWholeMillisecondsOrFallsBackToZero(string text, int expected)
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Delay",
            Block($"<Enabled>True</Enabled><Delay>{text}</Delay><SourceProfile>Edge Expo</SourceProfile>")));
        AimLayerConfig layer = Single(store);
        Assert.AreEqual(expected, layer.Delay);
        // A bad delay never costs the rest of the block.
        Assert.IsTrue(layer.Enabled);
        Assert.AreEqual("Edge Expo", layer.SourceProfile);
    }

    [DataTestMethod]
    [DataRow(-5, 0)]
    [DataRow(0, 0)]
    [DataRow(1000, 1000)]
    [DataRow(1001, 0)]
    public void ConfigNormalisesDelay(int delay, int expected)
    {
        Assert.AreEqual(expected,
            new AimLayerConfig(true, DS4Controls.L2, 100, delay, "X", false).Delay);
    }

    [TestMethod]
    public void NonZeroDelayAloneIsSaved()
    {
        var store = new BackingStore();
        store.aimLayers[Slot] = new[]
        {
            new AimLayerConfig(false, DS4Controls.L2, 100, 30, "", false),
        };
        string xml = SavedXml(store, "DelayOnly");
        StringAssert.Contains(xml, "<Delay>30</Delay>");
    }

    [TestMethod]
    public void BlocksPastTheFourthAreIgnored()
    {
        string body = "";
        for (int i = 1; i <= 5; i++)
            body += Block($"<Enabled>True</Enabled><SourceProfile>Source {i}</SourceProfile>");
        var store = new BackingStore();
        Load(store, WriteProfile("Five", body));

        Assert.AreEqual(BackingStore.MAX_AIM_LAYERS, store.aimLayers[Slot].Count);
        for (int i = 0; i < 4; i++)
            Assert.AreEqual($"Source {i + 1}", store.aimLayers[Slot][i].SourceProfile);

        string xml = SavedXml(store, "Five Saved");
        Assert.AreEqual(4, Count(xml, "<AimLayer>"), xml);
        Assert.IsFalse(xml.Contains("Source 5"), xml);
    }

    [TestMethod]
    public void ProfileWithoutAimLayerClearsSeveralLayers()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Two", SampleTwoAimLayersXml));
        Assert.AreEqual(2, store.aimLayers[Slot].Count);

        Load(store, WriteProfile("NoLayer", "<RumbleBoost>77</RumbleBoost>"));
        AssertDefaults(store);
    }
}

