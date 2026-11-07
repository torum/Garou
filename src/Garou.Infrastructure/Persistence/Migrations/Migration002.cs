namespace Garou.Infrastructure.Persistence.Migrations;

internal static class Migration002
{
    public const string Sql =
        """
        CREATE TABLE Library
        (
            Id TEXT NOT NULL PRIMARY KEY,
            RootPath TEXT NOT NULL UNIQUE,
            IsEnabled INTEGER NOT NULL DEFAULT 1
        );
        """;
}
