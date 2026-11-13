using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 可选的本地 HTTP 接口：<c>http://127.0.0.1:端口/randompicker/...</c>。
/// </summary>
/// <remarks>
/// <b>默认关闭</b>，只绑定 <see cref="IPAddress.Loopback"/>，外部设备连不上；
/// 开了之后可以设令牌，没带对令牌的请求一律 401。不开端口、不改系统设置、不上传任何东西。
/// 用 <see cref="TcpListener"/> 而不是 <c>HttpListener</c>：后者在 Windows 上需要管理员
/// 预留 URL ACL，普通用户跑 ClassIsland 会直接抛异常。
/// <para/>
/// 只认 GET / POST，只处理 <c>/randompicker</c> 下的路径，响应是最小的 JSON。
/// 抽选本身交给 <see cref="PickerEventService.Request"/>，由宿主服务切回 UI 线程执行。
/// </remarks>
public sealed class PickerHttpApi : IDisposable
{
    private readonly PickerEventService _events;
    private readonly PickerHostService _host;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PickerHttpApi(PickerEventService events, PickerHostService host)
    {
        _events = events;
        _host = host;
    }

    /// <summary>接口是否正在监听。</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>上次启动失败的原因，设置页里显示给用户。</summary>
    public string? LastError { get; private set; }

    /// <summary>按当前设置重启（或关闭）监听。</summary>
    public void Restart()
    {
        Stop();

        var settings = _host.Settings;
        if (!settings.EnableHttpApi)
        {
            LastError = null;
            return;
        }

        var port = Math.Clamp(settings.HttpApiPort, 1024, 65535);
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            LastError = null;
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            LastError = $"{port} 端口起不来：{ex.Message}";
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
            // 已经取消了。
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // 已经停了。
        }

        _listener = null;
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client, token));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[2048];
                var head = new StringBuilder();
                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read <= 0)
                    {
                        return;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    var text = head.ToString();
                    if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Length > 16384)
                    {
                        break;
                    }
                }

                var lines = head.ToString()
                    .Split("\r\n", StringSplitOptions.None);
                var first = lines.Length > 0 ? lines[0] : string.Empty;
                var parts = first.Split(' ');
                var method = parts.Length > 0 ? parts[0] : string.Empty;
                var target = parts.Length > 1 ? parts[1] : "/";

                if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 204, "{}");
                    return;
                }

                var qIndex = target.IndexOf('?');
                var path = qIndex < 0 ? target : target[..qIndex];
                var query = qIndex < 0 ? string.Empty : target[(qIndex + 1)..];

                if (!path.StartsWith("/randompicker", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 404, "{\"ok\":false,\"error\":\"not found\"}");
                    return;
                }

                var settings = _host.Settings;
                var expectedToken = settings.HttpApiToken;
                if (!string.IsNullOrEmpty(expectedToken))
                {
                    var provided = PickRequestParser.ParseQuery(query)
                        .GetValueOrDefault("token") ?? FindHeader(lines, "X-Picker-Token");
                    if (!string.Equals(provided, expectedToken, StringComparison.Ordinal))
                    {
                        await WriteAsync(stream, 401, "{\"ok\":false,\"error\":\"invalid token\"}");
                        return;
                    }
                }

                if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 200, StatusJson());
                    return;
                }

                // 悬浮窗显隐路由：/show、/hide、/toggle。和 classisland:// 同名路由共用宿主逻辑。
                var visibilityResponse = "{\"ok\":true,\"visible\":" +
                                         (_host.IsFloatingWindowVisible ? "true" : "false") + "}";
                if (path.EndsWith("/show", StringComparison.OrdinalIgnoreCase))
                {
                    _host.SetFloatingWindowVisible(true);
                    await WriteAsync(stream, 200, visibilityResponse);
                    return;
                }

                if (path.EndsWith("/hide", StringComparison.OrdinalIgnoreCase))
                {
                    _host.SetFloatingWindowVisible(false);
                    await WriteAsync(stream, 200, visibilityResponse);
                    return;
                }

                if (path.EndsWith("/toggle", StringComparison.OrdinalIgnoreCase))
                {
                    _host.ToggleFloatingWindowVisible();
                    await WriteAsync(stream, 200, visibilityResponse);
                    return;
                }

                var request = PickRequestParser.Parse(path, query, settings, "http");
                _events.Request(request);
                await WriteAsync(stream, 200,
                    "{\"ok\":true,\"kind\":\"" + request.Kind.ToString().ToLowerInvariant() +
                    "\",\"count\":" + request.Count + "}");
            }
            catch (OperationCanceledException)
            {
                // 关闭中。
            }
            catch (Exception)
            {
                // 单个连接出错不影响监听。
            }
        }
    }

    private string StatusJson()
    {
        var last = _events.LastPick;
        var payload = new
        {
            ok = true,
            running = IsRunning,
            port = _host.Settings.HttpApiPort,
            mode = _host.Settings.Mode.ToString(),
            names = _host.Roster?.Names.Count ?? 0,
            remaining = _host.RemainingInRound,
            last = last is null ? null : new
            {
                text = last.DisplayText,
                source = last.Source.ToString(),
                count = last.Count,
                names = last.Names,
                time = last.Time.ToString("O")
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string? FindHeader(string[] lines, string name)
    {
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 &&
                string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string json)
    {
        var statusText = status switch
        {
            200 => "OK",
            204 => "No Content",
            401 => "Unauthorized",
            _ => "Not Found"
        };
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {statusText}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: X-Picker-Token\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose() => Stop();
}
