using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

/// <summary>
/// Measures where the cost of PreparedProfileLoad.TryPrepare goes. Each stage
/// mirrors TryPrepare's source exactly. No timing assertions (yet); use
/// MeasureTryPrepare for later acceptance checks.
/// Run: dotnet test ... --filter "FullyQualifiedName~ProfileSwitchCostBenchmark"
/// Exclude from normal runs: --filter "TestCategory!=Benchmark"
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Benchmark")]
public class ProfileSwitchCostBenchmark
{
    internal const int WarmupRuns = 5;
    internal const int MeasuredRuns = 40;

    public TestContext TestContext { get; set; }

    private string folder;
    private string linearPath;
    private string customPath;

    [TestInitialize]
    public void Setup()
    {
        folder = Path.Combine(Path.GetTempPath(), $"ds4w-switch-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        linearPath = Path.Combine(folder, "Hip.xml");
        customPath = Path.Combine(folder, "Aim.xml");
        File.WriteAllText(linearPath, CreateSavedProfileXml("linear", ""), Encoding.UTF8);
        File.WriteAllText(customPath, CreateSavedProfileXml("custom", "0.72, 0.26, 1.00, 1.00"), Encoding.UTF8);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    /// <summary>
    /// Builds a full profile the same way BackingStore.SaveProfileNew does
    /// (default store, MapFrom, serializer with overrides), with the RS curve set.
    /// </summary>
    internal static string CreateSavedProfileXml(string rsCurveMode, string rsCurveCustom)
    {
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var strWriter = new Utf8StringWriter();
        using (XmlWriter xmlWriter = XmlWriter.Create(strWriter,
            new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true }))
        {
            xmlWriter.WriteComment(" DS4Windows Configuration Data. benchmark ");
            xmlWriter.WriteWhitespace("\r\n\r\n");
            var dto = new ProfileDTO { DeviceIndex = 0 };
            dto.MapFrom(new BackingStore());
            dto.RSOutputCurveMode = rsCurveMode;
            dto.RSOutputCurveCustom = rsCurveCustom;
            serializer.Serialize(xmlWriter, dto,
                new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty }));
        }
        return strWriter.ToString();
    }

    internal readonly record struct StageResult(string Name, double WallMs, double CpuMs);

    internal static StageResult Measure(string name, Action action)
    {
        for (int i = 0; i < WarmupRuns; i++)
            action();
        Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        TimeSpan cpuStart = proc.TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < MeasuredRuns; i++)
            action();
        double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds / MeasuredRuns;
        proc.Refresh();
        double cpu = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds / MeasuredRuns;
        return new StageResult(name, wall, cpu);
    }

    /// <summary>
    /// Average warm TryPrepare cost and assembly growth over a batch. Intended
    /// for later timing-based acceptance tests.
    /// </summary>
    internal static (double AvgWallMs, double AvgCpuMs, int AssembliesBefore, int AssembliesAfter)
        MeasureTryPrepare(string[] paths)
    {
        int n = 0;
        void Prepare()
        {
            string path = paths[n++ % paths.Length];
            bool ok = PreparedProfileLoad.TryPrepare(path, 0, out _, out _, out string error);
            if (!ok)
                throw new AssertFailedException($"TryPrepare failed for {path}: {error}");
        }

        for (int i = 0; i < WarmupRuns; i++)
            Prepare();
        int before = AppDomain.CurrentDomain.GetAssemblies().Length;
        StageResult r = Measure("TryPrepare", Prepare);
        int after = AppDomain.CurrentDomain.GetAssemblies().Length;
        return (r.WallMs, r.CpuMs, before, after);
    }

    // Stage a: identical to the TryPrepare block before deserialization.
    private static string ReadAndMigrate(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var migration = new ProfileMigration(stream);
        try
        {
            XmlReader original = migration.ProfileReader;
            if (original == null || original.MoveToContent() != XmlNodeType.Element ||
                original.LocalName != "DS4Windows" || original.NamespaceURI.Length != 0)
                throw new XmlException("Expected a DS4Windows profile document.");
            if (migration.RequiresMigration())
                migration.Migrate();
            return migration.CurrentMigrationText;
        }
        finally
        {
            migration.Close();
        }
    }

    [TestMethod]
    public void StageBreakdown()
    {
        string[] paths = { linearPath, customPath };
        int n = 0;
        string NextPath() => paths[n++ % paths.Length];

        string[] xmls = paths.Select(ReadAndMigrate).ToArray();
        var reused = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        ProfileDTO Deserialize(XmlSerializer s, string xml)
        {
            using var reader = new StringReader(xml);
            var dto = s.Deserialize(reader) as ProfileDTO;
            Assert.IsNotNull(dto);
            dto.DeviceIndex = 0;
            return dto;
        }
        ProfileDTO[] dtos = xmls.Select(x => Deserialize(reused, x)).ToArray();
        Assert.AreEqual("custom", dtos[1].RSOutputCurveMode);
        Assert.AreEqual("0.72, 0.26, 1.00, 1.00", dtos[1].RSOutputCurveCustom);

        var results = new List<StageResult>();
        results.Add(Measure("a. read + ProfileMigration", () => ReadAndMigrate(NextPath())));

        int asmBeforeCtor = AppDomain.CurrentDomain.GetAssemblies().Length;
        results.Add(Measure("b. new XmlSerializer(ProfileDTO, overrides)",
            () => _ = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides())));
        int asmAfterCtor = AppDomain.CurrentDomain.GetAssemblies().Length;

        results.Add(Measure("b0. ProfileDTO.GetAttributeOverrides() only",
            () => _ = ProfileDTO.GetAttributeOverrides()));
        results.Add(Measure("c. Deserialize (reused serializer)",
            () => Deserialize(reused, xmls[n++ % xmls.Length])));
        results.Add(Measure("bc. new XmlSerializer + first Deserialize",
            () => Deserialize(new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides()),
                xmls[n++ % xmls.Length])));
        results.Add(Measure("d1. CreateProfileValidationStore()",
            () => _ = BackingStore.CreateProfileValidationStore()));
        BackingStore store = BackingStore.CreateProfileValidationStore();
        results.Add(Measure("d2. MapTo (reused validation store)",
            () => dtos[n++ % dtos.Length].MapTo(store)));
        results.Add(Measure("d. MapTo(CreateProfileValidationStore())",
            () => dtos[n++ % dtos.Length].MapTo(BackingStore.CreateProfileValidationStore())));

        var full = MeasureTryPrepare(paths);
        results.Add(new StageResult("e. full TryPrepare (warm)", full.AvgWallMs, full.AvgCpuMs));

        var sb = new StringBuilder();
        sb.AppendLine($"ProfileSwitchCostBenchmark: warmup {WarmupRuns}, avg of {MeasuredRuns} runs, profile {new FileInfo(linearPath).Length} bytes");
        sb.AppendLine($"{"Stage",-46} {"wall ms",9} {"cpu ms",9}");
        foreach (StageResult r in results)
            sb.AppendLine($"{r.Name,-46} {r.WallMs,9:F3} {r.CpuMs,9:F3}");
        int batch = WarmupRuns + MeasuredRuns;
        sb.AppendLine($"Assemblies around serializer ctor batch ({batch} ctors): {asmBeforeCtor} -> {asmAfterCtor} " +
            $"(+{(asmAfterCtor - asmBeforeCtor) / (double)batch:F2}/ctor)");
        sb.AppendLine($"Assemblies around TryPrepare batch ({batch} calls): {full.AssembliesBefore} -> {full.AssembliesAfter} " +
            $"(+{(full.AssembliesAfter - full.AssembliesBefore) / (double)batch:F2}/call)");
        Console.WriteLine(sb.ToString());
        TestContext.WriteLine(sb.ToString());
    }
}
