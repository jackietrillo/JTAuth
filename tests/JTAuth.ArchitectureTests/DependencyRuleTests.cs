using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace JTAuth.ArchitectureTests;

public sealed class DependencyRuleTests
{
    private static readonly string[] Frameworks =
    [
        "Dapper",
        "Microsoft.AspNetCore",
        "Microsoft.Data",
        "System.Data",
        "Azure",
        "Microsoft.Azure",
        "Microsoft.IdentityModel",
    ];

    private static readonly string[] MediatorLibraries = ["MediatR", "Mediator", "Scrutor"];

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Api"];

    private static Assembly Load(string name) => Assembly.Load(name);

    private static void ShouldPass(NetArchTest.Rules.TestResult result, string because) =>
        result.IsSuccessful.ShouldBeTrue($"{because} Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");

    [Fact]
    public void DomainDependsOnNothingOutsideItself()
    {
        var result = Types.InAssembly(Load("JTAuth.Domain"))
            .ShouldNot()
            .HaveDependencyOnAny(["JTAuth.Application", "JTAuth.Infrastructure", "JTAuth.BuildingBlocks", "JTAuth.Contracts", .. Frameworks])
            .GetResult();

        ShouldPass(result, "Domain must have no dependencies.");
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureOrFrameworks()
    {
        var result = Types.InAssembly(Load("JTAuth.Application"))
            .ShouldNot()
            .HaveDependencyOnAny(["JTAuth.Infrastructure", .. Frameworks])
            .GetResult();

        ShouldPass(result, "Application must depend only on Domain, BuildingBlocks and Contracts.");
    }

    [Fact]
    public void ContractsContainNoServiceOrFrameworkCode()
    {
        var result = Types.InAssembly(Load("JTAuth.Contracts"))
            .ShouldNot()
            .HaveDependencyOnAny(["JTAuth.BuildingBlocks", "JTAuth.Domain", "JTAuth.Application", "JTAuth.Infrastructure", .. Frameworks])
            .GetResult();

        ShouldPass(result, "Contracts holds DTOs only.");
    }

    [Fact]
    public void JTAuthKnowsNothingAboutAnyApp()
    {
        foreach (var layer in Layers)
        {
            Load($"JTAuth.{layer}").GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => name.StartsWith("CityBars", StringComparison.Ordinal))
                .ShouldBeEmpty($"JTAuth.{layer} must not reference an app; apps reference JTAuth, never the other way round.");
        }
    }

    [Fact]
    public void NoMediatorLibraryIsReferenced()
    {
        foreach (var assembly in Layers.Select(layer => Load($"JTAuth.{layer}")).Append(Load("JTAuth.BuildingBlocks")))
        {
            assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => MediatorLibraries.Any(library => name.StartsWith(library, StringComparison.Ordinal)))
                .ShouldBeEmpty($"{assembly.GetName().Name} must not use a mediator library.");
        }
    }
}
