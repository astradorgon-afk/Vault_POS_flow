using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace Pos.Architecture.Tests;

/// <summary>
/// Structural rules that a code review can miss and a refactor can quietly
/// break. These fail the build rather than producing a warning nobody reads.
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(Pos.Domain.Common.Money).Assembly;
    private static readonly Assembly Shared = typeof(Pos.Shared.AssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(Pos.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Pos.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_DependsOnNothingButTheFramework()
    {
        TestResult result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny(
                "Pos.Application",
                "Pos.Infrastructure",
                "Pos.Api",
                "Pos.Web",
                "Pos.Client",
                "Pos.Shared",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions.DependencyInjection",
                "Npgsql")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Explain(result));
    }

    [Fact]
    public void Shared_DependsOnNothingButTheFramework()
    {
        TestResult result = Types.InAssembly(Shared)
            .Should()
            .NotHaveDependencyOnAny(
                "Pos.Domain",
                "Pos.Application",
                "Pos.Infrastructure",
                "Pos.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Explain(result));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrTransport()
    {
        TestResult result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny(
                "Pos.Infrastructure",
                "Pos.Api",
                "Pos.Web",
                "Pos.Client",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Explain(result));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnTransportOrUi()
    {
        TestResult result = Types.InAssembly(Infrastructure)
            .Should()
            .NotHaveDependencyOnAny("Pos.Api", "Pos.Web", "Pos.Client")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Explain(result));
    }

    [Fact]
    public void Domain_UsesNoFloatingPointForBusinessValues()
    {
        // Money and quantities are decimal everywhere. A double on a domain type
        // is how rounding error gets into a ledger.
        IEnumerable<Type> offenders = Domain.GetTypes()
            .Where(t => t is { IsPublic: true, IsEnum: false })
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.PropertyType == typeof(double)
                        || p.PropertyType == typeof(float)
                        || p.PropertyType == typeof(double?)
                        || p.PropertyType == typeof(float?))
            .Select(p => p.DeclaringType!)
            .Distinct();

        offenders.Should().BeEmpty("no domain value may be stored as binary floating point");
    }

    private static string Explain(TestResult result)
        => result.FailingTypeNames is null
            ? "architecture rule violated"
            : "violating types: " + string.Join(", ", result.FailingTypeNames);
}
