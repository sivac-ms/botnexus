using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Surfaces the availability feed written by the out-of-process availability watchdog.
/// <para>
/// The gateway's own <c>/health</c> endpoint reports 200 whenever the process is alive, which is
/// exactly the failure mode this endpoint exists to cover: on 2026-08-17 a transient HTTP 502 while
/// refreshing provider credentials left agents running with no API key, emitting empty responses,
/// while <c>/health</c> stayed green throughout. The watchdog probes credential validity from
/// outside the request path and publishes its verdict to
/// <c>{BOTNEXUS_HOME}/state/availability/health.json</c>; this controller simply reads that file so
/// the portal can render it.
/// </para>
/// <para>
/// The file is the single source of truth. When it is absent the watchdog is not installed or has
/// never run, and this endpoint returns 404 rather than fabricating a healthy-looking default -
/// a synthesised "healthy" here would recreate the very blind spot the feed exists to remove.
/// </para>
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private const string StateDirectoryName = "state";
    private const string AvailabilityDirectoryName = "availability";
    private const string AvailabilityFileName = "health.json";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly IFileSystem _fileSystem;
    private readonly BotNexusHome _home;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthController"/> class.
    /// </summary>
    /// <param name="fileSystem">Filesystem abstraction used for testable reads of the feed file.</param>
    /// <param name="home">Resolves the BotNexus home root the watchdog publishes under.</param>
    public HealthController(IFileSystem fileSystem, BotNexusHome home)
    {
        _fileSystem = fileSystem;
        _home = home;
    }

    /// <summary>
    /// Returns the most recent availability verdict published by the watchdog.
    /// </summary>
    /// <returns>
    /// The parsed availability feed; 404 only when the feed file does not exist (watchdog not
    /// installed or not yet run); 503 with <c>{"status":"unreadable"}</c> when the feed exists but
    /// cannot be read or parsed (the sensor is broken, which is NOT benign).
    /// </returns>
    [HttpGet("availability")]
    [ProducesResponseType(typeof(AvailabilityHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AvailabilityHealthDto), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<AvailabilityHealthDto> GetAvailability()
    {
        var path = _fileSystem.Path.Combine(
            _home.RootPath,
            StateDirectoryName,
            AvailabilityDirectoryName,
            AvailabilityFileName);

        if (!_fileSystem.File.Exists(path))
            return NotFound();

        string json;
        try
        {
            json = _fileSystem.File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The feed EXISTS but could not be read. That is a broken sensor, not an absent one:
            // collapsing it onto 404 made the portal paint "watchdog not installed" (benign, green)
            // while a genuine action-required verdict sat unread on disk.
            //
            // UnauthorizedAccessException does NOT derive from IOException: an ACL-denied feed -
            // the watchdog running as a different account than the gateway - escaped this arm
            // entirely and surfaced as a 500, a fifth state the documented contract does not have.
            return Unreadable();
        }

        AvailabilityHealthDto? feed;
        try
        {
            feed = JsonSerializer.Deserialize<AvailabilityHealthDto>(json, ReadOptions);
        }
        catch (JsonException)
        {
            // A corrupt or half-written feed is a broken sensor. If the watchdog died mid-write the
            // file stays corrupt forever, so reporting 404 here would render green permanently
            // through a total credential outage.
            return Unreadable();
        }

        // A JSON literal `null` body is likewise unusable data from an existing feed.
        return feed is null ? Unreadable() : Ok(feed);
    }

    /// <summary>
    /// 503 carrying an explicit <c>unreadable</c> marker so the portal can distinguish a broken
    /// sensor from an uninstalled one and render the credentials card degraded.
    /// </summary>
    private ActionResult<AvailabilityHealthDto> Unreadable() =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new AvailabilityHealthDto { Status = UnreadableStatus });

    /// <summary>Status value reported when the feed exists but cannot be read or parsed.</summary>
    public const string UnreadableStatus = "unreadable";
}

/// <summary>
/// The availability verdict published by the watchdog's <c>publish_health</c> routine. Field names
/// mirror the on-disk JSON exactly; the portal renders these values directly.
/// </summary>
public sealed class AvailabilityHealthDto
{
    /// <summary>Overall verdict: <c>healthy</c>, <c>recovered</c>, or <c>action-required</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>UTC timestamp of the check that produced this verdict.</summary>
    [JsonPropertyName("checkedAt")]
    public string? CheckedAt { get; init; }

    /// <summary>Remaining lifetime, in seconds, of the current provider token.</summary>
    [JsonPropertyName("tokenValidForSeconds")]
    public long? TokenValidForSeconds { get; init; }

    /// <summary>Seconds before expiry at which the watchdog expects a refresh to have occurred.</summary>
    [JsonPropertyName("refreshMarginSeconds")]
    public long? RefreshMarginSeconds { get; init; }

    /// <summary>True when interactive sign-in is required; agents cannot respond until it completes.</summary>
    [JsonPropertyName("signInRequired")]
    public bool SignInRequired { get; init; }

    /// <summary>Device-code verification URI to open, when sign-in is required.</summary>
    [JsonPropertyName("signInUri")]
    public string? SignInUri { get; init; }

    /// <summary>Device code to enter at <see cref="SignInUri"/>, when sign-in is required.</summary>
    [JsonPropertyName("signInCode")]
    public string? SignInCode { get; init; }

    /// <summary>Number of gateway restarts the watchdog observed in the last hour.</summary>
    [JsonPropertyName("restartsLastHour")]
    public int RestartsLastHour { get; init; }
}
