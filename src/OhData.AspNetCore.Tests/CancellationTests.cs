using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Cancellation-token propagation tests: verify that the <see cref="CancellationToken"/> passed
/// to GetAll/GetById/Post handlers is the request-aborted token, and that it actually fires when
/// the HTTP request is aborted client-side. Synchronization is entirely via
/// <see cref="TaskCompletionSource{TResult}"/> handshakes (started/observed signals) rather than
/// sleeps or timing assumptions; a generous <c>WaitAsync</c> deadline is used only as a safety net
/// against a genuine hang, never as the primary synchronization mechanism.
/// </summary>
public class CancellationTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(15);

    /// <summary>Coordinates started/cancelled signals between the test thread and the in-handler
    /// awaited continuation, for each of GetAll/GetById/Post.</summary>
    private sealed class CancellationCoordinator
    {
        public TaskCompletionSource<bool> StartedGetAll { get; } = NewTcs();
        public TaskCompletionSource<bool> ObservedCancelGetAll { get; } = NewTcs();

        public TaskCompletionSource<bool> StartedGetById { get; } = NewTcs();
        public TaskCompletionSource<bool> ObservedCancelGetById { get; } = NewTcs();

        public TaskCompletionSource<bool> StartedPost { get; } = NewTcs();
        public TaskCompletionSource<bool> ObservedCancelPost { get; } = NewTcs();

        /// <summary>Set to true only if the Post handler runs past the awaited cancellation point —
        /// i.e. the operation was allowed to "commit". Must remain false when cancellation fires.</summary>
        public volatile bool PostCommitted;

        private static TaskCompletionSource<bool> NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CancellableWidgetProfile : EntitySetProfile<int, Widget>
    {
        public CancellableWidgetProfile(CancellationCoordinator coord) : base(x => x.Id)
        {
            EntitySetName = "CancellableWidgets";

            GetAll = async (ct) =>
            {
                coord.StartedGetAll.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    coord.ObservedCancelGetAll.TrySetResult(true);
                    throw;
                }
                return Array.Empty<Widget>();
            };

            GetById = async (id, ct) =>
            {
                coord.StartedGetById.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    coord.ObservedCancelGetById.TrySetResult(true);
                    throw;
                }
                return (Widget?)null;
            };

            Post = async (widget, ct) =>
            {
                coord.StartedPost.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    coord.ObservedCancelPost.TrySetResult(true);
                    throw;
                }
                // Only reached if cancellation was NOT observed — proves a "committed" write.
                coord.PostCommitted = true;
                return widget;
            };
        }
    }

    [Fact]
    public async Task GetAll_CancellationTokenFires_WhenRequestAborted()
    {
        var coord = new CancellationCoordinator();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<CancellableWidgetProfile>(),
            configureServices: s => s.AddSingleton(coord));

        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> responseTask = fx.Client.GetAsync("/odata/CancellableWidgets", cts.Token);

        await coord.StartedGetAll.Task.WaitAsync(SafetyNet);
        cts.Cancel();

        await coord.ObservedCancelGetAll.Task.WaitAsync(SafetyNet);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
    }

    [Fact]
    public async Task GetById_CancellationTokenFires_WhenRequestAborted()
    {
        var coord = new CancellationCoordinator();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<CancellableWidgetProfile>(),
            configureServices: s => s.AddSingleton(coord));

        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> responseTask = fx.Client.GetAsync("/odata/CancellableWidgets(1)", cts.Token);

        await coord.StartedGetById.Task.WaitAsync(SafetyNet);
        cts.Cancel();

        await coord.ObservedCancelGetById.Task.WaitAsync(SafetyNet);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
    }

    [Fact]
    public async Task Post_CancellationTokenFires_WhenRequestAborted_AndDoesNotCommit()
    {
        var coord = new CancellationCoordinator();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<CancellableWidgetProfile>(),
            configureServices: s => s.AddSingleton(coord));

        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> responseTask = fx.Client.PostAsJsonAsync(
            "/odata/CancellableWidgets", new Widget { Id = 1, Name = "Ghost" }, cts.Token);

        await coord.StartedPost.Task.WaitAsync(SafetyNet);
        cts.Cancel();

        await coord.ObservedCancelPost.Task.WaitAsync(SafetyNet);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);

        // The handler must not have produced a committed 200/201 — the write never completed.
        Assert.False(coord.PostCommitted);
    }
}
