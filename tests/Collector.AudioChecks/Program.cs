using Collector.Desktop;

var clock = new TestClock();
var player = new TestPlayer();
using var effects = new UiSoundEffects(player, clock);
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
async Task<Play> Started() => await player.Events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

effects.Hover();
var hover = await Started();
Check(hover.Sound == UiSound.Hover, "наведение запускает тихий эффект");
for (var i = 0; i < 30; i++) effects.Hover();
Check(!player.Events.Reader.TryRead(out _), "частые наведения не создают очередь");
effects.Click();
var click = await Started();
Check(hover.Token.IsCancellationRequested && click.Sound == UiSound.Click && player.MaximumActive == 1,
    "нажатие прерывает наведение; одновременно играет не более одного эффекта");
for (var i = 0; i < 30; i++) effects.Click();
Check(!player.Events.Reader.TryRead(out _), "дребезг нажатий не создаёт очередь");
effects.Enabled = false;
Check(click.Token.IsCancellationRequested, "выключатель останавливает текущий эффект");
clock.Advance(200); effects.Hover(); effects.Click();
Check(!player.Events.Reader.TryRead(out _), "в выключенном режиме эффектов нет");
effects.Enabled = true; effects.Click();
var resumed = await Started();
Check(resumed.Sound == UiSound.Click, "эффекты включаются повторно");
effects.Dispose();
Check(resumed.Token.IsCancellationRequested && !effects.Enabled, "закрытие останавливает эффекты");

var failing = new UiSoundEffects(new FailingPlayer());
var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
failing.PlaybackFailed += message => failure.TrySetResult(message);
failing.Click();
Check((await failure.Task.WaitAsync(TimeSpan.FromSeconds(3))) == "Тестовая ошибка" && !failing.Enabled,
    "ошибка аудиоустройства отключает эффекты без исключения в интерфейсе");
failing.Dispose();

sealed record Play(UiSound Sound, CancellationToken Token);
sealed class TestClock : TimeProvider
{
    private long _now;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _now;
    public void Advance(long milliseconds) => _now += milliseconds;
}
sealed class TestPlayer : IUiSoundPlayer
{
    public readonly System.Threading.Channels.Channel<Play> Events = System.Threading.Channels.Channel.CreateUnbounded<Play>();
    private int _active;
    public int MaximumActive { get; private set; }
    public async Task PlayAsync(UiSound sound, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _active);
        MaximumActive = Math.Max(active, MaximumActive);
        Events.Writer.TryWrite(new Play(sound, token));
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        finally { Interlocked.Decrement(ref _active); }
    }
}
sealed class FailingPlayer : IUiSoundPlayer
{
    public Task PlayAsync(UiSound sound, CancellationToken token) => Task.FromException(new IOException("Тестовая ошибка"));
}
