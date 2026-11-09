namespace Garou.ViewModels;

public sealed record FolderBreadcrumb(string Title, string Path)
{
    public override string ToString() => Title;
}
