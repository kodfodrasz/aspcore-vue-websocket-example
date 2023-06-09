﻿using Microsoft.AspNetCore.SignalR;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Net.WebSockets;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace webapi;

public interface IWebSocketHub
{
    IDisposable RegisterWebsocket(WebSocket ws);
}

public class WebSocketHub : IWebSocketHub, IHostedService, IDisposable
{
    private readonly ILogger<WebSocketHub> logger;

    private readonly object socketsLock = new();
    private IImmutableSet<WebSocket> sockets = ImmutableHashSet<WebSocket>.Empty;
    private Timer? timer = null;

    public WebSocketHub(ILogger<WebSocketHub> logger)
    {
        this.logger = logger;
    }

    private TimeSpan AnnounceInterval { get; } = TimeSpan.FromSeconds(5);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Background Service running.");
        timer = new Timer(Broadcast, null, TimeSpan.Zero, AnnounceInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer?.Change(Timeout.Infinite, 0);
        timer?.Dispose();

        lock (socketsLock)
        {
            foreach (var s in sockets)
            {
                s.Abort();
            }
        }
    }

    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private WeatherForecast[] Weather { get; set; } = Enumerable.Range(1, 5).Select(index => webapi.Controllers.WeatherForecastController.BuildForecast(index)).ToArray();

    private async void PublishOnce(WebSocket ws)
    {
        string jsonString = JsonSerializer.Serialize(Weather, jsonOptions);
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
            await ws.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, $"Error while sending to websocket ${ws}");
        }
    }

    private async void Broadcast(object? state)
    {
        Weather = Enumerable.Range(1, 5).Select(index => webapi.Controllers.WeatherForecastController.BuildForecast(index)).ToArray();

        string jsonString = JsonSerializer.Serialize(Weather, jsonOptions);

        foreach (var s in sockets)
        {
            try
            {
                if (s.State == WebSocketState.Closed
                    || s.State == WebSocketState.CloseSent
                    || s.State == WebSocketState.CloseReceived
                    || s.State == WebSocketState.Aborted)
                {

                    lock (socketsLock)
                    {
                        sockets = sockets.Remove(s);
                    }
                    continue;
                }
                else if (s.State == WebSocketState.Open)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                    await s.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Error while sending to websocket ${s}");
            }
        }
    }

    public IDisposable RegisterWebsocket(WebSocket ws)
    {
        PublishOnce(ws);

        return new WebSocketRegistrationCancellation(this, ws);
    }

    private class WebSocketRegistrationCancellation : IDisposable
    {
        private readonly WebSocketHub hub;
        private readonly WebSocket ws;

        public WebSocketRegistrationCancellation(WebSocketHub websocketHub, WebSocket ws)
        {
            this.hub = websocketHub;
            this.ws = ws;

            lock (hub.socketsLock)
            {
                hub.sockets = hub.sockets.Add(ws);
            }
        }

        public void Dispose()
        {
            lock (hub.socketsLock)
            {
                hub.sockets = hub.sockets.Remove(ws);
            }
        }
    }
}
