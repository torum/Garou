namespace Garou.Domain.Models;

public static class MediaFileExtensions
{
    public static IReadOnlyList<string> Images { get; } = Array.AsReadOnly<string>([
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".gif", ".bmp", ".dib", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif", ".jxl", ".jp2", ".j2k", ".j2c", ".jpc", ".jpx", ".pbm",
        ".pgm", ".ppm", ".pnm", ".pam", ".pfm", ".pcx", ".tga", ".targa", ".sgi", ".exr", ".dpx",
        ".cin", ".dds", ".fits", ".hdr", ".ico", ".cur", ".ras", ".sun", ".xbm", ".xpm", ".qoi",
        ".psd", ".psb", ".jng", ".mng"
    ]);

    public static IReadOnlyList<string> Videos { get; } = Array.AsReadOnly<string>([
        ".3g2", ".3gp", ".amv", ".asf", ".avi", ".divx", ".dv", ".dvr-ms", ".f4v", ".fli", ".flc",
        ".flv", ".gxf", ".h264", ".264", ".h265", ".265", ".hevc", ".ivf", ".m1v", ".m2p", ".m2t",
        ".m2ts", ".m2v", ".m4v", ".mjpeg", ".mjpg", ".mkv", ".mov", ".mp4", ".mpe", ".mpeg",
        ".mpg", ".mpv", ".mxf", ".nut", ".nsv", ".ogv", ".qt", ".rm", ".rmvb", ".roq", ".swf",
        ".ts", ".mts", ".vdr", ".webm", ".wmv", ".wtv", ".y4m"
    ]);

    public static IReadOnlyList<string> Audio { get; } = Array.AsReadOnly<string>([
        ".3ga", ".8svx", ".aa", ".aac", ".aax", ".ac3", ".ac4", ".aif", ".aiff", ".alac", ".amr",
        ".ape", ".ast", ".au", ".caf", ".dts", ".eac3", ".flac", ".gsm", ".ircam", ".m4a", ".m4b",
        ".m4p", ".m4r", ".mid", ".midi", ".mka", ".mlp", ".mp2", ".mp3", ".mpa", ".mpc", ".mpp",
        ".mp+", ".oga", ".ogg", ".opus", ".ra", ".rf64", ".sds", ".sph", ".spx", ".tak", ".tta",
        ".voc", ".vox", ".wav", ".wave", ".w64", ".wma", ".wv", ".weba"
    ]);

    public static IReadOnlyList<string> LegacyAll { get; } = Array.AsReadOnly<string>([
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg",
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg", ".opus", ".aif", ".aiff"
    ]);

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        Images.Concat(Videos).Concat(Audio).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}
