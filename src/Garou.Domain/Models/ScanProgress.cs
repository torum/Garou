namespace Garou.Domain.Models;

public sealed record ScanProgress(string Phase, int Processed, int Total);
