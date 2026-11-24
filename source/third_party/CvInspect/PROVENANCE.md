# CvInspect source provenance

- Upstream: https://github.com/cmir79/CvInspect
- Exact commit: `0eddfbaa5195753c2a589fef82555bdf6455da80` (0.34.0).
- License: Apache-2.0, original LICENSE and THIRD-PARTY-NOTICES.md retained.
- All tracked files from upstream `src/CvInspect` were copied, with no algorithm source changes. No Demo, WPF, camera driver, bin/obj, or upstream native binaries are included.
- Modification: replaced only CvInspect.csproj with a net8.0-only build file; retained managed OpenCvSharp4 4.13.0.20260627 and embedded language resources. Removed upstream packaging-only metadata and netstandard target.
- `upstream-sha256.json` records SHA-256 of original files before that project-file modification. Paths in that manifest are upstream paths.
- VisionPlatform adds an independent adapter; it does not reuse DemoRecipeRunner or Demo project saving.
- Native OpenCvSharp for product execution is selected by VisionPlatform.Imaging: official OpenCvSharp4.runtime.win.slim 4.13.0.20260627. The independent comparison probe explicitly selects the same package. No upstream native binary is vendored here. See platform third-party inventory for exact DLL hashes, absence of a separate FFmpeg video-codec DLL, and remaining native compile-metadata/licensing review limits; the slim package name does not itself establish removal of all native licensing concerns.
