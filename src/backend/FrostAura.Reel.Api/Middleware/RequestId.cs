using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Api.Middleware;

/// <summary>
/// One id per request, the same in the <c>X-Request-Id</c> response header, in every log line the
/// request writes, and in <see cref="HttpContext.TraceIdentifier"/> (R1-36).
///
/// <para><b>Where the id comes from.</b> The gateway stamps every proxied request with nginx's own
/// <c>$request_id</c> and writes it into its access log, so one id joins the gateway's line to the
/// API's. A caller that reaches the API directly (dev, tests) may send its own; one that sends
/// none, or one that is not a plain token, keeps the server's id. An inbound value is never
/// trusted further than that: anything outside <see cref="IsValid"/> is dropped rather than
/// cleaned up, because the value ends up verbatim in a header and in a JSON log line.</para>
///
/// <para><b>Why a context factory and not a middleware.</b> ASP.NET Core's hosting layer opens
/// the request's log scope — the <c>RequestId</c> every structured line already carries — BEFORE
/// the first middleware runs, and copies <see cref="HttpContext.TraceIdentifier"/> into it at that
/// moment. A middleware that set the id would have to open a second scope with a second
/// <c>RequestId</c> beside the stale one, and a reader grepping the logs for the header's value
/// would find half of them. Setting the id where the context is created is the one place that
/// precedes the scope, so the existing <c>RequestId</c> simply becomes the right value.</para>
/// </summary>
public static class RequestId
{
    public const string HeaderName = "X-Request-Id";

    /// <summary>Long enough for a UUID with a prefix; nginx's <c>$request_id</c> is 32 hex digits.</summary>
    public const int MaxLength = 64;

    /// <summary>A non-empty token of letters, digits and <c>- _ . :</c>, at most <see cref="MaxLength"/> long.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The inbound id when it is valid and unambiguous, otherwise the server's own.</summary>
    public static string Resolve(string? inbound, string serverId) => IsValid(inbound) ? inbound! : serverId;

    /// <summary>Replaces the host's <see cref="IHttpContextFactory"/> with one that stamps the id.</summary>
    public static IServiceCollection AddRequestIds(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IHttpContextFactory>(
            provider => new RequestIdHttpContextFactory(new DefaultHttpContextFactory(provider))));
        return services;
    }
}

/// <summary>
/// Creates each request's context through the default factory, then pins its id before the
/// hosting layer reads it. See <see cref="RequestId"/>.
/// </summary>
public sealed class RequestIdHttpContextFactory(IHttpContextFactory inner) : IHttpContextFactory
{
    public HttpContext Create(IFeatureCollection featureCollection)
    {
        var context = inner.Create(featureCollection);

        // Two copies of the header are ambiguous; neither is chosen.
        var inbound = context.Request.Headers[RequestId.HeaderName];
        var id = RequestId.Resolve(inbound.Count == 1 ? inbound[0] : null, context.TraceIdentifier);
        context.TraceIdentifier = id;

        // OnStarting, not now: a handler that clears the response (the developer exception page
        // does) would otherwise take the header with it.
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            http.Response.Headers[RequestId.HeaderName] = http.TraceIdentifier;
            return Task.CompletedTask;
        }, context);

        return context;
    }

    public void Dispose(HttpContext httpContext) => inner.Dispose(httpContext);
}
