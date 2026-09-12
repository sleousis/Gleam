using System.Net;
using Gleam.Core.Execution;
using Gleam.Core.Integrations;

namespace Gleam.Core.Tests;

/// <summary>Work that must not stall the game's thread, and a price source that must not stall every scan.</summary>
public class ThreadingFixTests
{
    [Fact]
    public async Task Hopping_off_the_game_thread_always_lands_on_the_thread_pool()
    {
        // Run the awaiting code on a plain thread, the way the game's thread is one, and check where it continues.
        var landed = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            async Task Go()
            {
                await OffGameThread.Hop();
                landed.SetResult(Thread.CurrentThread.IsThreadPoolThread);
            }
            _ = Go();
        });
        thread.Start();

        Assert.True(await landed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task After_a_failure_the_price_source_stays_quiet_for_a_while_then_tries_again()
    {
        var handler = new CountingHandler(HttpStatusCode.InternalServerError);
        var clock = DateTimeOffset.UnixEpoch;
        using var client = new UniversalisClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, clock: () => clock);

        await client.GetPricesAsync([1, 2], "Phoenix", CancellationToken.None);
        await client.GetPricesAsync([1, 2], "Phoenix", CancellationToken.None);
        Assert.Equal(1, handler.Requests);

        clock += UniversalisClient.QuietAfterFailure + TimeSpan.FromSeconds(1);
        await client.GetPricesAsync([1, 2], "Phoenix", CancellationToken.None);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task One_failed_batch_stops_the_rest_of_that_lookup()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests);
        using var client = new UniversalisClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

        await client.GetPricesAsync(Enumerable.Range(1, UniversalisClient.BatchSize * 3).Select(i => (uint)i), "Phoenix", CancellationToken.None);

        Assert.Equal(1, handler.Requests);
    }
}
