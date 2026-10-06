using DS4Windows;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4WindowsTests;

/// <summary>
/// Task 6.3: soft deadzone, game curve and activation ramp in
/// GyroMouseStickMath. Defaults are pinned by GyroMouseStickMathTests.
/// </summary>
[TestClass]
public class GyroMouseStickFeatureTests
{
    private const double Dt = 0.004;

    private static GyroMouseStickInfo Plain(int maxZone) => new()
    {
        deadZone = 0,
        maxZone = maxZone,
        antiDeadX = 0.0,
        antiDeadY = 0.0,
    };

    private static GyroMouseStickFilterState NewState(GyroMouseStickInfo settings) =>
        new(new OneEuroFilter(settings.minCutoff, settings.beta),
            new OneEuroFilter(settings.minCutoff, settings.beta));

    private static GyroMouseStickOutput Compute(int yaw, int pitch,
        GyroMouseStickInfo settings, ref GyroMouseStickFilterState state) =>
        GyroMouseStickMath.Compute(yaw, pitch, 0, Dt, 0, settings,
            default(Switch2GyroTriggerModifierResult), ref state);

    private static int OffX(GyroMouseStickOutput o) => Math.Abs(o.AxisX - 128);
    private static int OffY(GyroMouseStickOutput o) => Math.Abs(o.AxisY - 128);

    [TestMethod]
    public void SoftDeadzoneIsContinuousAndMonotonic()
    {
        var settings = Plain(400);
        settings.softDeadZone = 200;
        var plain = Plain(400);
        var state = NewState(settings);
        var plainState = NewState(plain);
        int previous = 0;
        for (int yaw = 0; yaw <= 400; yaw++)
        {
            int off = OffX(Compute(yaw, 0, settings, ref state));
            Assert.IsTrue(off >= previous, $"not monotonic at {yaw}");
            Assert.IsTrue(off - previous <= 1, $"jump at {yaw}");
            previous = off;
            // At and above the threshold the soft deadzone does nothing.
            if (yaw >= 200)
            {
                Assert.AreEqual(OffX(Compute(yaw, 0, plain, ref plainState)), off);
            }
        }

        // Tightening: half the threshold (100 counts) gives 50 counts (100^2 / 200).
        Assert.AreEqual((byte)(50 / 400.0 * 127 + 128.0),
            Compute(100, 0, settings, ref state).AxisX);
        Assert.AreEqual((byte)128, Compute(1, 0, settings, ref state).AxisX);
    }

    [TestMethod]
    public void SoftDeadzoneIsRadial()
    {
        var settings = Plain(400);
        settings.softDeadZone = 400;
        var state = NewState(settings);
        // Length 300 of 400: scaled by 0.75 to (135, 180) counts.
        var o = Compute(180, 240, settings, ref state);
        Assert.AreEqual((byte)(135 / 400.0 * 127 + 128.0), o.AxisX);
        Assert.AreEqual((byte)(180 / 400.0 * -128 + 128.0), o.AxisY);
        Assert.AreEqual(0.75, OffX(o) / (double)OffY(o), 0.03);

        // Diagonal directions keep the same ratio along the whole ray.
        for (int s = 40; s <= 100; s += 10)
        {
            var d = Compute(3 * s, 4 * s, settings, ref state);
            Assert.AreEqual(0.75, OffX(d) / (double)OffY(d), 0.06, $"step {s}");
        }
    }

    [TestMethod]
    public void GameCurveTwoGivesSquareRootOfLength()
    {
        var settings = Plain(1000);
        settings.gameCurve = 2.0;
        var state = NewState(settings);
        Assert.AreEqual((byte)(0.5 * 127 + 128.0),
            Compute(250, 0, settings, ref state).AxisX);

        // Radial: (0.3, 0.4) has length 0.5, which becomes sqrt(0.5).
        var o = Compute(300, 400, settings, ref state);
        double scale = Math.Sqrt(0.5) / 0.5;
        Assert.AreEqual((byte)(0.3 * scale * 127 + 128.0), o.AxisX);
        Assert.AreEqual((byte)(0.4 * scale * -128 + 128.0), o.AxisY);

        int previous = 0;
        for (int yaw = 0; yaw <= 1000; yaw += 5)
        {
            int off = OffX(Compute(yaw, 0, settings, ref state));
            Assert.IsTrue(off >= previous, $"not monotonic at {yaw}");
            previous = off;
        }
    }

    [TestMethod]
    public void ActivationRampEasesInAndRestarts()
    {
        var settings = Plain(1000);
        settings.activationRamp = 100;
        var plain = Plain(1000);
        var state = NewState(settings);
        var plainState = NewState(plain);
        byte full = Compute(800, 0, plain, ref plainState).AxisX;

        for (int round = 0; round < 2; round++)
        {
            // Report n has gain n * 4 ms / 100 ms: 0 first, 1 from report 25.
            Assert.AreEqual((byte)128, Compute(800, 0, settings, ref state).AxisX);
            int previous = 0;
            for (int n = 1; n < 25; n++)
            {
                var o = Compute(800, 0, settings, ref state);
                Assert.AreEqual((byte)(0.8 * (n * 4.0 / 100.0) * 127 + 128.0),
                    o.AxisX, 1, $"report {n}");
                Assert.IsTrue(OffX(o) > previous);
                previous = OffX(o);
            }

            Assert.AreEqual(full, Compute(800, 0, settings, ref state).AxisX);
            Assert.AreEqual(full, Compute(800, 0, settings, ref state).AxisX);

            // Gyro switching off resets the ramp.
            GyroMouseStickMath.Reset(Dt, settings, ref state);
        }
    }

    [TestMethod]
    public void WarmComputeWithNewFeaturesAllocatesNothing()
    {
        var settings = new GyroMouseStickInfo
        {
            useSmoothing = true,
            smoothingMethod = GyroMouseStickInfo.SmoothingMethod.OneEuro,
            jitterCompensation = true,
            softDeadZone = 150,
            gameCurve = 2.5,
            activationRamp = 60,
            maxOutputEnabled = true,
            maxOutput = 90,
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
}
