using System;
using System.Runtime.CompilerServices;
using DS4Windows.Switch2;
using Sensorit.Base;

namespace DS4Windows
{
    // Three-entry ring for the weighted-average smoothing. Inline so the
    // state struct copies by value and holds no arrays.
    [InlineArray(GyroMouseStickFilterState.SmoothBufferLength)]
    internal struct GyroMouseStickSmoothBuffer
    {
        private int element0;
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
    }

    internal readonly struct GyroMouseStickOutput
    {
        internal GyroMouseStickOutput(byte axisX, byte axisY)
        {
            AxisX = axisX;
            AxisY = axisY;
        }

        // Legacy byte stick values, 128 = centre.
        internal byte AxisX { get; }
        internal byte AxisY { get; }
    }

    /// <summary>
    /// Gyro to mouse-joystick maths, from the hard deadzone to the output byte.
    /// Pulled out of <c>Mouse.SixMouseStickCore</c> unchanged so it can be
    /// pinned by a frozen oracle; the caller reads settings and submits.
    /// New steps must sit behind an <c>if</c> on their own setting, so the
    /// defaults keep today's exact operations (and bytes).
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
            int deltaX = horizontalAxis == 0 ? gyroYawFull : gyroRollFull;
            int deltaY = -gyroPitchFull;
            int maxDirX = deltaX >= 0 ? 127 : -128;
            int maxDirY = deltaY >= 0 ? 127 : -128;

            double tempAngle = Math.Atan2(-deltaY, deltaX);
            double normX = Math.Abs(Math.Cos(tempAngle));
            double normY = Math.Abs(Math.Sin(tempAngle));
            int signX = Math.Sign(deltaX);
            int signY = Math.Sign(deltaY);

            int deadzoneX = (int)Math.Abs(normX * msinfo.deadZone);
            int deadzoneY = (int)Math.Abs(normY * msinfo.deadZone);

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
                    deltaX = (int)(deltaX * softScale);
                    deltaY = (int)(deltaY * softScale);
                }
            }

            if (modifier.DeadzoneActive)
            {
                deltaX = (int)Switch2GyroTriggerModifier.ApplySoftDeadzone(
                    deltaX, normX, modifier.DeadzoneAmount);
                deltaY = (int)Switch2GyroTriggerModifier.ApplySoftDeadzone(
                    deltaY, normY, modifier.DeadzoneAmount);
            }
            deltaX = (int)Switch2GyroTriggerModifier.ApplyDampening(
                deltaX, modifier);
            deltaY = (int)Switch2GyroTriggerModifier.ApplyDampening(
                deltaY, modifier);

            if (msinfo.jitterCompensation)
            {
                // Possibly expose threshold later
                const double threshold = 2;
                const float thresholdF = (float)threshold;

                double absX = Math.Abs(deltaX);
                if (absX <= normX * threshold)
                {
                    deltaX = (int)(signX * Math.Pow(absX / thresholdF, 1.408) * threshold);
                }

                double absY = Math.Abs(deltaY);
                if (absY <= normY * threshold)
                {
                    deltaY = (int)(signY * Math.Pow(absY / thresholdF, 1.408) * threshold);
                }
            }

            if (msinfo.useSmoothing)
            {
                if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
                {
                    double currentRate = 1.0 / elapsed;
                    deltaX = (int)(state.FilterX.Filter(deltaX, currentRate));
                    deltaY = (int)(state.FilterY.Filter(deltaY, currentRate));
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
                    deltaX = (int)x_out;
                    y_out /= finalWeight;
                    deltaY = (int)y_out;
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
                deltaY = (int)(deltaY * verticalScale);
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

            byte axisXOut = (byte)(xNorm * maxDirX + 128.0);
            byte axisYOut = (byte)(yNorm * maxDirY + 128.0);
            return new GyroMouseStickOutput(axisXOut, axisYOut);
        }

        // Runs on reports while gyro output is off: pushes a zero into the
        // smoothing ring and, for One Euro, into both filters so they decay
        // instead of resuming from a stale value. Also restarts the
        // activation ramp for the next time output switches on.
        internal static void Reset(double elapsed, GyroMouseStickInfo msinfo,
            ref GyroMouseStickFilterState state)
        {
            int iIndex = state.SmoothTail % GyroMouseStickFilterState.SmoothBufferLength;
            state.SmoothX[iIndex] = 0;
            state.SmoothY[iIndex] = 0;
            state.SmoothTail = iIndex + 1;
            state.RampElapsedMs = 0.0;

            if (msinfo.smoothingMethod == GyroMouseStickInfo.SmoothingMethod.OneEuro)
            {
                double currentRate = 1.0 / elapsed;
                state.FilterX.Filter(0.0, currentRate);
                state.FilterY.Filter(0.0, currentRate);
            }
        }
    }
}
