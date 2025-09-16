using R3E.Data;

namespace ReHUD.Extensions
{
    public static class R3EDataExtensions
    {
        internal static bool IsInMenus(this Shared data) => data.GameInMenus == 1 || (data.GamePaused == 1 && data.GameInReplay == 0) || data.SessionType == -1;
        internal static bool IsInGame(this Shared data) => !data.IsInMenus();
        internal static bool IsDriving(this Shared data) => data.IsInGame() && data.ControlType == 0 && data.GameInReplay == 0;
        internal static bool IsNotDriving(this Shared data) => !data.IsDriving();
    }
}
