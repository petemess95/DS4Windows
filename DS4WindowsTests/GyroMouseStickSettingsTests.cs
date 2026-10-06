using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DS4Windows;

namespace DS4WindowsTests;

/// <summary>
/// Task 6.2: the five new per-profile mouse-joystick settings in
/// GyroMouseStickInfo, their XML elements and reset. All files live in a
/// temp folder; the Edge fixtures are copies, never the user's real files.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GyroMouseStickSettingsTests
{
    private const int Slot = Global.TEST_PROFILE_INDEX;

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "TestData", "Profiles");

    private string folder;
    private string oldAppDataPath;

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-gyro-mstick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "Profiles"));
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

    private static void Load(BackingStore store, string path)
    {
        Assert.IsTrue(store.LoadProfileNew(Slot, false, UninitializedService(),
            out _, path, xinputChange: false, postLoad: false));
    }

    private string SavedXml(BackingStore store, string name)
    {
        Assert.IsTrue(store.SaveProfileNew(Slot, name));
        return File.ReadAllText(Path.Combine(folder, "Profiles", $"{name}.xml"));
    }

    // The save header carries DateTime.Now; blank it so saves compare.
    private static string StripTimestamp(string xml) =>
        Regex.Replace(xml, @"<!-- DS4Windows Configuration Data\. [^>]*-->",
            "<!-- DS4Windows Configuration Data. -->");

    // The user's three Edge profiles (copies) load and save byte-identical
    // to the saves produced by the build before Task 6.2 (HEAD 61d190c),
    // stored as "<name>.saved-61d190c.xml" next to the fixtures.
    [DataTestMethod]
    [DataRow("Edge Linear")]
    [DataRow("Edge Expo")]
    [DataRow("Edge Hipfire")]
    public void EdgeProfilesSaveByteIdenticalToPreviousBuild(string name)
    {
        string input = Path.Combine(folder, $"{name}-input.xml");
        File.Copy(Path.Combine(FixtureDir, $"{name}.xml"), input);
        var store = new BackingStore();
        Load(store, input);
        string saved = StripTimestamp(SavedXml(store, name));

        string expectedPath = Path.Combine(FixtureDir, $"{name}.saved-61d190c.xml");
        string regenDir = Environment.GetEnvironmentVariable("DS4W_REGEN_GYRO_FIXTURES");
        if (!string.IsNullOrEmpty(regenDir))
        {
            File.WriteAllText(Path.Combine(regenDir, $"{name}.saved-61d190c.xml"), saved);
            Assert.Inconclusive("Regenerated expected save.");
        }

        Assert.AreEqual(File.ReadAllText(expectedPath), saved);
        Assert.IsFalse(saved.Contains("GyroMouseStickSoftDeadZone"));
    }

    private string WriteProfile(string name, string body)
    {
        string path = Path.Combine(folder, "Profiles", $"{name}.xml");
        File.WriteAllText(path, $"<DS4Windows config_version=\"5\">{body}</DS4Windows>");
        return path;
    }

    private static GyroMouseStickInfo Info(BackingStore store) => store.gyroMStickInfo[Slot];

    private static void SetNonDefaults(GyroMouseStickInfo info)
    {
        info.softDeadZone = 250;
        info.gameCurve = 2.5;
        info.activationRamp = 120;
        info.precision = GyroMouseStickInfo.PrecisionMode.Dither;
        info.blend = GyroMouseStickInfo.BlendMode.Add;
    }

    private static void AssertDefaults(GyroMouseStickInfo info)
    {
        Assert.AreEqual(0, info.softDeadZone);
        Assert.AreEqual(1.0, info.gameCurve);
        Assert.AreEqual(0, info.activationRamp);
        Assert.AreEqual(GyroMouseStickInfo.PrecisionMode.Legacy, info.precision);
        Assert.AreEqual(GyroMouseStickInfo.BlendMode.Stronger, info.blend);
    }

    [TestMethod]
    public void NewInfoAndResetGiveDefaults()
    {
        var info = new GyroMouseStickInfo();
        AssertDefaults(info);
        SetNonDefaults(info);
        info.Reset();
        AssertDefaults(info);
    }

    [TestMethod]
    public void SaveThenLoadKeepsEveryElement()
    {
        var source = new BackingStore();
        SetNonDefaults(Info(source));
        string xml = SavedXml(source, "AllSet");
        StringAssert.Contains(xml, "<GyroMouseStickSoftDeadZone>250</GyroMouseStickSoftDeadZone>");
        StringAssert.Contains(xml, "<GyroMouseStickGameCurve>2.5</GyroMouseStickGameCurve>");
        StringAssert.Contains(xml, "<GyroMouseStickActivationRamp>120</GyroMouseStickActivationRamp>");
        StringAssert.Contains(xml, "<GyroMouseStickPrecision>Dither</GyroMouseStickPrecision>");
        StringAssert.Contains(xml, "<GyroMouseStickBlend>Add</GyroMouseStickBlend>");

        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "AllSet.xml"));
        GyroMouseStickInfo info = Info(loaded);
        Assert.AreEqual(250, info.softDeadZone);
        Assert.AreEqual(2.5, info.gameCurve);
        Assert.AreEqual(120, info.activationRamp);
        Assert.AreEqual(GyroMouseStickInfo.PrecisionMode.Dither, info.precision);
        Assert.AreEqual(GyroMouseStickInfo.BlendMode.Add, info.blend);
    }

    [DataTestMethod]
    [DataRow("GyroMouseStickPrecision", "HighRes")]
    [DataRow("GyroMouseStickPrecision", "Dither")]
    [DataRow("GyroMouseStickSoftDeadZone", "400")]
    [DataRow("GyroMouseStickSoftDeadZone", "1")]
    [DataRow("GyroMouseStickGameCurve", "4")]
    [DataRow("GyroMouseStickGameCurve", "1.0001")]
    [DataRow("GyroMouseStickActivationRamp", "500")]
    [DataRow("GyroMouseStickBlend", "Add")]
    public void EachElementRoundTrips(string element, string value)
    {
        var store = new BackingStore();
        Load(store, WriteProfile("In", $"<{element}>{value}</{element}>"));
        string xml = SavedXml(store, "Out");
        StringAssert.Contains(xml, $"<{element}>{value}</{element}>");
    }

    [DataTestMethod]
    [DataRow("GyroMouseStickSoftDeadZone", "0")]
    [DataRow("GyroMouseStickSoftDeadZone", "-1")]
    [DataRow("GyroMouseStickSoftDeadZone", "401")]
    [DataRow("GyroMouseStickSoftDeadZone", "abc")]
    [DataRow("GyroMouseStickSoftDeadZone", "")]
    [DataRow("GyroMouseStickGameCurve", "1.0")]
    [DataRow("GyroMouseStickGameCurve", "0.5")]
    [DataRow("GyroMouseStickGameCurve", "4.01")]
    [DataRow("GyroMouseStickGameCurve", "NaN")]
    [DataRow("GyroMouseStickGameCurve", "Infinity")]
    [DataRow("GyroMouseStickGameCurve", "two")]
    [DataRow("GyroMouseStickActivationRamp", "0")]
    [DataRow("GyroMouseStickActivationRamp", "-5")]
    [DataRow("GyroMouseStickActivationRamp", "501")]
    [DataRow("GyroMouseStickActivationRamp", "1.5")]
    [DataRow("GyroMouseStickPrecision", "Legacy")]
    [DataRow("GyroMouseStickPrecision", "1")]
    [DataRow("GyroMouseStickPrecision", "99")]
    [DataRow("GyroMouseStickPrecision", "Fancy")]
    [DataRow("GyroMouseStickBlend", "Stronger")]
    [DataRow("GyroMouseStickBlend", "1")]
    [DataRow("GyroMouseStickBlend", "Multiply")]
    public void DefaultBadOrOutOfRangeLoadsDefaultAndIsNotWritten(string element, string value)
    {
        var store = new BackingStore();
        SetNonDefaults(Info(store));
        Load(store, WriteProfile("Bad", $"<{element}>{value}</{element}>"));
        AssertDefaults(Info(store));
        Assert.IsFalse(SavedXml(store, "BadOut").Contains(element));
    }

    [TestMethod]
    public void ProfileWithoutElementsLoadsDefaults()
    {
        var store = new BackingStore();
        Load(store, WriteProfile("Plain", "<GyroMouseStickDeadZone>30</GyroMouseStickDeadZone>"));
        AssertDefaults(Info(store));
        string xml = SavedXml(store, "PlainOut");
        foreach (string element in new[] { "GyroMouseStickSoftDeadZone", "GyroMouseStickGameCurve",
            "GyroMouseStickActivationRamp", "GyroMouseStickPrecision", "GyroMouseStickBlend" })
            Assert.IsFalse(xml.Contains(element), element);
    }

    [TestMethod]
    public void LoadingAnotherProfileResetsNewFields()
    {
        var store = new BackingStore();
        SetNonDefaults(Info(store));
        SavedXml(store, "Tuned");
        var loaded = new BackingStore();
        Load(loaded, Path.Combine(folder, "Profiles", "Tuned.xml"));
        Assert.AreEqual(GyroMouseStickInfo.BlendMode.Add, Info(loaded).blend);
        Load(loaded, WriteProfile("Other", "<GyroMouseStickDeadZone>30</GyroMouseStickDeadZone>"));
        AssertDefaults(Info(loaded));
    }

    [TestMethod]
    public void BadValueInFullEdgeProfileDoesNotFailLoad()
    {
        string text = File.ReadAllText(Path.Combine(FixtureDir, "Edge Expo.xml")).Replace(
            "</DS4Windows>",
            "<GyroMouseStickGameCurve>junk</GyroMouseStickGameCurve>" +
            "<GyroMouseStickPrecision>99</GyroMouseStickPrecision></DS4Windows>");
        string path = Path.Combine(folder, "edge-bad.xml");
        File.WriteAllText(path, text);
        var store = new BackingStore();
        Load(store, path);
        AssertDefaults(Info(store));
    }
}
