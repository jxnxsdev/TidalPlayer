# Third-party notices

The TidalMpvPlayer source is licensed under Apache-2.0. Release builds also
contain MPV and its FFmpeg-based runtime. These are separate third-party
components and are not relicensed under Apache-2.0.

| Component | License and source |
| --- | --- |
| MPV | GPL-2.0-or-later; https://github.com/mpv-player/mpv |
| FFmpeg | LGPL-2.1-or-later, with optional GPL components depending on build configuration; https://ffmpeg.org/legal.html |
| .NET runtime | MIT; https://github.com/dotnet/runtime; license and bundled dependency notices are in `licenses/` |

MPV is downloaded from the platform's package distribution during each
workflow build. The precise MPV version and build configuration are recorded
in `MPV-BUILD-INFO.txt` inside each release archive. Preserve the license and
source notices included with that MPV distribution when redistributing it.
The host operating system's base libraries and audio services remain platform
requirements.

The `licenses` directory contains the GNU GPL-2.0, GPL-3.0, and LGPL-2.1
license texts for applicable MPV/FFmpeg build configurations. The distribution
package's own notices and source availability apply to its bundled dependencies.
