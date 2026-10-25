using System.Globalization;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMdbStatementsTests
{
    private static WorkflowMdbRow Rate(decimal thickness, object? meter, object? pierce = null, string text = "Лазер")
    {
        var id = Guid.NewGuid();
        return new("cutting_rates", id, new()
        {
            ["id"] = id, ["machine_id"] = Guid.NewGuid(), ["machine_name"] = text,
            ["material_group"] = "Базовая", ["thickness_mm"] = thickness,
            ["price_per_meter"] = meter, ["price_per_pierce"] = pierce
        });
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void DecimalTokensRoundtripExactlyAndIgnoreCulture(string culture)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            decimal[] samples = [0m, 1m, 0.5m, 0.8m, 1.2m, 111.25m, 13.123456789m, 0.0000000001m,
                123456789012345678.1234567890m, -999999999999999999.9999999999m, 1.0000000000000000000000000000m];
            foreach (var value in samples)
            {
                var plan = WorkflowMdbStatements.Insert(Rate(0.5m, value, value));
                var tokens = plan.Sql[(plan.Sql.IndexOf("VALUES (", StringComparison.Ordinal) + 8)..^1].Split(',');
                Assert.Equal(7, tokens.Length); Assert.Equal("0.5", tokens[4]);
                foreach (var token in tokens.Skip(5))
                {
                    Assert.All(token, c => Assert.True(char.IsAsciiDigit(c) || c is '.' or '-', token));
                    Assert.Equal(value, decimal.Parse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
                }
                Assert.Equal(new[] {"id", "machine_id", "machine_name", "material_group"}, plan.Parameters.Select(p => p.Column.Name));
                Assert.Equal(plan.Parameters.Length, plan.Sql.Count(c => c == '?'));
            }
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void TextWithSqlSyntaxAndNullDecimalsRemainOrderedParameters()
    {
        const string input = "O'Reilly ?; DROP TABLE cutting_rates; --";
        var row = Rate(0.8m, 111.25m, text: input);
        var plan = WorkflowMdbStatements.Insert(row);
        Assert.EndsWith("VALUES (?,?,?,?,0.8,111.25,?)", plan.Sql);
        Assert.DoesNotContain(input, plan.Sql); Assert.Equal(input, plan.Parameters[2].Value);
        Assert.Equal("price_per_pierce", plan.Parameters[^1].Column.Name); Assert.Null(plan.Parameters[^1].Value);
        Assert.Equal(WorkflowMdbType.Decimal, plan.Parameters[^1].Column.Type);
        Assert.Equal(row.Values["machine_id"], plan.Parameters[1].Value);
    }

    [Fact]
    public void UpdateBindsOnlyChangedTextAndNullBeforeItsId()
    {
        var before = Rate(0.5m, 10m, 1m);
        var after = before with { Values = new(before.Values) { ["machine_name"] = "Имя ?'", ["price_per_meter"] = 13.1234567891m, ["price_per_pierce"] = null } };
        var plan = Assert.IsType<WorkflowMdbStatement>(WorkflowMdbStatements.Update(before, after));
        Assert.Equal("UPDATE [cutting_rates] SET [machine_name]=?,[price_per_meter]=13.1234567891,[price_per_pierce]=? WHERE [id]=?", plan.Sql);
        Assert.Equal(new[] {"machine_name", "price_per_pierce", "id"}, plan.Parameters.Select(p => p.Column.Name));
        Assert.Equal("Имя ?'", plan.Parameters[0].Value); Assert.Null(plan.Parameters[1].Value); Assert.Equal(before.Id, plan.Parameters[2].Value);
        Assert.Null(WorkflowMdbStatements.Update(before, before));
    }

    [Fact]
    public void DecimalReadbackScaleAloneDoesNotCauseAnUpdate()
    {
        var before = Rate(0.5m, 111.25m);
        var after = before with { Values = new(before.Values) { ["thickness_mm"] = 0.5000000000m, ["price_per_meter"] = 111.2500000000m } };
        Assert.Null(WorkflowMdbStatements.Update(before, after));
    }

    [Fact]
    public void VersionsAndColoursKeepTheirTypedParameters()
    {
        var employee = new Employee(Guid.NewGuid(), "Имя ?'") { ColorArgb = uint.MaxValue, IsTechnologist = true };
        var workspace = WorkflowWorkspace.Empty with { Employees = [employee] };
        var row = WorkflowMdbMapping.Flatten(workspace)["employees"][employee.Id];
        var plan = WorkflowMdbStatements.Insert(row);
        Assert.All(plan.Sql[(plan.Sql.IndexOf("VALUES (", StringComparison.Ordinal) + 8)..^1].Split(','), t => Assert.Equal("?", t));
        Assert.Equal(WorkflowMdbType.UInt32, plan.Parameters[2].Column.Type); Assert.Equal((decimal)uint.MaxValue, plan.Parameters[2].Value);
    }

    [Theory]
    [InlineData("0.12345678901")]
    [InlineData("1000000000000000000")]
    [InlineData("-1000000000000000000")]
    public void WritePlansAlsoRejectDecimalsBeyondColumnPrecision(string text)
    {
        var row = Rate(0.5m, decimal.Parse(text, CultureInfo.InvariantCulture));
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbStatements.Insert(row));
        var original = row with { Values = new(row.Values) { ["price_per_meter"] = 1m } };
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbStatements.Update(original, row));
    }

    [Fact]
    public void DecimalSlotsNeverAcceptRawTextOrFloatingPointInput()
    {
        foreach (var input in new object[] { "0.5); DROP TABLE cutting_rates; --", "0,5", 0.5d, 0.5f, 1 })
            Assert.Throws<WorkflowValidationException>(() => WorkflowMdbStatements.Insert(Rate(0.5m, input)));
    }
}
