# Reproduce the synthetic matcher comparison

Run commands sequentially from the VisionPlatform directory. The Python probe imports the original local source read-only; it does not copy it into this platform. Its `SOURCE` setting may be changed to another authorized IndustrialVisionStudio 1.3.1 source checkout.

```powershell
python tools/MatchingComparison/compare.py
dotnet run --project tools/MatchingComparison/MatchingComparison.csproj -c Release -- tools/MatchingComparison/data
python tools/MatchingComparison/summarize.py
```

The first command deterministically regenerates input images and evaluates IndustrialVisionStudio. The second evaluates the fixed vendored CvInspect core using the official OpenCvSharp4.runtime.win.slim 4.13.0.20260627 package. Every case has 2 warmups and 7 measured runs. Both algorithms receive the same template, same image pixels and a view of exactly the same integer ROI. C# output coordinates add the view offset; Python performs this offset mapping internally. No image loading or process startup is timed. OpenCV native threading is set to 1 in both; CvInspect still uses its own managed Parallel.For. The platform's normal matcher does not change global OpenCV thread settings.

Inputs are synthetic, not real industrial samples. They use a known affine transform, brightness/contrast/noise changes and a small rectangular occlusion. Angles and scales largely lie on search grids; the zero angle/scale errors observed for IndustrialVisionStudio are consequently not evidence of universal zero-error estimation.

Memory is Windows process working set after each run and process peak, including the language runtime and allocator-retained memory. Samples are not allocation counts or continuously sampled per-call peaks. No allocator/GC normalization or dedicated machine isolation was performed. Timing and memory data only characterize this fixture and environment.

`data/*-results.json` contains every measured result; `summary.json` includes median, min, max, errors and exact repeat consistency; `input-sha256.json` identifies all shared inputs; `source-hashes.json` identifies the IndustrialVisionStudio source files read. `native-package.json` records the actual current native DLL size/hash. The root report records the exact CvInspect commit. An earlier exploratory run under sibling VisionPlatform-audit was superseded by this source-backed, equal-crop run.

After switching native deployment to slim, the C# cases were re-executed while retaining the unchanged Python measurements and identical input PNGs. Previous full-native C# raw measurements, their summary, input hashes and native identity remain under `data/history-full-native`; they were not deleted or reused as slim results. The current native DLL is 55,547,904 bytes, SHA-256 `1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd`; no standalone FFmpeg DLL appears in this probe output. OpenCV GetBuildInformation still reports inherited full compile metadata (Non-free algorithms YES, FFMPEG YES). Absence of a separate video-codec DLL and a slim package name do not establish that all native licensing concerns are removed; consult the platform third-party inventory.
