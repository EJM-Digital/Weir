namespace Weir.Core.MediaManagers;

/// <summary>The plain words Activity uses for a scan Weir asked a Sonarr or Radarr for while it was busy with another scan.</summary>
public static class DownloadedScanMessages
{
    /// <summary>The app said it is busy; Weir will ask again shortly.</summary>
    public static string AskingAgain(string manager, string file) =>
        $"{manager} is busy with another scan, so Weir will ask it to scan for {file} again shortly";

    /// <summary>The app was still busy at the last ask, so Weir has stopped asking.</summary>
    public static string StoppedAsking(string manager, int asks) =>
        $"{manager} was still busy with another scan after Weir asked {asks.ToString(System.Globalization.CultureInfo.InvariantCulture)} times, so Weir has stopped asking.";
}
