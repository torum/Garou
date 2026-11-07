# FFmpeg and FFprobe Distribution

Garou uses FFmpeg for video and image thumbnails and FFprobe for video metadata.

FFmpeg and FFprobe are bundled with the Windows build. On Linux and macOS, the app uses the FFmpeg and FFprobe already installed on PATH.

## Status

Windows bundling is configured in `Garou.UI.csproj` (copies `ThirdParty\ffmpeg\win-x64\**` to output and publish). The binaries are not added to the repository. Linux and macOS use a system install and are not bundled.

## Requirements per Platform

| Platform | RID | FFmpeg source | Notes |
|----------|-----|---------------|-------|
| Windows x64 | win-x64 | Bundled: ffmpeg.exe, ffprobe.exe | Placed next to the app executable. |
| Linux x64 | linux-x64 | System install on PATH | Install via the distribution package manager. |
| macOS ARM64 | osx-arm64 | System install on PATH | Install via Homebrew or equivalent. |
| macOS x64 | osx-x64 | System install on PATH | Not host-verified. |

## Lookup Order

1. Bundled binary in the application directory (`AppContext.BaseDirectory`), if present.
2. Otherwise, the bare name `ffmpeg` or `ffprobe`, resolved from PATH.

If FFprobe is unavailable, video metadata extraction is skipped gracefully. If FFmpeg is unavailable, thumbnail generation is skipped gracefully.

## Licensing

FFmpeg is licensed under LGPL 2.1 or later, or GPL 2 or later, depending on how it is built. Before distribution:

- Record the exact FFmpeg version and build configuration for each platform.
- Use a build without GPL-only components if the app is not distributed under the GPL.
- Include the FFmpeg license text and the source offer or source location in the distribution.
- Keep the license files next to the binaries in the publish output.

Final license choice and attribution text are TODO and require review before release.
