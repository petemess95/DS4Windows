using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4WindowsTests;

/// <summary>
/// Task 6.4: HighRes and Dither precision for gyro mouse-joystick. "Exact"
/// is the stick coordinate (0..255, 128 = centre) from the same steps done
/// in doubles with no truncation, computed here independently of the
/// product code. Legacy is pinned by GyroMouseStickMathTests.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GyroMouseStickPrecisionTests
{
    private const double Dt = 0.004;
    private const GyroMouseStickInfo.OutputStick Right = GyroMouseStickInfo.OutputStick.RightStick;
    private const GyroMouseStickInfo.PrecisionMode Legacy = GyroMouseStickInfo.PrecisionMode.Legacy;
    private const GyroMouseStickInfo.PrecisionMode HighRes = GyroMouseStickInfo.PrecisionMode.HighRes;
    private const GyroMouseStickInfo.PrecisionMode Dither = GyroMouseStickInfo.PrecisionMode.Dither;

    private delegate void GyroProducer(SixAxisEventArgs args,
        in Switch2GyroTriggerModifierResult modifier);

    private static GyroMouseStickInfo Defaults(GyroMouseStickInfo.PrecisionMode precision) =>
        new() { precision = precision };

    private static GyroMouseStickFilterState NewState(GyroMouseStickInfo settings) =>
        new(new OneEuroFilter(settings.minCutoff, settings.beta),
            new OneEuroFilter(settings.minCutoff, settings.beta));

    private static GyroMouseStickOutput Compute(int yaw, int pitch,
        GyroMouseStickInfo settings, ref GyroMouseStickFilterState state) =>
        GyroMouseStickMath.Compute(yaw, pitch, 0, Dt, 0, settings,
            default(Switch2GyroTriggerModifierResult), ref state);

    // Hard deadzone (scaled by the direction), clamp to maxZone, ratio,
    // anti-deadzone, direction, all in doubles. Settings without smoothing,
    // jitter, vertical scale, max output or inversion.
    private static (double X, double Y) Exact(int yaw, int pitch, GyroMouseStickInfo s)
    {
        double dx = yaw, dy = -pitch;
        double angle = Math.Atan2(-dy, dx);
        double nx = Math.Abs(Math.Cos(angle)), ny = Math.Abs(Math.Sin(angle));
        return (Axis(dx, nx, s.antiDeadX), Axis(dy, ny, s.antiDeadY));

        double Axis(double d, double n, double anti)
        {
            double deadzone = n * s.deadZone;
            if (Math.Abs(d) <= deadzone) return 128.0;
            double ratio = Math.Min(Math.Abs(d) - deadzone, s.maxZone) / s.maxZone;
            double a = anti * n;
            return ((1.0 - a) * ratio + a) * (d >= 0 ? 127 : -128) + 128.0;
        }
    }

    private static IEnumerable<(int Yaw, int Pitch)> Grid()
    {
        for (int yaw = -1100; yaw <= 1100; yaw += 37)
            for (int pitch = -1100; pitch <= 1100; pitch += 41)
                yield return (yaw, pitch);
    }

    [TestMethod]
    public void HighResIsWithinOneOver256OfTheExactValueAndMirrorsTheRoundedByte()
    {
        var settings = Defaults(HighRes);
        var state = NewState(settings);
        int fractional = 0, betterThanLegacy = 0;
        foreach (var (yaw, pitch) in Grid())
        {
            var o = Compute(yaw, pitch, settings, ref state);
            var exact = Exact(yaw, pitch, settings);
            Check(o.MappedX, o.AxisX, exact.X);
            Check(o.MappedY, o.AxisY, exact.Y);

            var legacyState = NewState(settings);
            var legacy = Compute(yaw, pitch, Defaults(Legacy), ref legacyState);
            Assert.IsFalse(legacy.MappedX.IsHighResolution);
            Assert.AreEqual(DS4MappedStickAxis.FromLegacy(legacy.AxisX), legacy.MappedX);
            if (Math.Abs(legacy.AxisX - exact.X) > Math.Abs(o.MappedX.ProfileCoordinate - exact.X) + 0.25)
                betterThanLegacy++;
        }
        Assert.IsTrue(fractional > 1000, "HighRes must keep fractions.");
        Assert.IsTrue(betterThanLegacy > 1000, "HighRes must beat the byte path.");

        void Check(in DS4MappedStickAxis axis, byte mirror, double exact)
        {
            Assert.IsTrue(axis.IsHighResolution);
            Assert.AreEqual(exact, axis.ProfileCoordinate, 1.0 / 256.0);
            Assert.AreEqual((byte)Math.Round(axis.ProfileCoordinate, MidpointRounding.AwayFromZero), mirror);
            if (axis.ProfileCoordinate != Math.Floor(axis.ProfileCoordinate)) fractional++;
        }
    }

    [TestMethod]
    public void HighResSmoothingKeepsFractions()
    {
        foreach (var method in new[] { GyroMouseStickInfo.SmoothingMethod.WeightedAverage,
            GyroMouseStickInfo.SmoothingMethod.OneEuro })
        {
            var settings = Defaults(HighRes);
            settings.useSmoothing = true;
            settings.smoothingMethod = method;
            var state = NewState(settings);
            // A steady input settles on the exact value (Legacy would floor it).
            GyroMouseStickOutput o = default;
            for (int i = 0; i < 2000; i++) o = Compute(123, -77, settings, ref state);
            var exact = Exact(123, -77, settings);
            Assert.AreEqual(exact.X, o.MappedX.ProfileCoordinate, 1.0 / 256.0, method.ToString());
            Assert.AreEqual(exact.Y, o.MappedY.ProfileCoordinate, 1.0 / 256.0, method.ToString());
        }
    }

    [TestMethod]
    public void DitherAveragesToTheExactValueAndStaysWithinOneByte()
    {
        var settings = Defaults(Dither);
        var highRes = Defaults(HighRes);
        foreach (var (yaw, pitch) in Grid().Where((_, i) => i % 7 == 0))
        {
            var state = NewState(settings);
            var highState = NewState(highRes);
            var exact = Exact(yaw, pitch, settings);
            Assert.AreEqual(exact.X, Compute(yaw, pitch, highRes, ref highState).MappedX.ProfileCoordinate, 1e-9);
            double sumX = 0, sumY = 0;
            for (int i = 0; i < 1000; i++)
            {
                var o = Compute(yaw, pitch, settings, ref state);
                Assert.IsFalse(o.MappedX.IsHighResolution);
                Assert.IsTrue(Math.Abs(o.AxisX - exact.X) <= 1.0, $"{yaw},{pitch} X {o.AxisX} vs {exact.X}");
                Assert.IsTrue(Math.Abs(o.AxisY - exact.Y) <= 1.0, $"{yaw},{pitch} Y {o.AxisY} vs {exact.Y}");
                sumX += o.AxisX;
                sumY += o.AxisY;
            }
            Assert.AreEqual(exact.X, sumX / 1000, 0.01, $"{yaw},{pitch} X");
            Assert.AreEqual(exact.Y, sumY / 1000, 0.01, $"{yaw},{pitch} Y");
        }
    }

    [TestMethod]
    public void DitherTracksHighResThroughSmoothingJitterAndVerticalScale()
    {
        var rng = new Random(6400);
        foreach (var method in new[] { GyroMouseStickInfo.SmoothingMethod.OneEuro,
            GyroMouseStickInfo.SmoothingMethod.WeightedAverage })
        {
            var dither = Dither3(Dither);
            var high = Dither3(HighRes);
            var ditherState = NewState(dither);
            var highState = NewState(high);
            double sumOut = 0, sumExact = 0;
            for (int i = 0; i < 1000; i++)
            {
                // A slow sweep with noise, as a hand would make.
                int yaw = (int)(400 * Math.Sin(i * 0.01)) + rng.Next(-20, 21);
                int pitch = (int)(250 * Math.Cos(i * 0.013)) + rng.Next(-20, 21);
                var d = Compute(yaw, pitch, dither, ref ditherState);
                var h = Compute(yaw, pitch, high, ref highState);
                double exact = h.MappedX.ProfileCoordinate;
                Assert.IsTrue(Math.Abs(d.AxisX - exact) <= 1.0, $"{method} report {i}");
                Assert.IsTrue(Math.Abs(d.AxisY - h.MappedY.ProfileCoordinate) <= 1.0, $"{method} report {i}");
                sumOut += d.AxisX;
                sumExact += exact;
            }
            Assert.AreEqual(sumExact / 1000, sumOut / 1000, 0.01, method.ToString());

            GyroMouseStickInfo Dither3(GyroMouseStickInfo.PrecisionMode mode)
            {
                var s = Defaults(mode);
                s.useSmoothing = true;
                s.smoothingMethod = method;
                s.jitterCompensation = true;
                s.vertScale = 70;
                return s;
            }
        }
    }

    [TestMethod]
    public void DitherDoesNotDriftWhenGyroStopsAndResetClearsTheCarry()
    {
        var settings = Defaults(Dither);
        settings.useSmoothing = true;
        settings.smoothingMethod = GyroMouseStickInfo.SmoothingMethod.WeightedAverage;
        var state = NewState(settings);
        for (int i = 0; i < 37; i++) Compute(201, -113, settings, ref state);
        Assert.IsTrue(state.DitherErrorX != 0.0 || state.DitherErrorY != 0.0,
            "The test needs a carried error.");

        // Still (and below the deadzone): exactly centre, every report.
        for (int i = 0; i < 500; i++)
        {
            var o = Compute(i % 3 == 0 ? 0 : 10, i % 2 == 0 ? 0 : -10, settings, ref state);
            if (i >= 3) // the smoothing ring empties after three reports
            {
                Assert.AreEqual((byte)128, o.AxisX, $"report {i}");
                Assert.AreEqual((byte)128, o.AxisY, $"report {i}");
                Assert.AreEqual(0.0, state.DitherErrorX);
                Assert.AreEqual(0.0, state.DitherErrorY);
            }
        }

        // Gyro switching off clears the carry, so re-activation starts fresh.
        for (int i = 0; i < 37; i++) Compute(201, -113, settings, ref state);
        GyroMouseStickMath.Reset(Dt, settings, ref state);
        Assert.AreEqual(0.0, state.DitherErrorX);
        Assert.AreEqual(0.0, state.DitherErrorY);
    }

    [TestMethod]
    public void DitherAxisNeverCrossesCentreAndClampsTheEnds()
    {
        foreach (double carry in new[] { -0.5, -0.25, 0.0, 0.25, 0.5 })
        {
            double error = carry;
            Assert.IsTrue(GyroMouseStickMath.DitherAxis(128.01, ref error) >= 128);
            error = carry;
            Assert.IsTrue(GyroMouseStickMath.DitherAxis(127.99, ref error) <= 128);
            foreach (double end in new[] { 0.0, 0.25, 254.75, 255.0 })
            {
                error = carry;
                Assert.IsTrue(Math.Abs(GyroMouseStickMath.DitherAxis(end, ref error) - end) <= 1.0);
                Assert.IsTrue(Math.Abs(error) <= 0.5);
            }
        }
    }

    [DataTestMethod]
    [DataRow(HighRes)]
    [DataRow(Dither)]
    public void WarmHighResAndDitherComputeAllocateNothing(GyroMouseStickInfo.PrecisionMode mode)
    {
        var settings = new GyroMouseStickInfo
        {
            precision = mode,
            useSmoothing = true,
            smoothingMethod = GyroMouseStickInfo.SmoothingMethod.OneEuro,
            jitterCompensation = true,
            softDeadZone = 150,
            gameCurve = 2.5,
            activationRamp = 60,
        };
        var state = NewState(settings);
        int sink = 0;
        for (int i = 0; i < 2000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Step(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreNotEqual(0, sink);

        void Step(int i)
        {
            int yaw = (i * 37) % 4001 - 2000, pitch = (i * 53) % 4001 - 2000;
            if (i % 100 < 90)
                sink += Compute(yaw, pitch, settings, ref state).AxisX;
            else
                GyroMouseStickMath.Reset(Dt, settings, ref state);
        }
    }

    // --- PostMapStickData: the typed (high-resolution) submit. ---

    [TestMethod]
    public void TypedSubmitKeepsPrecisionThroughMergeApplyAndCurrentGyro()
    {
        var data = new Mapping.PostMapStickData();
        long epoch = data.CaptureEpoch();
        Assert.IsTrue(data.TrySubmit(epoch, Right, true, true, Axis(143.875), Axis(30.25), true));
        var state = new DS4State();
        data.ApplyTo(state);
        Assert.AreEqual(143.875, state.RXAxis.ProfileCoordinate);
        Assert.AreEqual(30.25, state.RYAxis.ProfileCoordinate);
        Assert.IsTrue(state.RXAxis.IsHighResolution);

        // Re-application (TempMouseJoystick's path) uses the precise value.
        state = new DS4State();
        Assert.IsTrue(data.TryApplyCurrentGyro(epoch, state, Right, true, true));
        Assert.AreEqual(143.875, state.RXAxis.ProfileCoordinate);
        Assert.AreEqual(30.25, state.RYAxis.ProfileCoordinate);
        data.Reset();

        // Stronger per axis, compared at full resolution in both directions.
        data.TrySubmit(epoch, Right, true, true, Axis(200.375), Axis(60.625), true);
        data.TrySubmit(epoch, Right, true, true, 200, 61, false);
        state = new DS4State();
        data.ApplyTo(state);
        Assert.AreEqual(200.375, state.RXAxis.ProfileCoordinate);
        Assert.AreEqual(60.625, state.RYAxis.ProfileCoordinate);
        data.TrySubmit(epoch, Right, true, true, Axis(200.375), Axis(60.625), true);
        data.TrySubmit(epoch, Right, true, true, 201, 60, false);
        state = new DS4State();
        data.ApplyTo(state);
        Assert.AreEqual(201.0, state.RXAxis.ProfileCoordinate);
        Assert.IsFalse(state.RXAxis.IsHighResolution);
        Assert.AreEqual(60.0, state.RYAxis.ProfileCoordinate);

        // A disabled axis neutralises the current gyro on that axis.
        data.TrySubmit(epoch, Right, true, false, Axis(150.5), Axis(10.5), true);
        data.Reset();
        state = new DS4State();
        data.TryApplyCurrentGyro(epoch, state, Right, true, true);
        Assert.AreEqual(150.5, state.RXAxis.ProfileCoordinate);
        Assert.AreEqual(128.0, state.RYAxis.ProfileCoordinate);
    }

    [TestMethod]
    public void TypedSubmitObeysEpochAndResetRules()
    {
        var data = new Mapping.PostMapStickData();
        long old = data.CaptureEpoch();
        Assert.IsTrue(data.TrySubmit(old, Right, true, true, Axis(140.5), Axis(110.5), true));
        data.RequestReset();

        // Reset retires the precise current gyro, not just the pending vector.
        var state = new DS4State();
        Assert.IsTrue(data.TryApplyCurrentGyro(data.CaptureEpoch(), state, Right, true, true));
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), state.RXAxis);
        Assert.AreEqual(DS4MappedStickAxis.FromLegacy(128), state.RYAxis);

        // Old-epoch typed work is never admitted, before or after a successor.
        Assert.IsFalse(data.TrySubmit(old, Right, true, true, Axis(255.0), Axis(0.0), true));
        Assert.IsTrue(data.TrySubmit(data.CaptureEpoch(), Right, true, true, Axis(170.25), Axis(80.75), true));
        Assert.IsFalse(data.TrySubmit(old, Right, true, true, Axis(255.0), Axis(0.0), true));
        Assert.IsFalse(data.TryClearGyro(old));
        state = new DS4State();
        Assert.IsFalse(data.TryApplyCurrentGyro(old, state, Right, true, true));
        Assert.AreEqual((byte)128, state.RX);
        data.ApplyTo(state);
        Assert.AreEqual(170.25, state.RXAxis.ProfileCoordinate);
        Assert.AreEqual(80.75, state.RYAxis.ProfileCoordinate);

        // TryClearGyro centres the current gyro at the current epoch.
        Assert.IsTrue(data.TryClearGyro(data.CaptureEpoch()));
        state = new DS4State();
        data.TryApplyCurrentGyro(data.CaptureEpoch(), state, Right, true, true);
        Assert.AreEqual((byte)128, state.RX);
        Assert.AreEqual((byte)128, state.RY);
    }

    [TestMethod]
    public void ConcurrentConsumptionNeverSeesHalfOfAPreciseVector()
    {
        var data = new Mapping.PostMapStickData();
        using var start = new ManualResetEventSlim();
        var x = Axis(240.125);
        var y = Axis(16.875);
        var producer = Task.Run(() =>
        {
            if (!start.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            for (int i = 0; i < 10000; i++)
                data.TrySubmit(data.CaptureEpoch(), Right, true, true, x, y, false);
        });
        start.Set();
        for (int i = 0; i < 10000; i++)
        {
            var state = new DS4State();
            data.ApplyTo(state);
            Assert.IsTrue((state.RX == 128 && state.RY == 128) ||
                (state.RXAxis.Equals(x) && state.RYAxis.Equals(y)));
        }
        Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void TypedAdmissionConsumptionAndResetAllocateNothingAfterWarmup()
    {
        var data = new Mapping.PostMapStickData();
        var state = new DS4State();
        var x = Axis(150.25);
        var y = Axis(100.75);
        for (int i = 0; i < 10000; i++) Cycle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Cycle();
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        void Cycle()
        {
            data.RequestReset();
            long epoch = data.CaptureEpoch();
            data.TrySubmit(epoch, Right, true, true, x, y, true);
            data.TrySubmit(epoch, Right, true, true, 180, 70, false);
            data.TryApplyCurrentGyro(epoch, state, Right, true, true);
            data.ApplyTo(state);
            data.TryClearGyro(epoch);
        }
    }

    // --- The real producer: Mouse -> PostMapStickData -> output. ---

    [TestMethod]
    public void RealHighResGyroReachesXboxAs16BitAndTempMouseJoystickKeepsIt()
    {
        using var fixture = new ProducerFixture();
        fixture.GyroInfo.precision = HighRes;
        // maxZone 128, no deadzones: yaw 16 is exactly 1/8 of full right,
        // pitch -24 is 3/16 of full down; both fall between bytes.
        fixture.Gyro(Args(16, 0, -24), default);
        const double exactX = 128.0 + 127.0 * 16 / 128;
        const double exactY = 128.0 + 127.0 * 24 / 128;
        var state = new DS4State();
        fixture.Data.ApplyTo(state);
        Assert.AreEqual(exactX, state.RXAxis.ProfileCoordinate, 1.0 / 256.0);
        Assert.AreEqual(exactY, state.RYAxis.ProfileCoordinate, 1.0 / 256.0);
        Assert.AreEqual((byte)144, Mapping.gyroStickX[fixture.Slot], "mirror is the rounded value");
        Assert.AreEqual((byte)152, Mapping.gyroStickY[fixture.Slot]);

        var xbox = ViiperStatePacketBuilder.BuildXbox360State(state, -1);
        Assert.AreEqual((short)Math.Round(32767.0 * 16 / 128), xbox.RightStickX);
        Assert.AreEqual((short)-Math.Round(32767.0 * 24 / 128), xbox.RightStickY);

        // The Legacy byte (143) reaches Xbox output a step short.
        var legacyXbox = ViiperStatePacketBuilder.BuildXbox360State(
            new DS4State { RX = 143, RY = 151 }, -1);
        Assert.IsTrue(xbox.RightStickX - legacyXbox.RightStickX > 200);

        // Re-applied without a new report, the gyro value stays precise.
        var temp = new DS4State();
        Mapping.TempMouseJoystick(fixture.Slot, temp);
        Assert.AreEqual(state.RXAxis, temp.RXAxis);
        Assert.AreEqual(state.RYAxis, temp.RYAxis);
        Assert.AreEqual(xbox.RightStickX, ViiperStatePacketBuilder.BuildXbox360State(temp, -1).RightStickX);
    }

    [TestMethod]
    public void RealDitherGyroSubmitsBytesThatAverageToTheExactValue()
    {
        using var fixture = new ProducerFixture();
        fixture.GyroInfo.precision = Dither;
        const double exactX = 128.0 + 127.0 * 16 / 128;
        double sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            fixture.Gyro(Args(16, 0, 0), default);
            var state = new DS4State();
            fixture.Data.ApplyTo(state);
            Assert.IsFalse(state.RXAxis.IsHighResolution);
            Assert.IsTrue(state.RX == 143 || state.RX == 144, $"report {i}: {state.RX}");
            Assert.AreEqual(state.RX, Mapping.gyroStickX[fixture.Slot]);
            sum += state.RX;
        }
        Assert.AreEqual(exactX, sum / 1000, 0.01);

        // Gyro stops: centre, every report.
        for (int i = 0; i < 100; i++)
        {
            fixture.Gyro(Args(0, 0, 0), default);
            var state = new DS4State();
            fixture.Data.ApplyTo(state);
            Assert.AreEqual((byte)128, state.RX);
            Assert.AreEqual((byte)128, Mapping.gyroStickX[fixture.Slot]);
        }
    }

    [DataTestMethod]
    [DataRow(HighRes)]
    [DataRow(Dither)]
    public void RealGyroProducerAllocatesNothingAfterWarmup(GyroMouseStickInfo.PrecisionMode mode)
    {
        using var fixture = new ProducerFixture();
        fixture.GyroInfo.precision = mode;
        var moving = Args(37, 0, -23);
        var still = Args(0, 0, 0);
        var state = new DS4State();
        for (int i = 0; i < 2000; i++) Report(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 22000; i++) Report(i);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        void Report(int i)
        {
            fixture.Gyro(i % 50 < 40 ? moving : still, default);
            fixture.Data.ApplyTo(state);
        }
    }

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var result));
        return result;
    }

    private static SixAxisEventArgs Args(int yaw, int roll, int pitch) => new(DateTime.UnixEpoch,
        new SixAxis(0, 0, 0, 0, 0, 0, Dt)
        { gyroYawFull = yaw, gyroRollFull = roll, gyroPitchFull = pitch });

    // Modelled on PostMapStickConcurrentPublicationTests.ProducerFixture.
    private sealed class ProducerFixture : IDisposable
    {
        private readonly BackingStore previousStore = Global.store;
        private readonly Mapping.PostMapStickData previousData;
        private readonly byte previousX, previousY;
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic);
        internal int Slot => 1;
        internal Mapping.PostMapStickData Data { get; }
        internal GyroMouseStickInfo GyroInfo { get; }
        internal GyroProducer Gyro { get; }

        internal ProducerFixture()
        {
            previousData = Mapping.mapStickActionData[Slot];
            previousX = Mapping.gyroStickX[Slot]; previousY = Mapping.gyroStickY[Slot];
            StoreField.SetValue(null, new BackingStore());
            Mapping.mapStickActionData[Slot] = Data = new Mapping.PostMapStickData();
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1, Switch2Transport.Usb,
                out var runtime, out _));
            var mouse = new Mouse(Slot, runtime);
            Global.GyroOutputMode[Slot] = GyroOutMode.MouseJoystick;
            Global.GyroMouseStickHorizontalAxis[Slot] = 0;
            GyroInfo = Global.GetGyroMouseStickInfo(Slot);
            GyroInfo.deadZone = 0; GyroInfo.maxZone = 128;
            GyroInfo.antiDeadX = GyroInfo.antiDeadY = 0;
            GyroInfo.useSmoothing = false; GyroInfo.jitterCompensation = false;
            GyroInfo.outputStick = Right;
            GyroInfo.outputStickDir = GyroMouseStickInfo.OutputStickAxes.XY;
            Gyro = typeof(Mouse).GetMethod("SixMouseStick", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate<GyroProducer>(mouse);
        }

        public void Dispose()
        {
            Mapping.mapStickActionData[Slot] = previousData;
            Mapping.gyroStickX[Slot] = previousX; Mapping.gyroStickY[Slot] = previousY;
            StoreField.SetValue(null, previousStore);
        }
    }
}
