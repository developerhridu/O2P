using O2P.Application.Schema;
using O2P.Domain.Entities;
using Xunit;

namespace O2P.Application.Tests;

public class TargetSchemaComparerTests
{
    // ---- helpers -----------------------------------------------------------

    private static ManifestTable Manifest(params ManifestColumn[] columns)
    {
        var table = new ManifestTable { Owner = "HR", TableName = "EMPLOYEES" };
        foreach (var column in columns) table.Columns.Add(column);
        return table;
    }

    private static ManifestColumn Column(string name, string postgresType, bool nullable = true, bool excluded = false, long id = 0) =>
        new()
        {
            Id = id,
            ColumnName = name,
            OracleDataType = "VARCHAR2",
            PostgresDataType = postgresType,
            IsNullable = nullable,
            IsExcluded = excluded
        };

    private static PostgresLiveColumn Live(
        string name,
        string formattedType,
        bool notNull = false,
        bool hasDefault = false,
        bool isIdentity = false,
        bool isGenerated = false) =>
        new(name, formattedType, notNull, hasDefault, isIdentity, isGenerated);

    private static IReadOnlyList<SchemaMismatch> Compare(ManifestColumn manifestColumn, PostgresLiveColumn liveColumn) =>
        TargetSchemaComparer.Compare(Manifest(manifestColumn), new[] { liveColumn });

    private static void AssertCompatible(string manifestType, string targetType)
    {
        var problems = Compare(Column("C", manifestType), Live("C", targetType));
        Assert.False(
            TargetSchemaComparer.HasBlockingProblem(problems),
            $"{manifestType} -> {targetType} should load, but: {string.Join(" | ", problems.Select(p => p.Message))}");
    }

    private static void AssertBlocked(string manifestType, string targetType)
    {
        var problems = Compare(Column("C", manifestType), Live("C", targetType));
        Assert.True(
            TargetSchemaComparer.HasBlockingProblem(problems),
            $"{manifestType} -> {targetType} should be blocked, but nothing was reported.");
    }

    // ---- normalisation: every TypeMapper output against its own format_type -

    [Theory]
    [InlineData("varchar(255)", "character varying(255)")]
    [InlineData("text", "text")]
    [InlineData("char(10)", "character(10)")]
    [InlineData("smallint", "smallint")]
    [InlineData("integer", "integer")]
    [InlineData("bigint", "bigint")]
    [InlineData("numeric(18)", "numeric(18,0)")]          // TypeMapper omits the scale, format_type adds it
    [InlineData("numeric(10, 2)", "numeric(10,2)")]       // TypeMapper puts a space in, format_type does not
    [InlineData("numeric", "numeric")]
    [InlineData("timestamp(0) without time zone", "timestamp(0) without time zone")]
    [InlineData("timestamp without time zone", "timestamp without time zone")]
    [InlineData("timestamp with time zone", "timestamp with time zone")]
    [InlineData("bytea", "bytea")]
    [InlineData("real", "real")]
    [InlineData("double precision", "double precision")]
    [InlineData("varchar(4000)", "character varying(4000)")]
    [InlineData("xml", "xml")]
    [InlineData("interval", "interval")]
    public void RoundTripsEveryTypeMapperOutput(string manifestType, string formatTypeOutput)
    {
        AssertCompatible(manifestType, formatTypeOutput);
    }

    [Theory]
    [InlineData("int4", "integer")]
    [InlineData("int8", "bigint")]
    [InlineData("float8", "double precision")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("bpchar", "character")]
    public void FoldsTypeAliases(string alias, string canonical)
    {
        AssertCompatible(alias, canonical);
    }

    // ---- fixed-width types: widening is NOT safe ---------------------------

    [Theory]
    [InlineData("integer", "bigint")]        // the counter-intuitive one: 4 bytes into int8recv
    [InlineData("smallint", "integer")]
    [InlineData("bigint", "integer")]
    [InlineData("real", "double precision")]
    [InlineData("integer", "numeric(10,0)")]
    [InlineData("smallint", "boolean")]
    public void BlocksFixedWidthTypeChanges(string manifestType, string targetType)
    {
        AssertBlocked(manifestType, targetType);
    }

    // ---- numeric -----------------------------------------------------------

    [Theory]
    [InlineData("numeric(10, 2)", "numeric(12,2)")]
    [InlineData("numeric(10, 2)", "numeric(12,4)")]
    [InlineData("numeric(10, 2)", "numeric")]
    public void AllowsWiderNumeric(string manifestType, string targetType) => AssertCompatible(manifestType, targetType);

