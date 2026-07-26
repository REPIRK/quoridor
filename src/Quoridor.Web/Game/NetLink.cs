using Microsoft.JSInterop;

namespace Quoridor.Web.Game;

public enum NetState
{
    Idle,
    Hosting,
    Joining,
    Connected,
    Failed,
}

/// <summary>
/// The .NET half of the peer-to-peer link. Owns the JavaScript module, turns its
/// callbacks into events, and knows nothing about Quoridor — what travels over the
/// wire is decided by the caller.
/// </summary>
public sealed class NetLink : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;
    private DotNetObjectReference<NetLink>? _self;

    public NetLink(IJSRuntime js) => _js = js;

    /// <summary>Raised for every message the other side sends.</summary>
    public event Action<string>? Received;

    /// <summary>Raised whenever the connection state changes.</summary>
    public event Action? Changed;

    public NetState State { get; private set; } = NetState.Idle;

    /// <summary>The code the other player needs. Only set while hosting.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Trouble { get; private set; } = string.Empty;

    public bool IsConnected => State == NetState.Connected;

    public async Task HostAsync()
    {
        await PrepareAsync();
        Set(NetState.Hosting);
        await _module!.InvokeVoidAsync("host", _self);
    }

    public async Task JoinAsync(string code)
    {
        await PrepareAsync();
        Set(NetState.Joining);
        await _module!.InvokeVoidAsync("join", code.Trim(), _self);
    }

    public async Task SendAsync(string message)
    {
        if (_module is null || !IsConnected) return;
        await _module.InvokeVoidAsync("send", message);
    }

    [JSInvokable]
    public void OnNetEvent(string kind, string payload)
    {
        switch (kind)
        {
            case "hosting":
                Code = payload;
                Set(NetState.Hosting);
                break;

            case "joining":
                Set(NetState.Joining);
                break;

            case "connected":
                Trouble = string.Empty;
                Set(NetState.Connected);
                break;

            case "message":
                Received?.Invoke(payload);
                break;

            case "closed":
                Trouble = "The other player disconnected.";
                Set(NetState.Failed);
                break;

            case "error":
                Trouble = payload;

                // A hiccup once the channel is open is worth reporting but is not the
                // end of the game; before that it is.
                if (!IsConnected) Set(NetState.Failed);
                else Changed?.Invoke();

                break;
        }
    }

    private void Set(NetState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private async Task PrepareAsync()
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/net.js");
        _self ??= DotNetObjectReference.Create(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("close");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; nothing to close.
            }
        }

        _self?.Dispose();
    }
}
