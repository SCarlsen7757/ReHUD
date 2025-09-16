using R3E.Data;

namespace ReHUD.Utils;

public static class DriverUtils
{
    public static string GetDriverUid(DriverInfo driver)
    {
        return $"{GetDriverName(driver)}_{driver.UserId}_{driver.SlotId}_{driver.LiveryId}";
    }

    public static string GetDriverName(DriverInfo driver)
    {
        return System.Text.Encoding.UTF8.GetString(driver.Name.TakeWhile(c => c != 0).ToArray());
    }

    public static double CalculateDistanceToDriverAhead(double trackLength, DriverData driver, DriverData driverAhead)
    {
        double distance = driverAhead.LapDistance - driver.LapDistance;

        if (distance < 0)
        {
            distance += trackLength;
        }

        return distance;
    }

    public static double CalculateDistanceToDriverBehind(double trackLength, DriverData driver, DriverData driverBehind)
    {
        return CalculateDistanceToDriverAhead(trackLength, driverBehind, driver);
    }
}