    [Theory]
    [InlineData("numeric(10, 2)", "numeric(8,2)")]    // fewer digits overall
    [InlineData("numeric(10, 2)", "numeric(10,4)")]   // same precision, but 2 fewer whole digits
    [InlineData("numeric(10, 2)", "numeric(10,1)")]   // scale shrinks: Postgres rounds silently
    [InlineData("numeric", "numeric(10,2)")]          // unbounded source into a bounded target
    public void BlocksNarrowerNumeric(string manifestType, string targetType) => AssertBlocked(manifestType, targetType);

    // ---- strings -----------------------------------------------------------

    [Theory]
    [InlineData("varchar(50)", "character varying(100)")]
    [InlineData("varchar(50)", "text")]
    [InlineData("text", "text")]
    [InlineData("xml", "text")]
    public void AllowsWiderStrings(string manifestType, string targetType) => AssertCompatible(manifestType, targetType);

    [Theory]
    [InlineData("varchar(50)", "character varying(10)")]
    [InlineData("text", "character varying(100)")]     // unbounded source
    [InlineData("varchar(50)", "bytea")]               // accepted silently, stores corrupt bytes
    [InlineData("text", "xml")]
    [InlineData("bytea", "text")]
    public void BlocksNarrowerOrWrongFamilyStrings(string manifestType, string targetType) => AssertBlocked(manifestType, targetType);

    [Fact]
    public void WarnsButAllowsBlankPaddedTarget()
    {
        var problems = Compare(Column("C", "varchar(10)"), Live("C", "character(20)"));

        Assert.False(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.Severity == MismatchSeverity.Warn);
    }

    // ---- timestamps --------------------------------------------------------

    [Fact]
    public void BlocksTimestampIntoDate()
    {
        // Oracle DATE maps to timestamp(0); hand-built Postgres targets usually use date.
        AssertBlocked("timestamp(0) without time zone", "date");
    }

    [Theory]
    [InlineData("timestamp without time zone", "timestamp with time zone")]
    [InlineData("timestamp with time zone", "timestamp without time zone")]
    public void BlocksTimeZoneChange(string manifestType, string targetType)
    {
        // COPY accepts these - both are int8 microseconds - and silently reinterprets the value.
        AssertBlocked(manifestType, targetType);
    }

    [Fact]
    public void WarnsButAllowsLowerTimestampPrecision()
    {
        var problems = Compare(
            Column("C", "timestamp without time zone"),
            Live("C", "timestamp(0) without time zone"));

        Assert.False(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.Severity == MismatchSeverity.Warn);
    }

    // ---- column presence ---------------------------------------------------

    [Fact]
    public void BlocksMissingColumn()
    {
        var problems = Compare(Column("NOTES", "text"), Live("OTHER", "text"));

        Assert.True(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.Message.Contains("not found in the destination table"));
    }

    [Fact]
    public void NamesTheRealSpellingOnACaseOnlyMismatch()
    {
        // The most likely real-world failure: Oracle yields EMP_ID, the hand-built table has emp_id.
        var problems = Compare(Column("EMP_ID", "integer"), Live("emp_id", "integer"));

        Assert.True(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.Message.Contains("the destination has \"emp_id\""));
        // and it must not ALSO be reported as a stray extra column
        Assert.Single(problems);
    }

    [Fact]
    public void IgnoresExcludedManifestColumns()
    {
        var manifest = Manifest(
            Column("KEEP", "text", id: 1),
            Column("DROPPED", "text", excluded: true, id: 2));

        var problems = TargetSchemaComparer.Compare(manifest, new[] { Live("KEEP", "text") });

        Assert.Empty(problems);
    }

    [Fact]
    public void IgnoresColumnOrder()
    {
        var manifest = Manifest(
            Column("A", "integer", id: 1),
            Column("B", "text", id: 2));

        // COPY names its columns explicitly, so physical order is irrelevant.
        var problems = TargetSchemaComparer.Compare(manifest, new[]
        {
            Live("B", "text"),
            Live("A", "integer")
        });

        Assert.Empty(problems);
    }

    // ---- extra target columns ---------------------------------------------

