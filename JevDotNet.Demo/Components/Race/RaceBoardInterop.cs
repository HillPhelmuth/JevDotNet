using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace JevDotNet.Demo.Components.Race;

internal sealed class RaceBoardInterop(IJSRuntime js, ElementReference canvas) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Race/RaceBoard.razor.js";
    private const string Initialize = "initialize";
    private const string Start = "start";
    private const string Stop = "stop";
    private const string Reset = "reset";
    private const string Queue = "queue";
    private const string Dispose = "dispose";
    private IJSObjectReference? module;
    private bool disposed;

    public async ValueTask InitializeAsync(DotNetObjectReference<RaceBoard> callback)
    {
        var imported = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        if (disposed)
        {
            await imported.DisposeAsync();
            return;
        }
        module = imported;
        await module.InvokeVoidAsync(Initialize, canvas, callback, PlatformRaceDecisions.Goal);
    }

    public ValueTask<bool> StartAsync(int run) => module!.InvokeAsync<bool>(Start, canvas, run);
    public ValueTask StopAsync(int run) => module!.InvokeVoidAsync(Stop, canvas, run);
    public ValueTask ResetAsync() => module!.InvokeVoidAsync(Reset, canvas);
    public ValueTask<bool> QueueAsync(RaceHop hop, int? target) =>
        module!.InvokeAsync<bool>(Queue, canvas, hop.Run, hop.Racer, hop.Id, target);

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (module is null) return;
        try
        {
            try { await module.InvokeVoidAsync(Dispose, canvas); }
            finally { await module.DisposeAsync(); }
        }
        catch (JSDisconnectedException) { }
    }
}
