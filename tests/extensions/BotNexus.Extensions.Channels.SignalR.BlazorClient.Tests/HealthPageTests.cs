using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for the Health page (/health). Verifies the portal-load gate and that the dashboard
/// renders only once the portal is ready - the dashboard fires eight HTTP probes on init, so
/// rendering it before the portal is up would produce a grid of failures for no reason.
/// </summary>
public sealed class HealthPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IPortalLoadService _portalLoad;

    public HealthPageTests()
    {
        _portalLoad = Substitute.For<IPortalLoadService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton(_portalLoad);
        // The dashboard resolves an HttpClient on render; with no handler registered every probe
        // fails, which is exactly the isolation path HealthDashboardComponentTests covers.
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_connecting_spinner_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Health>();

        cut.Find(".portal-loading");
        Assert.Contains("Connecting", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='health-dashboard']"));
    }

    [Fact]
    public void Shows_error_when_load_error_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Connection refused");

        var cut = _ctx.Render<Health>();

        cut.Find(".portal-load-error");
        Assert.Contains("Connection refused", cut.Markup);
    }

    [Fact]
    public void Renders_dashboard_when_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<Health>();

        cut.Find("[data-testid='health-dashboard']");
        Assert.Empty(cut.FindAll(".portal-loading"));
    }

    /// <summary>
    /// The page subscribes to <c>OnReadyChanged</c> in OnInitialized and must unsubscribe in
    /// Dispose. A leaked handler keeps a disposed component alive and re-renders it, so after
    /// disposal a ready transition must be inert rather than throwing.
    /// </summary>
    [Fact]
    public void Ready_transition_after_dispose_is_inert()
    {
        var cut = _ctx.Render<Health>();
        cut.Find(".portal-loading");

        cut.Instance.Dispose();

        _portalLoad.IsReady.Returns(true);
        var exception = Record.Exception(() => _portalLoad.OnReadyChanged += Raise.Event<Action>());
        Assert.Null(exception);
    }

    [Fact]
    public void Disposes_cleanly_while_the_dashboard_is_mounted()
    {
        _portalLoad.IsReady.Returns(true);
        var cut = _ctx.Render<Health>();
        cut.Find("[data-testid='health-dashboard']");

        var exception = Record.Exception(() => _ctx.Dispose());

        Assert.Null(exception);
    }
}
