using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Tesseract;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Recognition;
using PlatformOcrWord = VisionPlatform.Contracts.Recognition.OcrWord;

namespace VisionPlatform.Algorithms.Recognition;

public enum OcrSegmentation { SingleLine, SingleBlock, Auto }

public sealed record OcrOptions
{
    [DisplayName("语言模型")] public string Language { get; init; } = "eng";
    [DisplayName("模型目录（空为随程序 tessdata）")] public string DataPath { get; init; } = "";
    [DisplayName("文本布局")] public OcrSegmentation Segmentation { get; init; } = OcrSegmentation.SingleLine;
    [DisplayName("字符白名单（空为不限）")] public string CharacterWhitelist { get; init; } = "";
    [DisplayName("识别前归一化灰度对比度")] public bool NormalizeContrast { get; init; } = true;
    [DisplayName("最低置信度 0..1"), Range(0, 1)] public double MinimumConfidence { get; init; }
    [DisplayName("最大处理像素数"), Range(1, 32000000)] public int MaximumPixels { get; init; } = 16_000_000;
    [DisplayName("最大输出字符数"), Range(1, 65536)] public int MaximumCharacters { get; init; } = 4096;
    [DisplayName("最大输出词数"), Range(1, 8192)] public int MaximumWords { get; init; } = 2048;
    [DisplayName("使用参数 ROI")] public bool UseRoi { get; init; }
    [DisplayName("ROI 左上 X")] public double RoiX { get; init; }
    [DisplayName("ROI 左上 Y")] public double RoiY { get; init; }
    [DisplayName("ROI 宽度"), Range(double.Epsilon, 1000000)] public double RoiWidth { get; init; } = 200;
    [DisplayName("ROI 高度"), Range(double.Epsilon, 1000000)] public double RoiHeight { get; init; } = 100;
}

