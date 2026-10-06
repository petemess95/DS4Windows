using System;
using System.Runtime.CompilerServices;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4Windows
{
    // Three-entry ring for the weighted-average smoothing. Inline so the
    // state struct copies by value and holds no arrays. Doubles so HighRes
    // keeps fractions; Legacy only ever stores whole numbers, which a
    // double holds exactly, so its sums are unchanged.
    [InlineArray(GyroMouseStickFilterState.SmoothBufferLength)]
    internal struct GyroMouseStickSmoothBuffer
    {
        private double element0;
    }

    /// <summary>
    /// Per-device filter and buffer state carried between gyro mouse-joystick
    /// reports. Owned by <see cref="Mouse"/> and passed by ref; only the motion
    /// thread touches it.
    /// </summary>
    internal struct GyroMouseStickFilterState
    {
        internal const int SmoothBufferLength = 3;

        internal GyroMouseStickFilterState(OneEuroFilter filterX,
            OneEuroFilter filterY)
        {
            FilterX = filterX;
            FilterY = filterY;
            SmoothX = default;
            SmoothY = default;
            SmoothTail = 0;
            RampElapsedMs = 0.0;
            DitherErrorX = 0.0;
            DitherErrorY = 0.0;
        }

        // Borrowed from Mouse's filter pair: the profile's MinCutoff/Beta
        // change events are wired to those objects.
        internal OneEuroFilter FilterX;
        internal OneEuroFilter FilterY;
        internal GyroMouseStickSmoothBuffer SmoothX;
        internal GyroMouseStickSmoothBuffer SmoothY;
        // Always 1..3 after the first write, as in the original ring.
        internal int SmoothTail;
        // Summed report time (ms) since gyro output switched on, for the
        // activation ramp. Cleared by Reset while output is off.
        internal double RampElapsedMs;
        // Dither: quantisation error carried to the next report, per axis,
        // always within +-0.5 of a byte step. Cleared by Reset.
        internal double DitherErrorX;
        internal double DitherErrorY;
    }

    internal readonly struct GyroMouseStickOutput
    {
        internal GyroMouseStickOutput(byte axisX, byte axisY)
        {
            AxisX = axisX;
            AxisY = axisY;
            MappedX = DS4MappedStickAxis.FromLegacy(axisX);
            MappedY = DS4MappedStickAxis.FromLegacy(axisY);
        }

        internal GyroMouseStickOutput(in DS4MappedStickAxis mappedX,
            in DS4MappedStickAxis mappedY)
        {
            AxisX = mappedX.LegacyValue;
            AxisY = mappedY.LegacyValue;
            MappedX = mappedX;
            MappedY = mappedY;
        }

        // Byte stick values, 128 = centre. For HighRes this is the rounded
        // compatibility value of MappedX/MappedY.
        internal byte AxisX { get; }
        internal byte AxisY { get; }
        // What is submitted: legacy bytes for Legacy and Dither, a
        // high-resolution axis for HighRes.
        internal DS4MappedStickAxis MappedX { get; }
        internal DS4MappedStickAxis MappedY { get; }
    }

    /// <summary>
    /// Gyro to mouse-joystick maths, from the hard deadzone to the output byte.
    /// Pulled out of <c>Mouse.SixMouseStickCore</c> unchanged so it can be
    /// pinned by a frozen oracle; the caller reads settings and submits.
    /// New steps must sit behind an <c>if</c> on their own setting, so the
    /// defaults keep today's exact operations (and bytes).
    /// The deltas are doubles: Legacy truncates them at each of its original
    /// <c>int</c> steps through <see cref="LegacyInt"/>, while HighRes and
    /// Dither keep the fractions.
    /// </summary>
    internal static class GyroMouseStickMath
    {
        // horizontalAxis: 0 = yaw, otherwise roll. elapsed is in seconds.
        internal static GyroMouseStickOutput Compute(int gyroYawFull,
            int gyroPitchFull, int gyroRollFull, double elapsed,
            int horizontalAxis, GyroMouseStickInfo msinfo,
            in Switch2GyroTriggerModifierResult modifier,
            ref GyroMouseStickFilterState state)
        {
            bool legacy = msinfo.precision == GyroMouseStickInfo.PrecisionMode.Legacy;
            double deltaX = horizontalAxis == 0 ? gyroYawFull : gyroRollFull;
            double deltaY = -gyroPitchFull;
            int maxDirX = deltaX >= 0 ? 127 : -128;
            int maxDirY = deltaY >= 0 ? 127 : -128;

            double tempAngle = Math.Atan2(-deltaY, deltaX);
            double normX = Math.Abs(Math.Cos(tempAngle));
            double normY = Math.Abs(Math.Sin(tempAngle));
            int signX = Math.Sign(deltaX);
            int signY = Math.Sign(deltaY);

            double deadzoneX = Math.Abs(normX * msinfo.deadZone);
            double deadzoneY = Math.Abs(normY * msinfo.deadZone);
            if (legacy)
            {
                deadzoneX = LegacyInt(deadzoneX);
                deadzoneY = LegacyInt(deadzoneY);
            }

            int maxValX = signX * msinfo.maxZone;
            int maxValY = signY * msinfo.maxZone;

            double xratio = 0.0, yratio = 0.0;
            double antiX = msinfo.antiDeadX * normX;
            double antiY = msinfo.antiDeadY * normY;

            // Hard deadzone, then clamp to maxZone.
            if (Math.Abs(deltaX) > deadzoneX)
            {
                deltaX -= signX * deadzoneX;
                deltaX = (deltaX < 0 && deltaX < maxValX) ? maxValX :
                    (deltaX > 0 && deltaX > maxValX) ? maxValX : deltaX;
            }
            else
            {
                deltaX = 0;
            }

            if (Math.Abs(deltaY) > deadzoneY)
            {
                deltaY -= signY * deadzoneY;
                deltaY = (deltaY < 0 && deltaY < maxValY) ? maxValY :
                    (deltaY > 0 && deltaY > maxValY) ? maxValY : deltaY;
            }
            else
            {
                deltaY = 0;
            }

            if (msinfo.softDeadZone > 0)
            {
                // Radial tightening: below the threshold the length becomes
                // length^2 / threshold, so it meets the raw value at the edge.
                double length = Math.Sqrt((double)deltaX * deltaX +
                    (double)deltaY * deltaY);
                if (length > 0.0 && length < msinfo.softDeadZone)
                {
                    double softScale = length / msinfo.softDeadZone;
                    deltaX *= softScale;
                    deltaY *= softScale;
                    if (legacy)
                    {
                        deltaX = LegacyInt(deltaX);
                        deltaY = LegacyInt(deltaY);
                    }
                }
            }

            if (modifier.DeadzoneActive)
            {
                deltaX = Switch2GyroTriggerModifier.ApplySoftDeadzone(
                    deltaX, normX, modifier.DeadzoneAmount);
                deltaY = Switch2GyroTriggerModifier.ApplySoftDeadzone(
                    deltaY, normY, modifier.DeadzoneAmount);
                if (legacy)
                {
                    deltaX = LegacyInt(deltaX);
                    deltaY = LegacyInt(deltaY);
                }
            }
            deltaX = Switch2GyroTriggerModifier.ApplyDampening(
                deltaX, modifier);
            deltaY = Switch2GyroTriggerModifier.ApplyDampening(
                deltaY, modifier);
            if (legacy)
            {
                deltaX = LegacyInt(deltaX);
                deltaY = LegacyInt(deltaY);
            }

            if (msinfo.jitterCompensation)
            {
                // Possibly expose threshold later
                const double threshold = 2;
                const float thresholdF = (float)threshold;

                double absX = Math.Abs(deltaX);
                if (absX <= normX * threshold)
                {
                    deltaX = signX * Math.Pow(absX / thresholdF, 1.408) * threshold;
                    if (legacy) deltaX = LegacyInt(deltaX);
                }

                double absY = Math.Abs(deltaY);
                if (absY <= normY * threshold)
                {
                    deltaY = signY * Math.Pow(absY / thresholdF, 1.408) * threshold;
                    if (legacy) deltaY = LegacyInt(deltaY);
                }
            }

            if (msinfo.useSmoothing)
            {
                if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
                {
                    double currentRate = 1.0 / elapsed;
                    deltaX = state.FilterX.Filter(deltaX, currentRate);
                    deltaY = state.FilterY.Filter(deltaY, currentRate);
                    if (legacy)
                    {
                        deltaX = LegacyInt(deltaX);
                        deltaY = LegacyInt(deltaY);
                    }
                }
                else if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.WeightedAverage)
                {
                    const int len = GyroMouseStickFilterState.SmoothBufferLength;
                    int iIndex = state.SmoothTail % len;
                    state.SmoothX[iIndex] = deltaX;
                    state.SmoothY[iIndex] = deltaY;
                    state.SmoothTail = iIndex + 1;

                    double currentWeight = 1.0;
                    double finalWeight = 0.0;
                    double x_out = 0.0, y_out = 0.0;
                    int idx = 0;
                    for (int i = 0; i < len; i++)
                    {
                        idx = (state.SmoothTail - i - 1 + len) % len;
                        x_out += state.SmoothX[idx] * currentWeight;
                        y_out += state.SmoothY[idx] * currentWeight;
                        finalWeight += currentWeight;
                        currentWeight *= msinfo.smoothWeight;
                    }

                    x_out /= finalWeight;
                    deltaX = x_out;
                    y_out /= finalWeight;
                    deltaY = y_out;
                    if (legacy)
                    {
                        deltaX = LegacyInt(deltaX);
                        deltaY = LegacyInt(deltaY);
                    }
                }

                // Smoothing can flip a sign, so redo the per-direction limits.
                maxValX = deltaX < 0 ? -msinfo.maxZone : msinfo.maxZone;
                maxValY = deltaY < 0 ? -msinfo.maxZone : msinfo.maxZone;
                maxDirX = deltaX >= 0 ? 127 : -128;
                maxDirY = deltaY >= 0 ? 127 : -128;
            }

            if (msinfo.vertScale != 100)
            {
                double verticalScale = msinfo.vertScale * 0.01;
                deltaY *= verticalScale;
                if (legacy) deltaY = LegacyInt(deltaY);
                deltaY = (deltaY < 0 && deltaY < maxValY) ? maxValY :
                    (deltaY > 0 && deltaY > maxValY) ? maxValY : deltaY;
            }

            if (deltaX != 0) xratio = deltaX / (double)maxValX;
            if (deltaY != 0) yratio = deltaY / (double)maxValY;

            if (msinfo.activationRamp > 0)
            {
                // Gain eases 0 -> 1 over the ramp, timed by report elapsed
                // so it is deterministic. 0 on the first report after on.
                if (state.RampElapsedMs < msinfo.activationRamp)
                {
                    double rampGain = state.RampElapsedMs / msinfo.activationRamp;
                    xratio *= rampGain;
                    yratio *= rampGain;
                    state.RampElapsedMs += elapsed * 1000.0;
                }
            }

            if (msinfo.gameCurve != 1.0)
            {
                // Undo the game's stick curve on the vector length so the
                // camera speed follows hand speed; direction is kept.
                double ratioLength = Math.Sqrt(xratio * xratio + yratio * yratio);
                if (ratioLength > 0.0)
                {
                    double curveScale = Math.Pow(ratioLength, 1.0 / msinfo.gameCurve) /
                        ratioLength;
                    xratio *= curveScale;
                    yratio *= curveScale;
                }
            }

            if (msinfo.maxOutputEnabled)
            {
                double maxOutRatio = msinfo.maxOutput / 100.0;
                // Expand output a bit. Likely not going to get a straight line with Gyro
                double maxOutXRatio = Math.Min(normX / 0.95, 1.0) * maxOutRatio;
                double maxOutYRatio = Math.Min(normY / 0.95, 1.0) * maxOutRatio;

                xratio = Math.Min(Math.Max(xratio, 0.0), maxOutXRatio);
                yratio = Math.Min(Math.Max(yratio, 0.0), maxOutYRatio);
            }

            // Anti-deadzone.
            double xNorm = 0.0, yNorm = 0.0;
            if (xratio != 0.0)
            {
                xNorm = (1.0 - antiX) * xratio + antiX;
            }

            if (yratio != 0.0)
            {
                yNorm = (1.0 - antiY) * yratio + antiY;
            }

            if (msinfo.inverted != 0)
            {
                if ((msinfo.inverted & 1) == 1)
                {
                    // Invert max dir value
                    maxDirX = deltaX >= 0 ? -128 : 127;
                }

                if ((msinfo.inverted & 2) == 2)
                {
                    // Invert max dir value
                    maxDirY = deltaY >= 0 ? -128 : 127;
                }
            }

            if (legacy)
            {
                byte axisXOut = (byte)(xNorm * maxDirX + 128.0);
                byte axisYOut = (byte)(yNorm * maxDirY + 128.0);
                return new GyroMouseStickOutput(axisXOut, axisYOut);
            }

            // The exact stick coordinate (0..255, 128 = centre) before any
            // quantisation; the clamp only guards bad settings (maxZone 0).
            double coordX = ExactCoordinate(xNorm, maxDirX);
            double coordY = ExactCoordinate(yNorm, maxDirY);
            if (msinfo.precision == GyroMouseStickInfo.PrecisionMode.Dither)
            {
                return new GyroMouseStickOutput(
                    DitherAxis(coordX, ref state.DitherErrorX),
                    DitherAxis(coordY, ref state.DitherErrorY));
            }

            DS4MappedStickAxis.TryFromProfileCoordinate(coordX, out var mappedX);
            DS4MappedStickAxis.TryFromProfileCoordinate(coordY, out var mappedY);
            return new GyroMouseStickOutput(mappedX, mappedY);
        }

        // The original int cast, kept as a double. Not Math.Truncate: that
        // keeps -0.0, which the old int path never fed to the filters.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double LegacyInt(double value) => (int)value;

        private static double ExactCoordinate(double norm, int maxDir)
        {
            double coordinate = norm * maxDir + 128.0;
            return double.IsNaN(coordinate) ? 128.0 :
                Math.Clamp(coordinate, 0.0, 255.0);
        }

        // Error diffusion to a byte: carry the rounding error into the next
        // report, so the average equals the exact value and each byte is
        // within one step of it. An exact centre (gyro still) outputs 128 and
        // drops the carry, so a leftover error can never drift the stick.
        internal static byte DitherAxis(double coordinate, ref double error)
        {
            if (coordinate == 128.0)
            {
                error = 0.0;
                return 128;
            }

            double target = coordinate + error;
            double quantized = Math.Clamp(
                Math.Round(target, MidpointRounding.AwayFromZero), 0.0, 255.0);
            error = target - quantized;
            return (byte)quantized;
        }

        // Runs on reports while gyro output is off: pushes a zero into the
        // smoothing ring and, for One Euro, into both filters so they decay
        // instead of resuming from a stale value. Also restarts the
        // activation ramp and clears the dither carry for the next time
        // output switches on.
        internal static void Reset(double elapsed, GyroMouseStickInfo msinfo,
            ref GyroMouseStickFilterState state)
        {
            int iIndex = state.SmoothTail % GyroMouseStickFilterState.SmoothBufferLength;
            state.SmoothX[iIndex] = 0;
            state.SmoothY[iIndex] = 0;
            state.SmoothTail = iIndex + 1;
            state.RampElapsedMs = 0.0;
            state.DitherErrorX = 0.0;
            state.DitherErrorY = 0.0;

            if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
            {
                double currentRate = 1.0 / elapsed;
                state.FilterX.Filter(0.0, currentRate);
                state.FilterY.Filter(0.0, currentRate);
            }
        }
    }
}
