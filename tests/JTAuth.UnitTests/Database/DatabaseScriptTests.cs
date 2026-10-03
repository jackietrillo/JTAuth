using System.Text.RegularExpressions;
using JTAuth.Database;
using Shouldly;

namespace JTAuth.UnitTests.Database;

public sealed partial class DatabaseScriptTests
{
    private static readonly string DatabaseFolder = Path.Combine(RepoRoot.Path, "database");

    private static FileInfo[] ScriptFiles(string kind) =>
        new DirectoryInfo(Path.Combine(DatabaseFolder, kind)).GetFiles("*.sql").OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("migrations")]
    [InlineData("seed")]
    public void ScriptsAreNumberedFromOneWithoutGaps(string kind)
    {
        var numbers = ScriptFiles(kind)
            .Select(file => NumberedScriptName().Match(file.Name))
            .Select(match => match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1)
            .ToList();

        numbers.ShouldNotBeEmpty();
        numbers.ShouldBe(Enumerable.Range(1, numbers.Count), $"{kind} scripts must be named NNNN_description.sql, numbered 0001, 0002, ...");
    }

    [Theory]
    [InlineData("migrations", DatabaseDeployer.MigrationsPrefix)]
    [InlineData("seed", DatabaseDeployer.SeedPrefix)]
    public void EveryScriptIsEmbeddedInTheMigrator(string kind, string prefix)
    {
        typeof(DatabaseDeployer).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ShouldBe(ScriptFiles(kind).Select(file => prefix + file.Name));
    }

    [Fact]
    public void SchemaReferenceContainsEveryTableFromTheMigrations()
    {
        var reference = File.ReadAllText(Path.Combine(DatabaseFolder, "schema.sql"));
        var tables = ScriptFiles("migrations")
            .SelectMany(file => CreateTable().Matches(File.ReadAllText(file.FullName)))
            .Select(match => match.Value)
            .ToList();

        tables.ShouldNotBeEmpty();
        tables.Where(statement => !reference.Contains(statement, StringComparison.Ordinal))
            .ShouldBeEmpty("database/schema.sql is out of date with database/migrations.");
    }

    [Fact]
    public void EverythingLivesInDboAndThereAreNoPasswords()
    {
        foreach (var file in ScriptFiles("migrations").Concat(ScriptFiles("seed")))
        {
            var sql = File.ReadAllText(file.FullName);
            sql.ShouldNotContain("CREATE SCHEMA", Case.Insensitive, $"{file.Name}: use dbo.");
            Regex.IsMatch(sql, @"\bPassword\w*\s+(n?varchar|varbinary|char|binary)", RegexOptions.IgnoreCase)
                .ShouldBeFalse($"{file.Name}: JTAuth is passwordless; there is no password column anywhere.");
        }
    }

    [GeneratedRegex(@"^(\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex NumberedScriptName();

    [GeneratedRegex(@"CREATE TABLE dbo\.\[?\w+\]?")]
    private static partial Regex CreateTable();
}