public interface IOcrRecognizer
{
    OcrResult Recognize(ImageFrame image, OcrOptions options, RotatedRectRoi? roi = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Offline Tesseract adapter. Inputs are borrowed; no pixels, engines or results survive a call.</summary>
public sealed class TesseractOcrRecognizer : IOcrRecognizer
{
    // Bound simultaneous native engines and their model memory across Runtime sessions.
    private static readonly SemaphoreSlim EngineGate = new(1, 1);

    public static IReadOnlyList<string> Validate(OcrOptions p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<string>();
        if (p.Language is null || p.Language.Length > 64 ||
            !Regex.IsMatch(p.Language, "^[A-Za-z0-9_]+(?:\\+[A-Za-z0-9_]+){0,3}$", RegexOptions.CultureInvariant))
            errors.Add("Language must contain one to four model names joined with '+', without paths.");
        if (p.DataPath is null || p.DataPath.Length > 4096 || p.DataPath.IndexOf('\0') >= 0)
            errors.Add("DataPath must be a valid directory path or empty for bundled tessdata.");
        if (!Enum.IsDefined(p.Segmentation)) errors.Add("Invalid OCR Segmentation value.");
        if (p.CharacterWhitelist is null || p.CharacterWhitelist.Length > 1024 || p.CharacterWhitelist.Any(char.IsControl))
            errors.Add("CharacterWhitelist must contain at most 1024 printable characters.");
        if (!double.IsFinite(p.MinimumConfidence) || p.MinimumConfidence < 0 || p.MinimumConfidence > 1)
            errors.Add("MinimumConfidence must be finite in [0,1].");
        if (p.MaximumPixels < 1 || p.MaximumPixels > 32_000_000) errors.Add("MaximumPixels must be in [1,32000000].");
        if (p.MaximumCharacters < 1 || p.MaximumCharacters > 65536) errors.Add("MaximumCharacters must be in [1,65536].");
        if (p.MaximumWords < 1 || p.MaximumWords > 8192) errors.Add("MaximumWords must be in [1,8192].");
        if (p.UseRoi && (new[] { p.RoiX, p.RoiY, p.RoiWidth, p.RoiHeight }.Any(v => !double.IsFinite(v)) ||
            p.RoiWidth <= 0 || p.RoiHeight <= 0 || p.RoiWidth > 1_000_000 || p.RoiHeight > 1_000_000))
            errors.Add("ROI geometry must be finite, with Width/Height in (0,1000000].");
        return errors;
    }

    public OcrResult Recognize(ImageFrame image, OcrOptions options, RotatedRectRoi? roi = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        if (image.PixelFormat != PixelFormat.Mono8)
            throw new ArgumentException("OCR requires Mono8; convert RGB/BGR/Bayer explicitly before recognition.", nameof(image));
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        roi ??= options.UseRoi ? new RectRoi(options.RoiX, options.RoiY, options.RoiWidth, options.RoiHeight).ToRotated(image.CoordinateSpace.Id) : null;
        using var region = RecognitionRegion.Open(image, roi, options.MaximumPixels);
        var space = image.CoordinateSpace.Id;
        // SingleLine can hallucinate a glyph even on completely uniform pixels. Such data has no text edges.
        var modelDirectory = string.IsNullOrWhiteSpace(options.DataPath)
            ? Path.Combine(AppContext.BaseDirectory, "tessdata") : Path.GetFullPath(options.DataPath);
        foreach (var language in options.Language.Split('+'))
        {
            var model = Path.Combine(modelDirectory, language + ".traineddata");
            if (!File.Exists(model)) return Empty($"OCR model is missing: {model}. Install the reviewed offline traineddata resource.", status: OcrStatus.MissingLanguageModel);
            var length = new FileInfo(model).Length;
            if (length < 1 || length > 64_000_000) return Empty($"OCR model '{language}' is empty or exceeds the 64 MB per-model limit.", status: OcrStatus.ResourceLimit);
        }
        if (IsConstant(region, cancellationToken)) return Empty("No text: the current image/ROI has no grayscale variation.");
        EngineGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var engine = new TesseractEngine(modelDirectory, options.Language, EngineMode.LstmOnly);
            if (options.CharacterWhitelist.Length > 0 && !engine.SetVariable("tessedit_char_whitelist", options.CharacterWhitelist))
                return Empty("OCR engine rejected CharacterWhitelist.", status: OcrStatus.EngineError);
            using var pix = Pix.LoadFromMemory(EncodeMonoBmp(region, options.NormalizeContrast, cancellationToken));
            using var page = engine.Process(pix, options.Segmentation switch
            {
                OcrSegmentation.SingleLine => PageSegMode.SingleLine,
                OcrSegmentation.SingleBlock => PageSegMode.SingleBlock,
                _ => PageSegMode.Auto
            });
            // The native call is synchronous and cannot be preempted by this wrapper. Never publish after cancellation.
            var text = page.GetText().Trim();
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length == 0) return Empty("No text was recognized in the current image/ROI.");
            if (text.Length > options.MaximumCharacters) return Empty("OCR output exceeds MaximumCharacters; narrow the ROI.", status: OcrStatus.ResourceLimit);
            // Wrapper Page.GetMeanConfidence is already normalized to [0,1]; iterator word confidence is [0,100].
            var confidence = Math.Clamp((double)page.GetMeanConfidence(), 0, 1);
            if (!double.IsFinite(confidence)) return Empty("OCR engine returned an invalid confidence.");
            if (confidence < options.MinimumConfidence)
                return Empty($"OCR confidence {confidence:F4} is below MinimumConfidence {options.MinimumConfidence:F4}.", confidence, OcrStatus.LowConfidence);
            var words = new List<PlatformOcrWord>();
            using var iterator = page.GetIterator();
            iterator.Begin();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrWhiteSpace(word)) continue;
                if (words.Count >= options.MaximumWords) return Empty("OCR output exceeds MaximumWords; narrow the ROI.", status: OcrStatus.ResourceLimit);
                if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds)) continue;
                var wordConfidence = Math.Clamp(iterator.GetConfidence(PageIteratorLevel.Word) / 100d, 0, 1);
                if (!double.IsFinite(wordConfidence)) continue;
                var x = Math.Clamp(bounds.X1, 0, region.Width);
                var y = Math.Clamp(bounds.Y1, 0, region.Height);
                var right = Math.Clamp(bounds.X2, x, region.Width);
                var bottom = Math.Clamp(bounds.Y2, y, region.Height);
                if (right <= x || bottom <= y) continue;
                words.Add(new(word, wordConfidence, new(x + region.CoordinateSpace.OriginX,
                    y + region.CoordinateSpace.OriginY, right - x, bottom - y)));
            }
            while (iterator.Next(PageIteratorLevel.Word));
            cancellationToken.ThrowIfCancellationRequested();
            var overlays = words.Select(w => new RecognitionOverlay(RecognitionOverlayKind.Polygon,
                $"{w.Text} ({w.Confidence:P0})", Array.AsReadOnly(w.Bounds.ToRotated(space).Corners()), space)).ToArray();
            return new(true, text, confidence, words.AsReadOnly(), space, timer.Elapsed, Array.AsReadOnly(overlays));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException or
            TypeInitializationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Empty("OCR engine/resource error: " + ex.GetBaseException().Message, status: OcrStatus.EngineError);
        }
        finally { EngineGate.Release(); }

        OcrResult Empty(string reason, double confidence = 0, OcrStatus status = OcrStatus.NoText) =>
            new(false, "", confidence, Array.Empty<PlatformOcrWord>(), space, timer.Elapsed,
                Array.Empty<RecognitionOverlay>(), reason) { Status = status };
    }

    private static bool IsConstant(ImageFrame image, CancellationToken ct)
    {
        using var lease = image.Lease();
        var first = lease.GetRowSpan(0)[0];
        for (var y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var pixel in lease.GetRowSpan(y)) if (pixel != first) return false;
        }
        return true;
    }

    // Uncompressed indexed BMP avoids System.Drawing, WPF, codec NuGets and source stride assumptions.
    private static byte[] EncodeMonoBmp(ImageFrame image, bool normalizeContrast, CancellationToken ct)
    {
        var stride = checked((image.Width + 3) & ~3);
        const int pixelOffset = 14 + 40 + 256 * 4;
        var pixels = checked(stride * image.Height);
        var data = new byte[checked(pixelOffset + pixels)];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        Write(2, data.Length); Write(10, pixelOffset); Write(14, 40);
        Write(18, image.Width); Write(22, image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 8);
        Write(34, pixels); Write(46, 256);
        for (var i = 0; i < 256; i++)
            data[54 + i * 4] = data[55 + i * 4] = data[56 + i * 4] = (byte)i;
        using var lease = image.Lease();
        var minimum = 255; var maximum = 0;
        if (normalizeContrast)
            for (var y = 0; y < image.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var pixel in lease.GetRowSpan(y))
                { minimum = Math.Min(minimum, pixel); maximum = Math.Max(maximum, pixel); }
            }
        var normalize = normalizeContrast && maximum > minimum && (minimum > 0 || maximum < 255);
        for (var y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            var source = lease.GetRowSpan(y);
            var target = data.AsSpan(pixelOffset + (image.Height - 1 - y) * stride, image.Width);
            if (normalize)
                for (var x = 0; x < source.Length; x++)
                    target[x] = (byte)(((source[x] - minimum) * 255 + (maximum - minimum) / 2) / (maximum - minimum));
            else source.CopyTo(target);
        }
        return data;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }
}
