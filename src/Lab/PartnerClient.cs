using System.Net.Http.Json;
using System.Text.Json;

namespace Octo.IntegrationLab.Lab;

public sealed class PartnerClient(IHttpClientFactory clients, LabOptions options)
{
    public async Task<JsonElement?> StateAsync(CancellationToken token, string destination = "primary")
    {
        try
        {
            using var request = Request(HttpMethod.Get, destination);
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<JsonElement>(LabJson.Options, token);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    public async Task<bool> SetAsync(PartnerControl control, CancellationToken token, string destination = "primary")
    {
        try
        {
            using var request = Request(HttpMethod.Post, destination);
            request.Content = JsonContent.Create(control, options: LabJson.Options);
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public Task<ReceiverEvent?> EventAsync(string destination, Guid id, CancellationToken token) =>
        GetAsync<ReceiverEvent>(destination, $"api/events/{id}", token);

    public Task<List<ReceiverEvent>?> RunAsync(string destination, Guid id, CancellationToken token) =>
        GetAsync<List<ReceiverEvent>>(destination, $"api/runs/{id}", token);

    public async Task<bool> RecoverRunAsync(string destination, Guid id, CancellationToken token)
    {
        try
        {
            using var request = Request(HttpMethod.Post, destination, $"api/runs/{id}");
            request.Content = JsonContent.Create(new RunControl("healthy"));
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private async Task<T?> GetAsync<T>(string destination, string path, CancellationToken token) where T : class
    {
        try
        {
            using var request = Request(HttpMethod.Get, destination, path);
            using var response = await clients.CreateClient("control").SendAsync(request, token);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(LabJson.Options, token) : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private HttpRequestMessage Request(HttpMethod method, string destination = "primary", string path = "api/control")
    {
        var request = new HttpRequestMessage(method, new Uri(options.DestinationUrl(destination), path));
        request.Headers.Add("X-Lab-Control-Key", options.ControlKey);
        return request;
    }
}
