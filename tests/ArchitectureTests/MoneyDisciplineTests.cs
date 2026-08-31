using Mono.Cecil;
using Shouldly;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// Money is <see langword="long"/> minor units through the entire pipeline. No floating point,
/// anywhere, in anything that carries an amount.
/// </summary>
/// <remarks>
/// <para>
/// THE SINGLE FASTEST CREDIBILITY CHECK A BANK REVIEWER MAKES. 0.1 has no binary floating-point
/// representation; a pipeline that touches money as <see langword="double"/> even once produces
/// statements whose lines sum to a cent off the closing balance, discovered by the one customer
/// who reconciles by hand. Integer minor units make the error unrepresentable.
/// </para>
/// <para>
/// Checked against the compiled IL of the money-bearing assemblies: every field, property,
/// method parameter and return type whose NAME suggests money must not be a float, double or
/// decimal. Name-based, deliberately - the rendering layer legitimately uses <c>float</c> for
/// LAYOUT (margins, font sizes), and a blanket no-floats rule would either ban that or exempt
/// the whole assembly. A money-named float is the mistake; a padding-named float is a page
/// margin.
/// </para>
/// </remarks>
public sealed class MoneyDisciplineTests
{
    private static readonly string[] MoneyNameFragments =
        ["amount", "balance", "money", "total", "price", "fee", "charge", "minorunit"];

    private static readonly string[] ForbiddenTypes =
        ["System.Single", "System.Double", "System.Decimal"];

    // Every assembly an amount travels through: domain document, ledger DTOs (worker side),
    // mock ledger (wire contract), rendering, and the persistence layer.
    private static readonly string[] PipelineAssemblies =
    [
        "StatementDelivery.Domain",
        "StatementDelivery.Contracts",
        "StatementDelivery.Persistence",
        "StatementDelivery.Rendering",
        "Generation.Worker",
        "MockLedger.Api",
    ];

    [Fact]
    public void Money_IsNeverDoubleOrFloat_AnywhereInPipeline()
    {
        var offenders = new List<string>();
        int moneyMembersSeen = 0;

        foreach (string assemblyName in PipelineAssemblies)
        {
            string path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            File.Exists(path).ShouldBeTrue($"{assemblyName}.dll was not found beside the tests");

            using ModuleDefinition module = ModuleDefinition.ReadModule(path);

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (FieldDefinition field in type.Fields)
                {
                    Inspect($"{type.FullName}.{field.Name}", field.Name, field.FieldType, offenders, ref moneyMembersSeen);
                }

                foreach (PropertyDefinition property in type.Properties)
                {
                    Inspect($"{type.FullName}.{property.Name}", property.Name, property.PropertyType, offenders, ref moneyMembersSeen);
                }

                foreach (MethodDefinition method in type.Methods)
                {
                    Inspect($"{type.FullName}.{method.Name}() return", method.Name, method.ReturnType, offenders, ref moneyMembersSeen);

                    foreach (ParameterDefinition parameter in method.Parameters)
                    {
                        Inspect(
                            $"{type.FullName}.{method.Name}({parameter.Name})",
                            parameter.Name, parameter.ParameterType, offenders, ref moneyMembersSeen);
                    }
                }
            }
        }

        // Anti-vacuity: the pipeline genuinely carries money-named members (AmountMinorUnits,
        // OpeningBalanceMinor, ...). Zero seen means the scan is looking at the wrong thing.
        moneyMembersSeen.ShouldBeGreaterThan(20, "the scan found no money-named members - it is inspecting the wrong assemblies");

        offenders.ShouldBeEmpty(
            "money must be long minor units end to end; these members are floating-point or decimal: "
            + string.Join("; ", offenders));
    }

    private static void Inspect(
        string location, string memberName, TypeReference type, List<string> offenders, ref int seen)
    {
        string lowered = memberName.ToUpperInvariant();
        bool moneyNamed = false;

        foreach (string fragment in MoneyNameFragments)
        {
            if (lowered.Contains(fragment.ToUpperInvariant(), StringComparison.Ordinal))
            {
                moneyNamed = true;
                break;
            }
        }

        if (!moneyNamed)
        {
            return;
        }

        seen++;

        if (ForbiddenTypes.Contains(type.FullName, StringComparer.Ordinal))
        {
            offenders.Add($"{location}: {type.FullName}");
        }
    }
}
