using JevDotNet.Demo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace JevDotNet.Demo.Components.Lanes;

public sealed class LaneRoadInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./Components/Lanes/LaneRoad.razor.js";
    private const string Initialize = "initialize", Reset = "reset", Start = "start", Pause = "pause", Snapshot = "snapshot", Steer = "steer", Dispose = "dispose";
    private IJSObjectReference? module, road;
    public async Task InitializeAsync(ElementReference canvas, DotNetObjectReference<LaneRoad> reference)
    {
        module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        road = await module.InvokeAsync<IJSObjectReference>(Initialize, canvas, reference);
    }
    public ValueTask<LaneSnapshot> ResetAsync(int id, int seconds) => road!.InvokeAsync<LaneSnapshot>(Reset, id, seconds);
    public ValueTask<bool> StartAsync(int id) => road!.InvokeAsync<bool>(Start, id);
    public ValueTask<LaneSnapshot> PauseAsync(int id) => road!.InvokeAsync<LaneSnapshot>(Pause, id);
    public ValueTask<LaneSnapshot> SnapshotAsync() => road!.InvokeAsync<LaneSnapshot>(Snapshot);
    public ValueTask<bool> SteerAsync(int id, int lane) => road!.InvokeAsync<bool>(Steer, id, lane);
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (road is not null) { await road.InvokeVoidAsync(Dispose); await road.DisposeAsync(); }
            if (module is not null) await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
