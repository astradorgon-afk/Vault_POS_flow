using FluentAssertions;
using Pos.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Pos.Security.Tests;

/// <summary>
/// Secrets never reach a log sink, however they are logged: as a scalar
/// property, inside a destructured object, or inside a dictionary.
/// </summary>
public sealed class SensitiveDataScrubberTests
{
    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("NewPassword")]
    [InlineData("RefreshToken")]
    [InlineData("access_token")]
    [InlineData("Jwt__SigningKeyPem")]
    [InlineData("ConnectionStrings:Postgres:ConnectionString")]
    [InlineData("Authorization")]
    [InlineData("Pin")]
    [InlineData("TwoFactorCode")]
    [InlineData("EnrolmentCode")]
    [InlineData("SharedKey")]
    public void SecretNames_AreSensitive(string name)
        => SensitiveDataScrubber.IsSensitive(name).Should().BeTrue();

    [Theory]
    [InlineData("ErrorCode")]
    [InlineData("PreApprovalTokenId")]
    [InlineData("UserName")]
    [InlineData("Shipping")]
    [InlineData("CorrelationId")]
    [InlineData("")]
    public void OrdinaryNames_AreNotSensitive(string name)
        => SensitiveDataScrubber.IsSensitive(name).Should().BeFalse();

    [Fact]
    public void Scalars_NestedObjects_AndDictionaries_AreMasked()
    {
        CollectingSink sink = new();
        using Logger logger = new LoggerConfiguration()
            .Enrich.With<SensitiveDataScrubber>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "Sign-in {UserName} with {Password} and body {@Body} and headers {@Headers}",
            "store1.mgr",
            "DevVaultFlow!2026",
            new { UserName = "store1.mgr", Password = "DevVaultFlow!2026", Nested = new { RefreshToken = "abc" } },
            new Dictionary<string, string> { ["Authorization"] = "Bearer eyJ...", ["Accept"] = "application/json" });

        LogEvent logged = sink.Events.Should().ContainSingle().Subject;
        string rendered = logged.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

        rendered.Should().NotContain("DevVaultFlow!2026").And.NotContain("Bearer").And.NotContain("abc");
        rendered.Should().Contain("store1.mgr").And.Contain("application/json");
        logged.Properties["Password"].ToString().Should().Contain(SensitiveDataScrubber.Mask);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
