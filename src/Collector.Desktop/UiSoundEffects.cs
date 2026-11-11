using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Collector.Desktop;

public enum UiSound { Hover, Click }

public interface IUiSoundEffects : IDisposable
{
    bool Enabled { get; set; }
    event Action<string>? PlaybackFailed;
    void Hover();
    void Click();
}

public interface IUiSoundPlayer
{
    Task PlayAsync(UiSound sound, CancellationToken cancellationToken);
}

/// <summary>One short effect at a time. Clicks take priority; hover never queues up.</summary>
public sealed class UiSoundEffects(IUiSoundPlayer player, TimeProvider? timeProvider = null) : IUiSoundEffects
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _active;
    private long? _lastSound, _lastClick;
    private bool _enabled = true, _disposed;
    public event Action<string>? PlaybackFailed;
    public bool Enabled
    {
        get { lock (_gate) return _enabled && !_disposed; }
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                _enabled = value;
                if (!value) _active?.Cancel();
            }
        }
    }
    public void Hover() => Request(UiSound.Hover);
    public void Click() => Request(UiSound.Click);

    private void Request(UiSound sound)
    {
        CancellationTokenSource current;
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            var now = _clock.GetTimestamp();
            if (sound == UiSound.Hover && (_active != null
                || _lastSound.HasValue && _clock.GetElapsedTime(_lastSound.Value, now).TotalMilliseconds < 150)) return;
            if (sound == UiSound.Click && _lastClick.HasValue
                && _clock.GetElapsedTime(_lastClick.Value, now).TotalMilliseconds < 75) return;
            _active?.Cancel();
            current = _active = new CancellationTokenSource();
            _lastSound = now;
            if (sound == UiSound.Click) _lastClick = now;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                current.Token.ThrowIfCancellationRequested();
                await _playGate.WaitAsync(current.Token);
                try { await player.PlayAsync(sound, current.Token); }
                finally { _playGate.Release(); }
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception e)
            {
                var report = false;
                lock (_gate)
                {
                    if (!_disposed && !current.IsCancellationRequested && ReferenceEquals(_active, current))
                    { _enabled = false; report = true; }
                }
                if (report) PlaybackFailed?.Invoke(e.Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, current)) _active = null;
                    current.Dispose();
                }
            }
        });
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _enabled = false; _active?.Cancel();
        }
    }
}

public sealed class SystemUiSoundPlayer(string directory) : IUiSoundPlayer
{
    public async Task PlayAsync(UiSound sound, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, sound == UiSound.Hover ? "ui-hover.wav" : "ui-click.wav");
        if (!File.Exists(path)) throw new FileNotFoundException("Звук интерфейса не найден.", path);
        if (OperatingSystem.IsMacOS())
        {
            var info = new ProcessStartInfo("/usr/bin/afplay")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-v"); info.ArgumentList.Add("0.65"); info.ArgumentList.Add(path);
            using var process = Process.Start(info) ?? throw new IOException("Не удалось включить звук интерфейса.");
            using var stop = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var message = await error; await output;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(message) ? "Аудиоустройство недоступно." : message.Trim());
        }
        else if (OperatingSystem.IsWindows())
        {
            // Use an independent MCI device. PlaySound would replace the looping background track.
            var alias = "collector_fx_" + Guid.NewGuid().ToString("N");
            Check(MciSendString($"open \"{path}\" type waveaudio alias {alias}", IntPtr.Zero, 0, IntPtr.Zero));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(MciSendString($"play {alias}", IntPtr.Zero, 0, IntPtr.Zero));
                await Task.Delay(sound == UiSound.Hover ? 180 : 250, cancellationToken);
            }
            finally { MciSendString($"close {alias}", IntPtr.Zero, 0, IntPtr.Zero); }
        }
        else throw new PlatformNotSupportedException("Звуки интерфейса доступны в Windows и macOS.");
    }
    private static void Check(uint result)
    {
        if (result != 0) throw new IOException($"Звук интерфейса недоступен (код {result}).");
    }
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern uint MciSendString(string command, IntPtr result, uint resultLength, IntPtr callback);
}
