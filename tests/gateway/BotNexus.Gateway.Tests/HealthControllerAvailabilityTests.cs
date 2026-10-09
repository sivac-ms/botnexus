using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// The availability feed has FOUR distinct outcomes and only ONE of them is benign. Collapsing them
/// onto 404 told the portal "the optional watchdog is not installed", which it renders green - so a
/// read that collided with the watchdog's periodic rewrite, or a feed the watchdog corrupted as it
/// died mid-write, published a permanently "Healthy" headline through a total credential outage.
/// </summary>
public sealed class HealthControllerAvailabilityTests
{
    private static readonly string HomeRoot =
        Path.Combine(Path.GetTempPath(), "botnexus-health-tests", Guid.NewGuid().ToString("N"));

    private static string FeedPath =>
        Path.Combine(HomeRoot, "state", "availability", "health.json");

    private static HealthController CreateSut(IFileSystem fileSystem)
        => new(fileSystem, new BotNexusHome(fileSystem, HomeRoot, dataPath: null));

    private static HealthController CreateSutWithFeed(string? contents)
    {
        var fileSystem = new MockFileSystem();
        if (contents is not null)
            fileSystem.AddFile(FeedPath, new MockFileData(contents));

        return CreateSut(fileSystem);
    }

    [Fact]
    public void Absent_feed_is_the_only_case_that_may_answer_404()
    {
        var result = CreateSutWithFeed(contents: null).GetAvailability();

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void A_readable_feed_is_served_as_200()
    {
        var result = CreateSutWithFeed("""{"status":"healthy","signInRequired":false}""").GetAvailability();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var feed = Assert.IsType<AvailabilityHealthDto>(ok.Value);
        Assert.Equal("healthy", feed.Status);
    }

    [Fact]
    public void A_read_that_collides_with_the_watchdogs_rewrite_is_never_reported_as_not_installed()
    {
        // The feed EXISTS and may say "action-required". Answering 404 here painted the credentials
        // card benign-green and suppressed the sign-in banner while agents could not respond.
        var file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(true);
        file.ReadAllText(Arg.Any<string>()).Returns(_ => throw new IOException("locked by watchdog"));

        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(new MockFileSystem().Path);
        fileSystem.File.Returns(file);

        AssertUnreadable(CreateSut(fileSystem).GetAvailability());
    }

    [Fact]
    public void A_corrupt_or_half_written_feed_is_never_reported_as_not_installed()
    {
        AssertUnreadable(CreateSutWithFeed("""{"status":"action-req""").GetAvailability());
    }

    [Fact]
    public void A_feed_containing_the_json_literal_null_is_never_reported_as_not_installed()
    {
        AssertUnreadable(CreateSutWithFeed("null").GetAvailability());
    }

    [Fact]
    public void A_permission_denied_feed_is_unreadable_never_404_and_never_500()
    {
        // UnauthorizedAccessException does not derive from IOException, so an ACL-denied feed - the
        // watchdog writing as a different account than the gateway reads as - escaped the catch arm
        // and surfaced as a 500: a fifth state the contract does not define and no monitor maps to
        // "degraded". Narrow the filter back to `catch (IOException)` and this test throws rather
        // than returning 503, so it cannot pass without the fix.
        var file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(true);
        file.ReadAllText(Arg.Any<string>()).Returns(_ => throw new UnauthorizedAccessException("ACL denied"));

        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(new MockFileSystem().Path);
        fileSystem.File.Returns(file);

        var result = CreateSut(fileSystem).GetAvailability();

        Assert.IsNotType<NotFoundResult>(result.Result);
        AssertUnreadable(result);
    }

    private static void AssertUnreadable(ActionResult<AvailabilityHealthDto> result)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        var feed = Assert.IsType<AvailabilityHealthDto>(status.Value);
        Assert.Equal(HealthController.UnreadableStatus, feed.Status);
    }
}
