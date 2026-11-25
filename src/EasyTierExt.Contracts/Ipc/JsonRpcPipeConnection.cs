using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EasyTierExt.Contracts.Ipc;

public sealed class JsonRpcPipeConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    private Func<string, JsonElement?, Task<object?>>? _requestHandler;
    private Func<string, JsonElement?, Task>? _notificationHandler;
    private Task? _readLoopTask;
    private long _nextId;
    private int _disposed;

    public event Action? Disconnected;

    public JsonRpcPipeConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(_stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 16384, leaveOpen: true);
        _writer = new StreamWriter(_stream, Utf8NoBom, bufferSize: 16384, leaveOpen: true) { AutoFlush = true };
    }

    public void Start(
        Func<string, JsonElement?, Task<object?>>? requestHandler,
        Func<string, JsonElement?, Task>? notificationHandler)
    {
        _requestHandler = requestHandler;
        _notificationHandler = notificationHandler;
        _readLoopTask = Task.Run(ReadLoopAsync);
    }

    public async Task<TResult?> SendRequestAsync<TResult>(string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(ct);
            }
        });

        try
        {
            var payload = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = @params ?? new { }
            };
            await WriteLineAsync(JsonSerializer.Serialize(payload, IpcJsonContext.Options), ct).ConfigureAwait(false);
            var resultElement = await tcs.Task.ConfigureAwait(false);
            if (resultElement is null || resultElement.Value.ValueKind == JsonValueKind.Null || resultElement.Value.ValueKind == JsonValueKind.Undefined)
            {
                return default;
            }
            return resultElement.Value.Deserialize<TResult>(IpcJsonContext.Options);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public async Task SendNotificationAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            method,
            @params = @params ?? new { }
        };
        await WriteLineAsync(JsonSerializer.Serialize(payload, IpcJsonContext.Options), ct).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonRpcMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<JsonRpcMessage>(line, IpcJsonContext.Options);
                }
                catch
                {
                    continue;
                }

                if (msg is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(msg.Method))
                {
                    _ = Task.Run(() => HandleIncomingCallAsync(msg));
                }
                else if (!string.IsNullOrEmpty(msg.Id) && _pending.TryRemove(msg.Id, out var tcs))
                {
                    if (msg.Error is not null)
                    {
                        tcs.TrySetException(new InvalidOperationException($"IPC Error {msg.Error.Code}: {msg.Error.Message}"));
                    }
                    else
                    {
                        tcs.TrySetResult(msg.Result);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetException(new IOException("IPC pipe disconnected."));
                }
            }
            Disconnected?.Invoke();
        }
    }

    private async Task HandleIncomingCallAsync(JsonRpcMessage msg)
    {
        if (msg.Id is null)
        {
            if (_notificationHandler is not null)
            {
                try
                {
                    await _notificationHandler(msg.Method!, msg.Params);
                }
                catch
                {
                }
            }
            return;
        }

        if (_requestHandler is null)
        {
            await SendErrorAsync(msg.Id, -32601, "Method handler not registered.");
            return;
        }

        try
        {
            var result = await _requestHandler(msg.Method!, msg.Params);
            var response = new
            {
                jsonrpc = "2.0",
                id = msg.Id,
                result
            };
            await WriteLineAsync(JsonSerializer.Serialize(response, IpcJsonContext.Options), _cts.Token);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(msg.Id, -32000, ex.Message);
        }
    }

    private async Task SendErrorAsync(string id, int code, string message)
    {
        try
        {
            var err = new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            };
            await WriteLineAsync(JsonSerializer.Serialize(err, IpcJsonContext.Options), _cts.Token);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
