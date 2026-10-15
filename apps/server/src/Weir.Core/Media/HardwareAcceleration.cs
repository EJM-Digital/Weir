using System.Globalization;
using Weir.Core.Json;
using Weir.Core.Rules;
using Weir.Core.Text;

namespace Weir.Core.Media;

/// <summary>What ffmpeg on this machine says it can do.</summary>
public sealed record AccelerationReport
{
    /// <summary>Methods ffmpeg was compiled with, sorted. Not proof a device is present or working.</summary>
    public IReadOnlyList<string> AvailableMethods { get; init; } = [];

    public bool Detected { get; init; }

    /// <summary>Why detection produced nothing, when it did not.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The vendors any available method belongs to, sorted.</summary>
    public IReadOnlyList<string> Vendors =>
        HardwareAcceleration.VendorMethods
            .Where(vendor => vendor.Value.Any(method => AvailableMethods.Contains(method, StringComparer.Ordinal)))
            .Select(vendor => vendor.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
}

/// <summary>Reading what <c>ffmpeg -hwaccels</c> reports, for the machine-wide hardware check.</summary>
public static class HardwareAcceleration
{
    /// <summary>
    /// Vendors an operator can switch off, and the ffmpeg hwaccel names each covers. The order decides the
    /// vendor of a method two vendors share, such as <c>d3d11va</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> VendorMethods { get; } =
    [
        new("nvidia", ["cuda", "nvdec", "cuvid"]),
        new("intel", ["qsv", "d3d11va"]),
        new("amd", ["amf", "d3d11va"]),
        new("vaapi", ["vaapi"]),
        new("apple", ["videotoolbox"]),
    ];

    /// <summary>The report when ffmpeg could not be run at all.</summary>
    public static AccelerationReport ReportForRunError(string errorText) => new()
    {
        Detected = false,
        Detail = $"Weir could not ask ffmpeg which acceleration methods it supports ({errorText}).",
    };

    /// <summary>The report when ffmpeg ran but did not answer within the time limit.</summary>
    public static AccelerationReport ReportForTimeout() => new()
    {
        Detected = false,
        Detail = "FFmpeg didn't answer in time. Check again in a moment.",
    };

    /// <summary>The report from <c>ffmpeg -hide_banner -hwaccels</c>'s exit code and stdout.</summary>
    public static AccelerationReport ReportFromHwaccels(int returnCode, string? stdout)
    {
        if (returnCode != 0)
        {
            return new AccelerationReport
            {
                Detected = false,
                Detail = "ffmpeg did not report its acceleration methods "
                    + $"(exit code {returnCode.ToString(CultureInfo.InvariantCulture)}). Weir will use software decoding.",
            };
        }

        var methods = new List<string>();
        foreach (var raw in MediaText.SplitLines(stdout ?? string.Empty))
        {
            var line = RulesJson.Lower(WireStrings.Strip(raw));
            // The first line is a heading; anything with whitespace is prose, not a method.
            if (line.Length == 0 || line.Contains(':', StringComparison.Ordinal) || line.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            if (line.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                methods.Add(line);
            }
        }

        if (methods.Count == 0)
        {
            return new AccelerationReport
            {
                Detected = true,
                Detail = "This ffmpeg build reports no hardware acceleration methods, so decoding is done in software.",
            };
        }

        return new AccelerationReport
        {
            AvailableMethods = methods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Detected = true,
            Detail = $"ffmpeg reports {Plural.Of(methods.Count, "acceleration method")}. Being listed does not prove a device is present.",
        };
    }
}
