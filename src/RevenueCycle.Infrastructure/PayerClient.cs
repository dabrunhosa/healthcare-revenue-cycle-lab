using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;
using RevenueCycle.Application;

namespace RevenueCycle.Infrastructure;

public sealed class PayerClient(HttpClient client) : IPayerClient
{
    public async Task<PayerResult> SubmitAsync(PayerSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/claims") { Content = JsonContent.Create(submission) };
            request.Headers.Add("Idempotency-Key", submission.ClaimId);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var receipt = await response.Content.ReadFromJsonAsync<PayerReceipt>(cancellationToken);
                return receipt?.ClaimId == submission.ClaimId && receipt.PayerClaimId is { Length: > 0 and <= 80 }
                    ? new("Accepted", receipt.PayerClaimId) : new("InvalidPayerReceipt");
            }
            var transient = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return new(transient ? "PayerUnavailable" : "PayerRejected", Transient: transient);
        }
        catch (BrokenCircuitException) { return new("CircuitOpen", Transient: true); }
        catch (TimeoutRejectedException) { return new("Timeout", Transient: true); }
        catch (HttpRequestException) { return new("TransportFailure", Transient: true); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("Timeout", Transient: true); }
        catch (JsonException) { return new("InvalidPayerReceipt"); }
    }
}
