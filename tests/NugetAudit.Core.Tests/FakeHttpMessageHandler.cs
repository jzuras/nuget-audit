namespace NugetAudit.Core.Tests;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> that delegates to a user-provided function.
/// Used in unit tests to simulate HTTP responses without real network calls.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    /// <summary>
    /// Gets the delegate invoked for each HTTP request.
    /// </summary>
    private Func<HttpRequestMessage, HttpResponseMessage> Handler { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeHttpMessageHandler"/> class.
    /// </summary>
    /// <param name="handler">
    /// A function that receives an <see cref="HttpRequestMessage"/> and returns a
    /// <see cref="HttpResponseMessage"/> to simulate a specific HTTP response.
    /// </param>
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        this.Handler = handler;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(this.Handler(request));
    }
}
