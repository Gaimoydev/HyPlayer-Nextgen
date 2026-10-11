#nullable enable
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HyPlayer.Classes;

internal sealed class NetworkWatchHandler : DelegatingHandler
{
    public NetworkWatchHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(ResourceSaver.ProbeKey, out var probe) && probe)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ResourceSaver.ReportRequest(watch.Elapsed, (int)response.StatusCode >= 500);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ResourceSaver.ReportRequest(watch.Elapsed, true);
            throw;
        }
    }
}
