# AGENTS.md

Project context and engineering constraints for AI coding agents working on `PTT-2026` (`PdfToTiff.exe`).

## Overview

Single-file native Windows desktop utility (`.NET Framework 4.8`, WinForms + WPF WIC) that converts **PDF** files into **CMYK 32-bit TIFF (LZW)** in the source folder using an embedded ICC profile (default `USWebCoatedSWOP.icc`), matching Adobe Photoshop color and sharpness.

## Hard Constraints

- **No external libraries / no NuGet / no .csproj required.** Everything lives in `Program.cs` and compiles via built-in x64 `csc.exe`.
- **C# 5 syntax only.** Built-in .NET 4.8 `csc.exe` does **not** support C# 6+ syntax: do not use string interpolation (`$""`), null-conditional (`?.`), expression-bodied members (`=>` on properties/methods), `nameof()`, `out var`, or pattern matching.
- **Large-file RAM safety.** Print files can exceed 50+ MP (`510×710 mm @ 300 DPI` and larger). Never load a full TIFF into memory; keep pixel processing band-by-band (64-row strips, lazy 1-bit masks).
- **Keep user print files out of git.** `*.pdf`, `*.tif`, `*.tiff`, `*.exe`, `engine/`, and `test/` are ignored in `.gitignore`.

## Build

Windows CMD / Explorer:
```bat
build.bat
```

Git Bash / MSYS (must set `MSYS_NO_PATHCONV=1` so `/switches` are not mangled into paths):
```sh
MSYS_NO_PATHCONV=1 /c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe \
  /platform:x64 /codepage:65001 /target:winexe /optimize+ /win32icon:app.ico \
  /r:System.Xaml.dll \
  '/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll' \
  '/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF\WindowsBase.dll' \
  /out:PdfToTiff.exe Program.cs
```

## Architecture (`Program.cs`)

- `Program.Main`: calls `SetProcessDPIAware()`, parses optional PDF CLI arguments, launches `MainForm`.
- `MainForm`: WinForms UI (ICC selector, `TopMost` toggle button, symmetric crop `Ш × В` in mm with auto-select on focus/Tab, split-pages / overprint / Rich Black checkboxes, Drag & Drop panel, log).
- `QueryPageSizes`: queries PDF TrimBox dimensions in points via Ghostscript `nullpage`.
- `DetectNativeDpi`: probes page 1 via `-dPDFDEBUG` on `nullpage` to detect embedded image DPI and vector/text operators. Enforces a `300 DPI` floor for vector/hybrid PDFs; preserves native DPI (`72..1200`) for pure raster PDFs.
- `BuildRenderArgs`: invokes `gswin64c.exe` (`-sDEVICE=tiff32nc`, LZW, ICC, centered crop).
- `TiffColorProcessor`, `VerticalMorphFilter`, `RichBlackBitmapSource`: band-by-band WIC (`TiffBitmapDecoder` / `TiffBitmapEncoder`) preflight and optional safe Rich Black enhancement.

## Critical Gotchas (Do Not Regress)

1. **Ghostscript 10 cropping:** use `-dDEVICEWIDTHPOINTS` + `-dDEVICEHEIGHTPOINTS` + `-dFIXEDMEDIA` with `<</Install { -trimX -trimY translate }>> setpagedevice`. Do **not** use `PageOffset` (breaks Y-axis alignment in GS 10 `pdfi`).
2. **Ghostscript 10 overprint:** use `-dOverprint=/simulate` (or `/disable`). `-dSimulateOverprint=true` was removed in GS 10.
3. **Ghostscript CMYK passthrough:** always pass `-sDefaultCMYKProfile="<icc>"` together with `-sOutputICCProfile="<icc>"`. Without `-sDefaultCMYKProfile`, untagged `DeviceCMYK` (including pure `0/0/0/100` black text) gets converted through GS's internal default CMYK profile into 4-color black and drifts from Photoshop.
4. **WinForms + WPF DPI shrinking bug:** first access to `PresentationCore.dll` switches a DPI-unaware process to DPI-aware mid-flight, shrinking an already-open WinForms window on >100% displays. Keep `SetProcessDPIAware()` before `Application.Run` and `AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;` in `MainForm.BuildUi()`.
5. **WPF `BitmapSource` custom subclassing:** `TiffBitmapEncoder` calls unmanaged `CopyPixels(Int32Rect, IntPtr, int, int)`. Always override **both** `CopyPixels` overloads (`Array` and `IntPtr`) on `RichBlackBitmapSource`, and keep `/r:System.Xaml.dll` in `build.bat` (required by `BitmapFrame.Create`).
6. **Rich Black safety (unchecked by default):** targets only pure-K pixels (`K >= 250 && C,M,Y <= 5`) inside solid areas surviving morphological erosion (`0.45 mm`), dilated back with a `0.17 mm` pure-K keepaway choke around white knockouts, replaced with `C40 M30 Y30 K100` (`102, 77, 77, 255`). Preserves fine text, thin lines, photos, and knockouts.
7. **UX conventions:** `_tbTargetW` (`TabIndex = 0`) and `_tbTargetH` (`TabIndex = 1`) auto-select all text on Tab/click (`AttachAutoSelect`); `_btnTopMost` stays enabled during conversion and has `TabStop = false`.

## Validation

After editing `Program.cs`:
1. Compile with `csc.exe` (must succeed with 0 errors / 0 warnings).
2. Verify converted TIFF tags: `PixelFormats.Cmyk32`, LZW (`tag 259 == 5`), expected DPI, and embedded ICC profile (`tag 34675`).
3. If touching UI or WPF code, verify `MainForm.ClientSize` does not change before/after instantiating `TiffBitmapDecoder` at system DPI.

## Planned Direction

- Potential second tab/mode for print imposition (спуски): grid layout of business cards and stickers onto sheet sizes (e.g. SRA3/A3) with bleeds, gutters, crop marks, duplex mirroring, and optional PDF output for plotter cut contours.
