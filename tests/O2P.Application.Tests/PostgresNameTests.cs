using System.Globalization;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using Xunit;

namespace O2P.Application.Tests;

public class PostgresNameTests
{
    private static ManifestColumn Column(string name) => new() { ColumnName = name };

    // ---- For ---------------------------------------------------------------

    [Theory]
    [InlineData("BL_NOTIFICATION", "bl_notification")]
    [InlineData("LAST_UPDATED", "last_updated")]
    [InlineData("already_lower", "already_lower")]
    [InlineData("MiXeD", "mixed")]
    [InlineData("COL$1#", "col$1#")]
    public void For_lower_cases_the_name(string input, string expected) =>
        Assert.Equal(expected, PostgresName.For(input));

    [Fact]
    public void For_is_culture_independent()
    {
        // Under tr-TR, "I".ToLower() is the dotless "ı" - a name nobody could type and that no other
        // machine would agree on. ToLowerInvariant is the whole point of having this helper.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("id", PostgresName.For("ID"));
            Assert.Equal("notification", PostgresName.For("NOTIFICATION"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_rejects_a_blank_name(string input) =>
        Assert.Throws<ArgumentException>(() => PostgresName.For(input));

    // ---- style -------------------------------------------------------------

    [Fact]
    public void Lower_style_lower_cases_column_names() =>
        Assert.Equal("last_updated", PostgresName.TargetColumn(Column("LAST_UPDATED"), PostgresName.LowerStyle));

    [Fact]
    public void Source_style_keeps_the_oracle_spelling() =>
        Assert.Equal("LAST_UPDATED", PostgresName.TargetColumn(Column("LAST_UPDATED"), PostgresName.SourceStyle));

    [Fact]
    public void An_unrecorded_style_reads_as_source()
    {
        // Table runs written before the style was tracked can only have loaded an upper-case table,
        // so null must not start lower-casing their columns retrospectively.
        Assert.Equal("LAST_UPDATED", PostgresName.TargetColumn(Column("LAST_UPDATED"), null));
        Assert.False(PostgresName.IsLower(null));
        Assert.False(PostgresName.IsLower("anything else"));
    }

    [Fact]
    public void Target_columns_keep_their_order()
    {
        var columns = new[] { Column("EMP_ID"), Column("FIRST_NAME"), Column("SALARY") };

        // Order is the contract: the reader sends rows positionally and field i lands in column i.
        Assert.Equal(
            new[] { "emp_id", "first_name", "salary" },
            PostgresName.TargetColumns(columns, PostgresName.LowerStyle));
    }

    // ---- collisions --------------------------------------------------------

    [Fact]
    public void Names_differing_only_in_case_are_reported()
    {
        var collisions = PostgresName.FindCollisions(new[] { "EMP", "Emp", "DEPT" });

        var group = Assert.Single(collisions);
        Assert.Equal(new[] { "EMP", "Emp" }, group);
        Assert.Contains("\"EMP\" and \"Emp\"", PostgresName.DescribeCollisions(collisions));
        Assert.Contains("would both become \"emp\"", PostgresName.DescribeCollisions(collisions));
    }

    [Fact]
    public void Names_that_stay_distinct_are_not_reported() =>
        Assert.Empty(PostgresName.FindCollisions(new[] { "EMP", "DEPT", "already_lower" }));

    [Fact]
    public void The_same_name_listed_twice_is_not_a_collision() =>
        // One table ticked once is not two tables fighting over a name.
        Assert.Empty(PostgresName.FindCollisions(new[] { "EMP", "EMP" }));

    [Fact]
    public void Three_way_collisions_are_reported_as_one_group()
    {
        var group = Assert.Single(PostgresName.FindCollisions(new[] { "COL", "Col", "col" }));
        Assert.Equal(new[] { "COL", "Col", "col" }, group);
    }
}