    [Fact]
    public void BlocksExtraMandatoryTargetColumn()
    {
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("A", "integer")),
            new[] { Live("A", "integer"), Live("TENANT_ID", "integer", notNull: true) });

        Assert.True(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.ColumnName == "TENANT_ID");
    }

    [Theory]
    [InlineData(false, false, false, false)]  // nullable
    [InlineData(true, true, false, false)]    // NOT NULL with a default
    [InlineData(true, false, true, false)]    // NOT NULL identity - atthasdef is false for these
    [InlineData(true, false, false, true)]    // NOT NULL generated
    public void AllowsExtraTargetColumnPostgresCanFillIn(bool notNull, bool hasDefault, bool isIdentity, bool isGenerated)
    {
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("A", "integer")),
            new[]
            {
                Live("A", "integer"),
                Live("EXTRA", "integer", notNull: notNull, hasDefault: hasDefault, isIdentity: isIdentity, isGenerated: isGenerated)
            });

        Assert.Empty(problems);
    }

    // ---- nullability -------------------------------------------------------

    [Fact]
    public void WarnsWhenTargetIsMandatoryButSourceIsNot()
    {
        var problems = Compare(Column("C", "text", nullable: true), Live("C", "text", notNull: true));

        Assert.False(TargetSchemaComparer.HasBlockingProblem(problems));
        Assert.Contains(problems, p => p.Severity == MismatchSeverity.Warn);
    }

    [Fact]
    public void AllowsTargetLooserThanSource()
    {
        var problems = Compare(Column("C", "text", nullable: false), Live("C", "text", notNull: false));

        Assert.Empty(problems);
    }

    // ---- unknown types -----------------------------------------------------

    [Theory]
    [InlineData("integer[]")]
    [InlineData("public.email_address")]
    [InlineData("mood_enum")]
    public void BlocksTypesItCannotReasonAbout(string targetType) => AssertBlocked("text", targetType);

    // ---- name style --------------------------------------------------------
    //
    // O2P now creates destination tables in lower case, while tables left by earlier runs still carry
    // Oracle's upper-case spelling. Which one a run uses is decided when the table is prepared and
    // passed in here; the comparer itself never guesses.

    [Fact]
    public void LowerStyleMatchesAnUppercaseSourceOntoLowercaseColumns()
    {
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer"), Column("FIRST_NAME", "text")),
            new[] { Live("emp_id", "integer"), Live("first_name", "text") },
            PostgresName.LowerStyle);

        Assert.Empty(problems);
    }

    [Fact]
    public void SourceStyleMatchesATableAnEarlierRunCreated()
    {
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer")),
            new[] { Live("EMP_ID", "integer") },
            PostgresName.SourceStyle);

        Assert.Empty(problems);
    }

    [Fact]
    public void LowerStyleStillBlocksATypeClash()
    {
        // Folding the case must not soften anything else: binary COPY carries no type OIDs, so a
        // widened destination column is as broken as a narrowed one whatever the name looks like.
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer")),
            new[] { Live("emp_id", "bigint") },
            PostgresName.LowerStyle);

        Assert.True(TargetSchemaComparer.HasBlockingProblem(problems));
    }

    [Fact]
    public void LowerStyleAgainstAnUppercaseTableReportsTheNameItLookedFor()
    {
        // The wrong style is a plain mismatch reported before anything is touched, not a mid-copy
        // failure - and the message has to name both spellings to be actionable.
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer")),
            new[] { Live("EMP_ID", "integer") },
            PostgresName.LowerStyle);

        var problem = Assert.Single(problems, p => p.Severity == MismatchSeverity.Fail);
        Assert.Contains("\"emp_id\"", problem.Message);
        Assert.Contains("(from \"EMP_ID\")", problem.Message);
        Assert.Contains("the destination has \"EMP_ID\"", problem.Message);
    }

    [Fact]
    public void ANearMissIsNotAlsoReportedAsAnExtraColumn()
    {
        // The upper-case column is claimed by the missing lower-case one, so it must not come back a
        // second time as "extra required column in the destination".
        var problems = TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer", nullable: false)),
            new[] { Live("EMP_ID", "integer", notNull: true) },
            PostgresName.LowerStyle);

        Assert.Single(problems);
    }

    [Fact]
    public void DefaultingToSourceStyleKeepsTheOldBehaviour()
    {
        // The two-argument overload is what every existing caller used before the style existed.
        Assert.Empty(TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer")),
            new[] { Live("EMP_ID", "integer") }));

        Assert.True(TargetSchemaComparer.HasBlockingProblem(TargetSchemaComparer.Compare(
            Manifest(Column("EMP_ID", "integer")),
            new[] { Live("emp_id", "integer") })));
    }
}
