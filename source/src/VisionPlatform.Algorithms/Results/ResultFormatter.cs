using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Algorithms.Results;

public enum ResultMessageFormat { Json, Csv }
public sealed record ResultFormatOptions
{
    [DisplayName("消息格式")] public ResultMessageFormat Format { get; init; } = ResultMessageFormat.Json;
    [DisplayName("小数位数"), Range(0, 15)] public int DecimalPlaces { get; init; } = 3;
    [DisplayName("CSV 包含位置/角度/尺度")] public bool IncludePoseInCsv { get; init; }
    [DisplayName("CSV 包含算法耗时")] public bool IncludeTimingInCsv { get; init; }
    [DisplayName("CSV 包含通信状态")] public bool IncludeCommunicationInCsv { get; init; }
}

/// <summary>Pure inspection-to-message conversion. Transport implementations know no inspection fields.</summary>
public static class ResultFormatter
{
    public static IReadOnlyList<string> Validate(ResultFormatOptions? options)
    {
        if (options is null) return ["Result formatting options are required."];
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Format)) errors.Add("Result message format is invalid.");
        if (options.DecimalPlaces is < 0 or > 15) errors.Add("DecimalPlaces must be between 0 and 15.");
        return errors;
    }

    public static string Format(InspectionReport report, ResultFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        ValidateReport(report);
        var result = report.IsOk ? "OK" : "NG";
        double? Rounded(double? value) => value is null ? null : Math.Round(value.Value, options.DecimalPlaces, MidpointRounding.AwayFromZero);
        if (options.Format == ResultMessageFormat.Json)
            return JsonSerializer.Serialize(new
            {
                result, count = report.BlobCount, score = Rounded(report.MatchScore),
                x = Rounded(report.CenterX), y = Rounded(report.CenterY), angle = Rounded(report.Angle), scale = Rounded(report.Scale),
                elapsedMs = Rounded(report.ExecutionMilliseconds), communication = report.Communication?.State.ToString(),
                communicationSuccess = report.Communication?.Success, inspectionId = report.InspectionId
            });
        string Number(double? value) => value?.ToString("F" + options.DecimalPlaces, CultureInfo.InvariantCulture) ?? "";
        var fields = new List<string> { result, report.BlobCount?.ToString(CultureInfo.InvariantCulture) ?? "", Number(report.MatchScore) };
        if (options.IncludePoseInCsv) fields.AddRange([Number(report.CenterX), Number(report.CenterY), Number(report.Angle), Number(report.Scale)]);
        if (options.IncludeTimingInCsv) fields.Add(Number(report.ExecutionMilliseconds));
        if (options.IncludeCommunicationInCsv) fields.Add(report.Communication?.State.ToString() ?? "");
        return string.Join(",", fields);
    }

    public static void ValidateReport(InspectionReport report)
    {
        if (report.BlobCount is < 0) throw new ArgumentException("BlobCount must be nonnegative.");
        foreach (var value in new[] { report.MatchScore, report.CenterX, report.CenterY, report.Angle, report.Scale, report.ExecutionMilliseconds })
            if (value is { } present && !double.IsFinite(present)) throw new ArgumentException("Inspection numeric fields must be finite.");
        if (report.Scale is <= 0) throw new ArgumentException("Inspection scale must be positive.");
        if (report.ExecutionMilliseconds is < 0) throw new ArgumentException("Inspection execution time must be nonnegative.");
    }
}
