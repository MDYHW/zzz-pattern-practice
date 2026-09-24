using System;
using System.Diagnostics;
using System.Threading;

namespace VesperLab
{
    public class ScheduledPress
    {
        public int Attack;
        public string Key = "";
        public double? AtMs;
    }

    public class InputEvent
    {
        public string Action = "", Key = "";
        public int Attack;
        public double DueMs, BeginMs, EndMs;
        public int Inserted, Win32Error;
    }

    public interface ITrialClock
    {
        long Timestamp { get; }
        long Frequency { get; }
        double ElapsedMs(long origin);
        void WaitUntil(long origin, double dueMs, Func<bool> cancelled);
    }

    public interface IInputSink
    {
        bool IsTargetForeground();
        bool AnyControlHeld(string ownedKey = null, bool includeLmb = false, bool includeE = false);
        bool AnyDirectionHeld(string ownedKey = null);
        int Send(string key, bool down, out int error);
        string ProcessName { get; }
        int ProcessId { get; }
        string WindowLabel { get; }
    }

    public sealed class StopwatchClock : ITrialClock
    {
        public long Timestamp { get { return Stopwatch.GetTimestamp(); } }
        public long Frequency { get { return Stopwatch.Frequency; } }
        public double ElapsedMs(long origin) { return (Timestamp - origin) * 1000.0 / Frequency; }

        public void WaitUntil(long origin, double dueMs, Func<bool> cancelled)
        {
            while (true)
            {
                if (cancelled != null && cancelled()) return;
                double remaining = dueMs - ElapsedMs(origin);
                if (remaining <= 0) return;
                // Sleep(1) can resume a timer tick later. Keep the final 20ms
                // spinning locally instead of changing the system timer period.
                if (remaining > 20) Thread.Sleep(1);
                else Thread.SpinWait(32);
            }
        }
    }

}
