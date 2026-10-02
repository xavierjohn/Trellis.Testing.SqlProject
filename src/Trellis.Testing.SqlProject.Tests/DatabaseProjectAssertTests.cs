namespace Trellis.Testing.SqlProject.Tests;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

public sealed class DatabaseProjectAssertTests : IDisposable
{
    private const string ProjectSchema = """
        CREATE TABLE [dbo].[Places]
        (
            [Id] int NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [LocatedIn] nvarchar(100) NULL,
            CONSTRAINT [PK_Places] PRIMARY KEY ([Id])
        );
        """;

    private const string ProjectSchemaWithLocatedInBeforeName = """
        CREATE TABLE [dbo].[Places]
        (
            [Id] int NOT NULL,
            [LocatedIn] nvarchar(100) NULL,
            [Name] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_Places] PRIMARY KEY ([Id])
        );
        """;

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }

    [Fact]
    public void Compare_ProjectMatchesModel_ReturnsEmptyReport() =>
        Compare(ProjectSchema).Should().BeEmpty();

    [Fact]
    public void Compare_ModelNeedsNoRunningDatabase_ReturnsEmptyReport()
    {
        using var context = new PlacesContext("Server=unreachable.invalid;Database=Nothing;Connect Timeout=1");

        var report = DatabaseProjectAssert.Compare(context, BuildProject(ProjectSchema), cancellationToken: TestContext.Current.CancellationToken);

        report.Should().BeEmpty();
    }

    [Fact]
    public void Compare_ColumnOrderDiffers_ReportsPositionInEachSide()
    {
        var report = Compare(ProjectSchemaWithLocatedInBeforeName);

        report.Should().Contain(
            "Column [dbo].[Places].[LocatedIn] is at position 2 in the SQL project but 3 in the EF model");
        report.Should().Contain(
            "Column [dbo].[Places].[Name] is at position 3 in the SQL project but 2 in the EF model");
    }

    [Fact]
    public void Compare_ColumnOrderDiffers_IgnoreColumnOrder_ReturnsEmptyReport() =>
        Compare(ProjectSchemaWithLocatedInBeforeName, new DatabaseProjectAssertOptions { IgnoreColumnOrder = true })
            .Should().BeEmpty();

    [Fact]
    public void Compare_ColumnOrderOptOut_WithRealDrift_ReportsDriftWithoutPositions()
    {
        var project = ProjectSchemaWithLocatedInBeforeName.Replace("[Name] nvarchar(100)", "[Name] nvarchar(200)");

        var report = Compare(project, new DatabaseProjectAssertOptions { IgnoreColumnOrder = true });

        report.Should().Contain(line => line.Contains("[Name].Length", StringComparison.Ordinal));
        report.Should().NotContain(line => line.Contains("is at position", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ColumnLengthDiffers_ReportsBothValues()
    {
        var project = ProjectSchema.Replace("[Name] nvarchar(100)", "[Name] nvarchar(200)");

        Compare(project).Should().Contain(
            "[dbo].[Places].[Name].Length: SQL project=200; EF model=100");
    }

    [Fact]
    public void Compare_NullabilityDiffers_ReportsBothValues()
    {
        var project = ProjectSchema.Replace("[Name] nvarchar(100) NOT NULL", "[Name] nvarchar(100) NULL");

        Compare(project).Should().Contain(
            "[dbo].[Places].[Name].IsNullable: SQL project=true; EF model=false");
    }

    [Fact]
    public void Compare_DataTypeDiffers_ReportsBothTypes()
    {
        var project = ProjectSchema.Replace("[Name] nvarchar(100)", "[Name] varchar(100)");

        Compare(project).Should().Contain(line =>
            line.Contains("[varchar]", StringComparison.Ordinal) && line.Contains("[nvarchar]", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ColumnOnlyInProject_ReportsMissingFromModel()
    {
        var project = ProjectSchema.Replace(
            "[LocatedIn] nvarchar(100) NULL,",
            "[LocatedIn] nvarchar(100) NULL,\n    [Extra] int NULL,");

        Compare(project).Should().Contain(
            "Column [dbo].[Places].[Extra] is in the SQL project but not in the EF model");
    }

    [Fact]
    public void Compare_ColumnOnlyInModel_ReportsMissingFromProject()
    {
        var project = ProjectSchema.Replace("[LocatedIn] nvarchar(100) NULL,", string.Empty);

        Compare(project).Should().Contain(
            "Column [dbo].[Places].[LocatedIn] is in the EF model but not in the SQL project");
    }

    [Fact]
    public void Compare_TableOnlyInProject_ReportsMissingFromModel()
    {
        var project = ProjectSchema + "\nGO\nCREATE TABLE [dbo].[Orphans] ([Id] int NOT NULL);";

        Compare(project).Should().Contain(
            "Table [dbo].[Orphans] is in the SQL project but not in the EF model");
    }

    [Theory]
    [InlineData("AutoCreatedLocal")]
    [InlineData("AlwaysOn_health")]
    [InlineData("AutoCreatedLocalCopy")]
    public void Compare_ProjectTableNamedLikeASystemObject_ReportsTable(string tableName)
    {
        var project = ProjectSchema + $"{Environment.NewLine}GO{Environment.NewLine}CREATE TABLE [dbo].[{tableName}] ([Id] int NOT NULL);";

        Compare(project).Should().Contain(
            $"Table [dbo].[{tableName}] is in the SQL project but not in the EF model");
    }

    private const string ShopProjectSchema = """
        CREATE SCHEMA [sales];
        GO
        CREATE TABLE [sales].[Customers]
        (
            [Id] int NOT NULL IDENTITY,
            [Name] nvarchar(max) NOT NULL,
            CONSTRAINT [PK_Customers] PRIMARY KEY ([Id])
        );
        GO
        CREATE TABLE [sales].[Orders]
        (
            [Id] int NOT NULL IDENTITY,
            [Number] nvarchar(450) NOT NULL,
            [CustomerId] int NOT NULL,
            [Total] decimal(18,2) NOT NULL,
            CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
            CONSTRAINT [FK_Orders_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [sales].[Customers] ([Id]) ON DELETE CASCADE
        );
        GO
        CREATE INDEX [IX_Orders_CustomerId] ON [sales].[Orders] ([CustomerId]);
        GO
        CREATE UNIQUE INDEX [IX_Orders_Number] ON [sales].[Orders] ([Number]);
        """;

    [Fact]
    public void Compare_NonDefaultSchemaIdentityForeignKeyAndIndexes_ReturnsEmptyReport()
    {
        using var context = new ShopContext();

        DatabaseProjectAssert.Compare(
                context, BuildProject(ShopProjectSchema), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_IndexUniquenessDiffers_ReportsBothValues()
    {
        using var context = new ShopContext();
        var project = ShopProjectSchema.Replace("CREATE UNIQUE INDEX", "CREATE INDEX");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().Contain("[sales].[Orders].[IX_Orders_Number].IsUnique: SQL project=false; EF model=true");
    }

    [Fact]
    public void Compare_ForeignKeyDeleteActionDiffers_ReportsBothValues()
    {
        using var context = new ShopContext();
        var project = ShopProjectSchema.Replace("ON DELETE CASCADE", "ON DELETE NO ACTION");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().Contain(
                "[sales].[FK_Orders_Customers_CustomerId].OnDeleteAction: SQL project=NoAction; EF model=Cascade");
    }

    [Fact]
    public void Compare_NoReportedValueIsUnavailable()
    {
        using var context = new ShopContext();
        var project = ShopProjectSchema
            .Replace("CREATE UNIQUE INDEX", "CREATE INDEX")
            .Replace("ON DELETE CASCADE", "ON DELETE NO ACTION");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().NotContain(line => line.Contains("unavailable", StringComparison.Ordinal));
    }

    private const string DefaultsSchema = """
        CREATE TABLE [dbo].[Defaults]
        (
            [Id] int NOT NULL,
            [Score] int NOT NULL CONSTRAINT [DF_Defaults_Score] DEFAULT (5),
            [Created] datetime2 NOT NULL CONSTRAINT [DF_Defaults_Created] DEFAULT (getdate()),
            [Label] nvarchar(10) NOT NULL CONSTRAINT [DF_Defaults_Label] DEFAULT ('a'),
            [A] int NOT NULL,
            [B] int NOT NULL,
            [Sum] AS ([A] + [B]),
            CONSTRAINT [PK_Defaults] PRIMARY KEY ([Id])
        );
        """;

    [Fact]
    public void Compare_NamedDefaultConstraintsAgainstEfUnnamedDefaults_ReturnsEmptyReport()
    {
        using var context = new DefaultsContext();

        DatabaseProjectAssert.Compare(
                context, BuildProject(DefaultsSchema), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty("EF emits unnamed defaults, so a project that names its defaults must still match");
    }

    [Fact]
    public void Compare_DefaultExpressionDiffers_ReportsBothValues()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("DEFAULT (5)", "DEFAULT (6)");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[Score].DefaultExpression: SQL project=6; EF model=5");
    }

    [Fact]
    public void Compare_DefaultOnlyInModel_ReportsMissingFromProject()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace(" CONSTRAINT [DF_Defaults_Score] DEFAULT (5)", string.Empty);

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[Score].DefaultExpression: SQL project=(none); EF model=5");
    }

    [Fact]
    public void Compare_DefaultOnlyInProject_ReportsMissingFromModel()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("[A] int NOT NULL,", "[A] int NOT NULL CONSTRAINT [DF_Defaults_A] DEFAULT (1),");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[A].DefaultExpression: SQL project=1; EF model=(none)");
    }

    [Fact]
    public void Compare_DefaultDiffersOnlyByCaseOutsideLiterals_ReturnsEmptyReport()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("DEFAULT (getdate())", "DEFAULT (GETDATE())");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_ComputedColumnExpressionDiffers_ReportsBothValues()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("AS ([A] + [B])", "AS ([A] * [B])");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[Sum].ExpressionScript: SQL project=[A] * [B]; EF model=[A] + [B]");
    }

    [Fact]
    public void Compare_CheckConstraintExpressionDiffers_ReportsBothValues()
    {
        using var context = new CheckedContext();
        const string project = """
            CREATE TABLE [dbo].[Checked] ([Id] int NOT NULL, [Qty] int NOT NULL, CONSTRAINT [PK_Checked] PRIMARY KEY ([Id]));
            GO
            ALTER TABLE [dbo].[Checked] ADD CONSTRAINT [CK_Checked_Qty] CHECK ([Qty] > 10);
            """;

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[CK_Checked_Qty].CheckExpressionScript: SQL project=[Qty] > 10; EF model=[Qty] > 0");
    }

    [Theory]
    [InlineData("[dbo].[Defaults]", "[dbo].[defaults]", "[dbo].[defaults].[Score]")]
    [InlineData("[dbo].[Defaults]", "[DBO].[Defaults]", "[DBO].[Defaults].[Score]")]
    public void Compare_DefaultDiffers_ProjectUsesDifferentTableOrSchemaCasing_ReportsDefault(
        string modelQualifiedName, string projectQualifiedName, string expectedColumn)
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace(modelQualifiedName, projectQualifiedName).Replace("DEFAULT (5)", "DEFAULT (6)");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be($"{expectedColumn}.DefaultExpression: SQL project=6; EF model=5");
    }

    [Fact]
    public void Compare_DefaultDiffers_ProjectUsesDifferentColumnCasing_ReportsDefault()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("[Score] int", "[score] int").Replace("DEFAULT (5)", "DEFAULT (6)");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[score].DefaultExpression: SQL project=6; EF model=5");
    }

    [Fact]
    public void Compare_ColumnOrderDiffers_ProjectUsesDifferentColumnCasing_ReportsPositions()
    {
        var project = ProjectSchemaWithLocatedInBeforeName.Replace("[LocatedIn]", "[locatedin]").Replace("[Name]", "[name]");

        var report = Compare(project);

        report.Should().Contain("Column [dbo].[Places].[locatedin] is at position 2 in the SQL project but 3 in the EF model");
        report.Should().Contain("Column [dbo].[Places].[name] is at position 3 in the SQL project but 2 in the EF model");
    }

    [Fact]
    public void Compare_DefaultDiffers_CaseSensitiveProject_TreatsDifferentCasingAsDifferentColumns()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("[Score] int", "[score] int").Replace("DEFAULT (5)", "DEFAULT (6)");

        DatabaseProjectAssert.Compare(
                context,
                BuildProject(project, "SQL_Latin1_General_CP1_CS_AS"),
                cancellationToken: TestContext.Current.CancellationToken)
            .Should().NotContain(line => line.Contains("DefaultExpression", StringComparison.Ordinal))
            .And.Contain(line => line.Contains("Column", StringComparison.Ordinal));
    }

    private const string CaseSensitiveCollation = "SQL_Latin1_General_CP1_CS_AS";

    private const string CaseSensitiveTablesSchema = """
        CREATE TABLE [dbo].[Cases] ([Id] int NOT NULL, CONSTRAINT [PK_Cases] PRIMARY KEY ([Id]));
        GO
        CREATE TABLE [dbo].[cases] ([Id] int NOT NULL, CONSTRAINT [PK_cases] PRIMARY KEY ([Id]));
        """;

    [Fact]
    public void Compare_CaseSensitiveProject_TablesDifferingOnlyByCase_ReturnsEmptyReport()
    {
        using var context = new CaseCollidingTablesContext();

        DatabaseProjectAssert.Compare(
                context,
                BuildProject(CaseSensitiveTablesSchema, CaseSensitiveCollation),
                cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_CaseSensitiveProject_OneOfTwoCaseCollidingTablesMissing_ReportsOnlyThatTable()
    {
        using var context = new CaseCollidingTablesContext();
        var project = """
            CREATE TABLE [dbo].[Cases] ([Id] int NOT NULL, CONSTRAINT [PK_Cases] PRIMARY KEY ([Id]));
            """;

        var report = DatabaseProjectAssert.Compare(
            context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken);

        report.Should().Contain("Table [dbo].[cases] is in the EF model but not in the SQL project");
        report.Should().NotContain(line => line.Contains("[dbo].[Cases]", StringComparison.Ordinal) && line.StartsWith("Table", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_CaseSensitiveProject_ColumnsDifferingOnlyByCase_ReturnsEmptyReport()
    {
        using var context = new CaseCollidingColumnsContext();
        const string project = """
            CREATE TABLE [dbo].[Mixed] ([Id] int NOT NULL, [Value] int NOT NULL, [value] int NOT NULL, CONSTRAINT [PK_Mixed] PRIMARY KEY ([Id]));
            """;

        DatabaseProjectAssert.Compare(
                context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    private const string SequencesProject = """
        CREATE SEQUENCE [dbo].[Seq] AS bigint START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE NO CYCLE;
        GO
        CREATE SEQUENCE [dbo].[seq] AS bigint START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE NO CYCLE;
        GO
        CREATE TABLE [dbo].[Counters] ([Id] bigint NOT NULL CONSTRAINT [DF_Counters_Id] DEFAULT (NEXT VALUE FOR [dbo].[Seq]), CONSTRAINT [PK_Counters] PRIMARY KEY ([Id]));
        """;

    private const string SingleSequenceProject = """
        CREATE SEQUENCE [dbo].[Seq] AS bigint START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE NO CYCLE;
        GO
        CREATE TABLE [dbo].[Counters] ([Id] bigint NOT NULL CONSTRAINT [DF_Counters_Id] DEFAULT (NEXT VALUE FOR [dbo].[Seq]), CONSTRAINT [PK_Counters] PRIMARY KEY ([Id]));
        """;

    [Fact]
    public void Compare_CaseSensitiveProject_DefaultsReferenceSameSequence_ReturnsEmptyReport()
    {
        using var context = new CaseCollidingSequencesContext();

        DatabaseProjectAssert.Compare(
                context, BuildProject(SequencesProject, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_CaseSensitiveProject_DefaultsReferenceCaseCollidingSequences_ReportsDefault()
    {
        using var context = new CaseCollidingSequencesContext();
        var project = SequencesProject.Replace("DEFAULT (NEXT VALUE FOR [dbo].[Seq])", "DEFAULT (NEXT VALUE FOR [dbo].[seq])");

        DatabaseProjectAssert.Compare(
                context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Counters].[Id].DefaultExpression: SQL project=NEXT VALUE FOR [dbo].[seq]; EF model=NEXT VALUE FOR [dbo].[Seq]");
    }

    [Fact]
    public void Compare_CaseSensitiveProject_DefaultsDifferOnlyByKeywordCase_ReturnsEmptyReport()
    {
        using var context = new CaseCollidingSequencesContext();
        var project = SequencesProject.Replace("NEXT VALUE FOR", "next value for");

        DatabaseProjectAssert.Compare(
                context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty("keywords are case-insensitive whatever the collation");
    }

    [Fact]
    public void Compare_CaseSensitiveProject_DefaultsDifferOnlyByBuiltInFunctionCase_ReturnsEmptyReport()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("DEFAULT (getdate())", "DEFAULT (GetDate())");

        DatabaseProjectAssert.Compare(
                context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty("built-in function names are case-insensitive whatever the collation");
    }

    [Theory]
    [InlineData("NEXT VALUE FOR [dbo].[SEQ]")]
    [InlineData("NEXT VALUE FOR dbo.Seq")]
    [InlineData("NEXT VALUE FOR [DBO].[seq]")]
    public void Compare_CaseInsensitiveProject_DefaultsDifferOnlyByIdentifierCaseOrQuoting_ReturnsEmptyReport(string projectExpression)
    {
        using var context = new SingleSequenceContext();
        var project = SingleSequenceProject.Replace("NEXT VALUE FOR [dbo].[Seq]", projectExpression);

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_DefaultUnicodeLiteralPrefixDiffersOnlyByCase_ReturnsEmptyReport()
    {
        using var context = new UnicodeDefaultsContext();
        const string project = """
            CREATE TABLE [dbo].[UnicodeDefaults]
            (
                [Id] int NOT NULL,
                [Label] nvarchar(10) NOT NULL CONSTRAINT [DF_UnicodeDefaults_Label] DEFAULT (n'a'),
                CONSTRAINT [PK_UnicodeDefaults] PRIMARY KEY ([Id])
            );
            """;

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_CaseSensitiveProject_DefaultCollationNameDiffersOnlyByCase_ReturnsEmptyReport()
    {
        using var context = new CollatedDefaultsContext();
        const string project = """
            CREATE TABLE [dbo].[CollatedDefaults]
            (
                [Id] int NOT NULL,
                [Label] nvarchar(10) NOT NULL CONSTRAINT [DF_CollatedDefaults_Label] DEFAULT ('a' COLLATE Latin1_General_100_CS_AS),
                CONSTRAINT [PK_CollatedDefaults] PRIMARY KEY ([Id])
            );
            """;

        DatabaseProjectAssert.Compare(
                context, BuildProject(project, CaseSensitiveCollation), cancellationToken: TestContext.Current.CancellationToken)
            .Should().BeEmpty("collation names are case-insensitive whatever the project collation");
    }

    [Fact]
    public void Compare_DefaultStringLiteralDiffersByCase_ReportsDefault()
    {
        using var context = new DefaultsContext();
        var project = DefaultsSchema.Replace("DEFAULT ('a')", "DEFAULT ('A')");

        DatabaseProjectAssert.Compare(context, BuildProject(project), cancellationToken: TestContext.Current.CancellationToken)
            .Should().ContainSingle()
            .Which.Should().Be("[dbo].[Defaults].[Label].DefaultExpression: SQL project='A'; EF model='a'");
    }

    [Fact]
    public void Compare_Cancelled_ThrowsBeforeReadingAnything()
    {
        using var context = new PlacesContext();

        var act = () => DatabaseProjectAssert.Compare(
            context, "does-not-exist.dacpac", cancellationToken: new CancellationToken(canceled: true));

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Compare_MissingDacpac_ThrowsFileNotFoundNamingTheFile()
    {
        using var context = new PlacesContext();

        var act = () => DatabaseProjectAssert.Compare(
            context, "does-not-exist.dacpac", cancellationToken: TestContext.Current.CancellationToken);

        act.Should().Throw<FileNotFoundException>().WithMessage("*does-not-exist.dacpac*");
    }

    [Fact]
    public void Compare_NullContext_Throws()
    {
        var act = () => DatabaseProjectAssert.Compare(null!, "x.dacpac");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MatchesModel_ProjectMatchesModel_DoesNotThrow()
    {
        using var context = new PlacesContext();

        var act = () => DatabaseProjectAssert.MatchesModel(context, BuildProject(ProjectSchema));

        act.Should().NotThrow();
    }

    [Fact]
    public void MatchesModel_ProjectDrifted_ThrowsListingEveryDifference()
    {
        using var context = new PlacesContext();
        var project = ProjectSchemaWithLocatedInBeforeName.Replace("[Name] nvarchar(100)", "[Name] nvarchar(200)");

        var act = () => DatabaseProjectAssert.MatchesModel(context, BuildProject(project));

        var exception = act.Should().Throw<DatabaseProjectDriftException>().Which;
        exception.Message.Should()
            .Contain("[dbo].[Places].[Name].Length: SQL project=200; EF model=100")
            .And.Contain("is at position 2 in the SQL project but 3 in the EF model");
        exception.Differences.Should().Contain("[dbo].[Places].[Name].Length: SQL project=200; EF model=100");
    }

    [Fact]
    public void ShippedAssembly_DoesNotReferenceFluentAssertions() =>
        typeof(DatabaseProjectAssert).Assembly.GetReferencedAssemblies()
            .Should().NotContain(
                assembly => assembly.Name!.StartsWith("FluentAssertions", StringComparison.Ordinal),
                "a consumer on a different FluentAssertions major would otherwise get a TypeLoadException");

    private IReadOnlyList<string> Compare(string projectSql, DatabaseProjectAssertOptions? options = null)
    {
        using var context = new PlacesContext();
        return DatabaseProjectAssert.Compare(
            context, BuildProject(projectSql), options, TestContext.Current.CancellationToken);
    }

    private string BuildProject(string sql, string? collation = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SqlProjectTests_{Guid.NewGuid():N}.dacpac");
        _tempFiles.Add(path);
        var modelOptions = collation is null ? new TSqlModelOptions() : new TSqlModelOptions { Collation = collation };
        using var model = new TSqlModel(SqlServerVersion.Sql160, modelOptions);
        model.AddObjects(sql);
        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata { Name = "SqlProjectTests", Version = "1.0.0" });
        return path;
    }

    private sealed class Place
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? LocatedIn { get; set; }
    }

    private sealed class PlacesContext(string? connectionString = null) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer(connectionString ?? "Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Place>(place =>
            {
                place.ToTable("Places");
                place.Property(p => p.Id).ValueGeneratedNever();
                place.Property(p => p.Name).HasMaxLength(100);
                place.Property(p => p.LocatedIn).HasMaxLength(100);
            });
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public string Number { get; set; } = string.Empty;

        public int CustomerId { get; set; }

        public Customer? Customer { get; set; }

        public decimal Total { get; set; }
    }

    private sealed class ShopContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToTable("Customers", "sales");
            modelBuilder.Entity<Order>(order =>
            {
                order.ToTable("Orders", "sales");
                order.HasIndex(o => o.Number).IsUnique();
                order.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
            });
        }
    }

    private sealed class DefaultsRow
    {
        public int Id { get; set; }

        public int Score { get; set; }

        public DateTime Created { get; set; }

        public string Label { get; set; } = string.Empty;

        public int A { get; set; }

        public int B { get; set; }

        public int Sum { get; set; }
    }

    private sealed class DefaultsContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<DefaultsRow>(row =>
            {
                row.ToTable("Defaults");
                row.Property(r => r.Id).ValueGeneratedNever();
                row.Property(r => r.Score).HasDefaultValueSql("5");
                row.Property(r => r.Created).HasDefaultValueSql("GETDATE()");
                row.Property(r => r.Label).HasMaxLength(10).HasDefaultValueSql("'a'");
                row.Property(r => r.Sum).HasComputedColumnSql("[A] + [B]");
            });
    }

    private sealed class CheckedRow
    {
        public int Id { get; set; }

        public int Qty { get; set; }
    }

    private sealed class CheckedContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CheckedRow>(row =>
            {
                row.ToTable("Checked", table => table.HasCheckConstraint("CK_Checked_Qty", "[Qty] > 0"));
                row.Property(r => r.Id).ValueGeneratedNever();
            });
    }

    private sealed class UpperCaseTable
    {
        public int Id { get; set; }
    }

    private sealed class LowerCaseTable
    {
        public int Id { get; set; }
    }

    private sealed class CaseCollidingTablesContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UpperCaseTable>(table =>
            {
                table.ToTable("Cases");
                table.Property(t => t.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<LowerCaseTable>(table =>
            {
                table.ToTable("cases");
                table.Property(t => t.Id).ValueGeneratedNever();
            });
        }
    }

    private sealed class MixedRow
    {
        public int Id { get; set; }

        public int Value { get; set; }

        public int ValueLower { get; set; }
    }

    private sealed class CaseCollidingColumnsContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<MixedRow>(row =>
            {
                row.ToTable("Mixed");
                row.Property(r => r.Id).ValueGeneratedNever();
                row.Property(r => r.Value).HasColumnName("Value");
                row.Property(r => r.ValueLower).HasColumnName("value");
            });
    }

    private sealed class UnicodeDefaultsRow
    {
        public int Id { get; set; }

        public string Label { get; set; } = string.Empty;
    }

    private sealed class UnicodeDefaultsContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<UnicodeDefaultsRow>(row =>
            {
                row.ToTable("UnicodeDefaults");
                row.Property(r => r.Id).ValueGeneratedNever();
                row.Property(r => r.Label).HasMaxLength(10).HasDefaultValueSql("N'a'");
            });
    }

    private sealed class CollatedDefaultsRow
    {
        public int Id { get; set; }

        public string Label { get; set; } = string.Empty;
    }

    private sealed class CollatedDefaultsContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CollatedDefaultsRow>(row =>
            {
                row.ToTable("CollatedDefaults");
                row.Property(r => r.Id).ValueGeneratedNever();
                row.Property(r => r.Label).HasMaxLength(10).HasDefaultValueSql("'a' COLLATE latin1_general_100_cs_as");
            });
    }

    private sealed class Counter
    {
        public long Id { get; set; }
    }

    private sealed class CaseCollidingSequencesContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<long>("Seq");
            modelBuilder.HasSequence<long>("seq");
            modelBuilder.Entity<Counter>(counter =>
            {
                counter.ToTable("Counters");
                counter.Property(c => c.Id).HasDefaultValueSql("NEXT VALUE FOR [dbo].[Seq]");
            });
        }
    }

    private sealed class SingleSequenceContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=.;Database=Unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<long>("Seq");
            modelBuilder.Entity<Counter>(counter =>
            {
                counter.ToTable("Counters");
                counter.Property(c => c.Id).HasDefaultValueSql("NEXT VALUE FOR [dbo].[Seq]");
            });
        }
    }
}
