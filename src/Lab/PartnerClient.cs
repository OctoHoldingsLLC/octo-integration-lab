using System.Net.Http.Json;
using System.Text.Json;

namespace Octo.IntegrationLab.Lab;

public sealed class PartnerClient(IHttpClientFactory clients, LabOptions options)
{
    public async Task<JsonElement?> StateAsync(CancellationToken token)
    {
        try
        {
            using var request = Request(HttpMethod.Get);
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<JsonElement>(LabJson.Options, token);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    public async Task<bool> SetAsync(PartnerControl control, CancellationToken token)
    {
        try
        {
            using var request = Request(HttpMethod.Post);
            request.Content = JsonContent.Create(control, options: LabJson.Options);
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private HttpRequestMessage Request(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, new Uri(options.PartnerUrl, "api/control"));
        request.Headers.Add("X-Lab-Control-Key", options.ControlKey);
        return request;
    }
}
