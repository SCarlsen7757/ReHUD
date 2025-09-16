using R3E.Data;
using System.Collections.Immutable;
using System.Diagnostics;

namespace ReHUD.Utils
{
    public static class Utilities
    {
        public static List<Tuple<DriverData, DriverData>> GetDriverMatches(DriverData[] oldData, DriverData[] newData)
        {
            var oldUids = new Dictionary<string, DriverData>();
            foreach (var driver in oldData)
            {
                oldUids[DriverUtils.GetDriverUid(driver.DriverInfo)] = driver;
            }

            var res = new List<Tuple<DriverData, DriverData>>();
            foreach (var driver in newData)
            {
                string uid = DriverUtils.GetDriverUid(driver.DriverInfo);
                DriverData? oldDriver = oldUids.GetValueOrDefault(uid);
                if (oldDriver != null)
                {
                    res.Add(new(oldDriver.Value, driver));
                }
            }
            return res;
        }

        public static float RpsToRpm(float rps)
        {
            return rps * (60 / (2 * (float)Math.PI));
        }

        public static float MpsToKph(float mps)
        {
            return mps * 3.6f;
        }

        public static bool IsRaceRoomRunning()
        {
            return Process.GetProcessesByName("RRRE").Length > 0 || Process.GetProcessesByName("RRRE64").Length > 0;
        }

        /// <summary>
        /// Returns either the estimated total number of laps and number of laps left, or null if the data is not available. <total, left>
        /// </summary>
        internal static Tuple<int?, double?> GetEstimatedLapCount(Shared data, double? bestLaptime)
        {
            double fraction = data.LapDistanceFraction;

            double leaderFraction = fraction;
            double leaderCurrentLaptime = data.LapTimeCurrentSelf;
            int leaderCompletedLaps = data.CompletedLaps;

            DriverData? leader_ = GetLeader(data);
            if (leader_ != null)
            {
                DriverData leader = leader_.Value;
                if (leader.FinishStatus == 1)
                {
                    return new(data.CompletedLaps + 1, 1 - fraction);
                }

                if (leader.CompletedLaps != -1)
                {
                    leaderCompletedLaps = leader.CompletedLaps;
                }

                leaderCurrentLaptime = leader.LapTimeCurrentSelf;
                leaderFraction = -1;
                if (leader.LapDistance != -1 && data.LayoutLength != -1)
                {
                    leaderFraction = leader.LapDistance / data.LayoutLength;
                }
            }


            if (leaderCompletedLaps == -1 || leaderCompletedLaps == -1 && leaderFraction == -1)
            {
                return new(null, null);
            }


            // number of laps left for the leader
            int res;

            double sessionTimeRemaining = data.SessionTimeRemaining;
            if (sessionTimeRemaining != -1)
            {
                double referenceLap;

                if (data.LapTimeBestLeader > 0 && leaderCompletedLaps > 1)
                {
                    referenceLap = data.LapTimeBestLeader;
                }
                else if (data.LapTimeBestSelf > 0 && data.CompletedLaps > 1)
                {
                    referenceLap = data.LapTimeBestSelf;
                }
                else
                {
                    if (bestLaptime == null)
                    {
                        return new(null, null);
                    }
                    referenceLap = bestLaptime.Value;
                }

                if (leaderCurrentLaptime != -1)
                {
                    res = (int)Math.Ceiling((sessionTimeRemaining + leaderCurrentLaptime) / referenceLap);
                }
                else
                {
                    res = (int)Math.Ceiling(sessionTimeRemaining / referenceLap + leaderFraction);
                }
            }
            else
            {
                int sessionLaps = data.NumberOfLaps;
                if (sessionLaps == -1)
                {
                    return new(null, null);
                }

                if (leaderCompletedLaps == -1)
                {
                    return new(sessionLaps, 0);
                }
                res = sessionLaps - leaderCompletedLaps;
            }
            res = res +
                    (leaderFraction < fraction ? 1 : 0) +
                    (data.SessionLengthFormat == 2 ? 1 : 0);

            return new(res + data.CompletedLaps, res - fraction);
        }

        internal static DriverData? GetLeader(Shared data)
        {
            foreach (DriverData leader in data.DriverData)
            {
                if (leader.Place == 1)
                {
                    return leader;
                }
            }
            return null;
        }

        public static long SafeCastToLong(object value)
        {
            if (value is int v)
            {
                return v;
            }
            else if (value is long v1)
            {
                return v1;
            }
            else if (value is uint v2)
            {
                return v2;
            }
            else if (value is ulong v3)
            {
                return (long)v3;
            }
            else
            {
                throw new InvalidCastException($"Cannot cast {value.GetType()} to long");
            }
        }

        private static readonly ImmutableHashSet<R3E.Constant.SessionPhase> drivingPhases = ImmutableHashSet.Create(
            R3E.Constant.SessionPhase.Green,
            R3E.Constant.SessionPhase.Checkered
        );

        public static bool SessionPhaseNotDriving(R3E.Constant.SessionPhase? sessionPhase)
        {
            return sessionPhase == null || !drivingPhases.Contains(sessionPhase!.Value);
        }
    }
}