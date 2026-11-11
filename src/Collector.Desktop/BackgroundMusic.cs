using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Collector.Desktop;

public interface IBackgroundMusic : IDisposable
{
    bool IsPlaying { get; }
    event Action<string>? PlaybackFailed;
    void Start();
    void Stop();
}

/// <summary>Plays the bundled original score using OS audio. Never blocks the UI thread.</summary>
public sealed class BackgroundMusic(string filePath) : IBackgroundMusic
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _disposed;
    public bool IsPlaying { get; private set; }
    public event Action<string>? PlaybackFailed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPlaying) return;
        if (_cancellation != null) Stop();
        if (!File.Exists(filePath)) throw new FileNotFoundException("Фоновая композиция не найдена.", filePath);
        if (OperatingSystem.IsWindows())
        {
            const uint asyncLoopFile = 0x0001 | 0x0002 | 0x0008 | 0x20000;
            if (!PlaySound(filePath, IntPtr.Zero, asyncLoopFile))
                throw new IOException("Windows не удалось воспроизвести фоновую музыку.");
            IsPlaying = true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            _cancellation = new CancellationTokenSource();
            IsPlaying = true;
            _loop = RunMacLoopAsync(_cancellation.Token);
        }
        else throw new PlatformNotSupportedException("Фоновая музыка поддерживается в Windows и macOS.");
    }

    private async Task RunMacLoopAsync(CancellationToken token)
    {
        // Task.Run also keeps process startup and teardown away from Avalonia's dispatcher.
        await Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var info = new ProcessStartInfo("/usr/bin/afplay")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardError = true, RedirectStandardOutput = true
                    };
                    info.ArgumentList.Add("-v"); info.ArgumentList.Add("0.65");
                    info.ArgumentList.Add(filePath);
                    using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить звук.");
                    using var stop = token.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    });
                    var error = process.StandardError.ReadToEndAsync();
                    var output = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    var message = await error;
                    await output;
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new IOException(string.IsNullOrWhiteSpace(message) ? "Аудиоустройство недоступно." : message.Trim());
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    IsPlaying = false;
                    PlaybackFailed?.Invoke(e.Message);
                }
            }
        });
    }

    public void Stop()
    {
        if (_disposed) return;
        IsPlaying = false;
        _cancellation?.Cancel();
        // Each loop owns its token/process, so a rapid stop/start cannot kill a new player.
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation != null && _loop != null)
            _ = _loop.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        if (OperatingSystem.IsWindows()) PlaySound(null, IntPtr.Zero, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _disposed = true;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);
}

public sealed class AudioPreferences
{
    public bool Enabled { get; set; } = true;
    public bool EffectsEnabled { get; set; } = true;
    public static AudioPreferences Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AudioPreferences>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}
