using System.Net.Http;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Core.Tests;

public class SourceBaseTests
{
    private sealed class FakeSource() : SourceBase("id", "fake", "Fake")
    {
        public override Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionInfo>>([]);

        public Task<int> RunAsync(Func<CancellationToken, Task<int>> operation, CancellationToken cancellationToken, int maxAttempts = 3) =>
            RetryAsync(operation, maxAttempts, TimeSpan.Zero, cancellationToken);
    }

    [Fact]
    public void Exposes_identity()
    {
        var source = new FakeSource();

        Assert.Equal(("id", "fake", "Fake"), (source.Id, source.Kind, source.DisplayName));
    }

    [Fact]
    public void Rejects_blank_id() =>
        Assert.ThrowsAny<ArgumentException>(() => new BlankIdSource());

    private sealed class BlankIdSource() : SourceBase("", "fake", "Fake")
    {
        public override Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionInfo>>([]);
    }

    [Fact]
    public async Task Retry_retries_unavailable_then_succeeds()
    {
        var calls = 0;

        var result = await new FakeSource().RunAsync(
            _ => ++calls < 3 ? throw new SourceUnavailableException("down") : Task.FromResult(42),
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_gives_up_after_max_attempts()
    {
        var calls = 0;

        await Assert.ThrowsAsync<SourceUnavailableException>(() => new FakeSource().RunAsync(
            _ =>
            {
                calls++;
                throw new SourceUnavailableException("down");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_does_not_retry_auth_failures()
    {
        var calls = 0;

        await Assert.ThrowsAsync<SourceAuthException>(() => new FakeSource().RunAsync(
            _ =>
            {
                calls++;
                throw new SourceAuthException("rejected");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Network_errors_become_unavailable_without_leaking_the_message()
    {
        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => new FakeSource().RunAsync(
            _ => throw new HttpRequestException("GET https://user:hunter2@example.com failed"),
            TestContext.Current.CancellationToken,
            maxAttempts: 1));

        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Unknown_errors_become_source_exceptions()
    {
        var ex = await Assert.ThrowsAsync<SourceException>(() => new FakeSource().RunAsync(
            _ => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken));

        Assert.Equal(typeof(SourceException), ex.GetType());
    }

    [Fact]
    public async Task Cancellation_passes_through_unchanged()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FakeSource().RunAsync(
            ct => Task.FromCanceled<int>(ct), cts.Token));
    }
}
