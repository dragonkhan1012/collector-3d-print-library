using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Collector.Core.Preview;

namespace Collector.Desktop;

public sealed class ModelViewport : Control,IDisposable
{
    private readonly DispatcherTimer _timer=new() { Interval=TimeSpan.FromMilliseconds(33) };
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private WriteableBitmap? _bitmap;
    private readonly PreviewRenderBuffer _frameBuffer=new();
    private bool _preferSharp;
    private int _motionPixels=640;
    private double _lastPresented,_lastReport;
    public bool PreferSharp { get=>_preferSharp;set { _preferSharp=value;_dirty=true; } }
    public int RenderPixelSize=>_bitmap?.PixelSize.Width??0;
    public double LastRenderMilliseconds { get; private set; }
    public double FramesPerSecond { get; private set; }
    public event Action? FramePresented;
    private PreviewScene? _scene;
    private bool _rendering,_disposed,_dragging,_dirty=true,_attached;
    private Point _lastPointer;
    private double _lastTime;
    private double _yaw=.5,_elevation=.32,_zoom=1;
    public bool AutoRotate { get; set; } = true;
    public bool IsDragging => _dragging;
    public PreviewScene? Scene => _scene;
    public double Yaw => _yaw;
    public double Elevation => _elevation;
    public double Zoom => _zoom;
    public int RenderedFrames { get; private set; }
    public event Action<string>? RenderFailed;

    public ModelViewport()
    {
        ClipToBounds=true;Focusable=true;
        RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.HighQuality);
        Cursor=new Cursor(StandardCursorType.Hand);
        _timer.Tick+=async (_,_) => await RefreshFrameAsync();
        AttachedToVisualTree+=(_,_) => { _attached=true;_lastTime=_clock.Elapsed.TotalSeconds;_timer.Start(); };
        DetachedFromVisualTree+=(_,_) => { _attached=false;_timer.Stop(); };
        PointerPressed+=(_,e) =>
        {
            if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragging=true;_lastPointer=e.GetPosition(this);AutoRotate=false;e.Pointer.Capture(this);e.Handled=true;
        };
        PointerMoved+=(_,e) =>
        {
            if(!_dragging) return;
            var p=e.GetPosition(this);_yaw+=(p.X-_lastPointer.X)*.012;
            _elevation=Math.Clamp(_elevation+(p.Y-_lastPointer.Y)*.008,.05,1.3);
            _lastPointer=p;_dirty=true;
        };
        PointerReleased+=(_,e) => { _dragging=false;e.Pointer.Capture(null); };
        PointerCaptureLost+=(_,_) => _dragging=false;
        PointerWheelChanged+=(_,e) => { SetZoom(_zoom+e.Delta.Y*.06);e.Handled=true; };
    }
    public void SetScene(PreviewScene? scene)
    {
        _scene=scene;_motionPixels=640;_lastPresented=0;FramesPerSecond=0;ResetView();_bitmap?.Dispose();_bitmap=null;InvalidateVisual();
    }
    public void ResetView() { _yaw=.5;_elevation=.32;_zoom=1;_dirty=true; }
    public void SetZoom(double value) { _zoom=Math.Clamp(value,.35,1);_dirty=true; }
    public async Task RefreshFrameAsync()
    {
        if(_disposed || _rendering || !_attached || !IsEffectivelyVisible || _scene==null
            || TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;
        var time=_clock.Elapsed.TotalSeconds;
        if(AutoRotate && !_dragging) { _yaw+=Math.Min(.2,time-_lastTime)*.22;_dirty=true; }
        _lastTime=time;
        var moving=AutoRotate || _dragging;
        var physical=Math.Min(Bounds.Width,Bounds.Height)*(TopLevel.GetTopLevel(this)?.RenderScaling??1);
        var requested=(int)Math.Clamp(physical,128,moving && !PreferSharp?_motionPixels:1024);
        if(!_dirty && _bitmap?.PixelSize.Width==requested) return;
        _rendering=true;_dirty=false;
        var started=_clock.Elapsed.TotalSeconds;
        var scene=_scene;var yaw=_yaw;var elevation=_elevation;var zoom=_zoom;var token=_lifetime.Token;
        try
        {
            var watch=Stopwatch.StartNew();
            var pixels=await Task.Run(()=>PreviewRenderer.Render(scene,_frameBuffer,requested,yaw,elevation,zoom,token),token);
            LastRenderMilliseconds=watch.Elapsed.TotalMilliseconds;
            if(_disposed || !ReferenceEquals(scene,_scene)) return;
            if(_bitmap?.PixelSize.Width!=requested)
            {
                _bitmap?.Dispose();_bitmap=new(new PixelSize(requested,requested),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
            }
            using(var buffer=_bitmap.Lock())
                for(var y=0;y<requested;y++) Marshal.Copy(pixels,y*requested,buffer.Address+y*buffer.RowBytes,requested);
            RenderedFrames++;
            var presented=_clock.Elapsed.TotalSeconds;
            if(moving && _lastPresented>0)
            {
                var fps=1/Math.Max(.001,presented-_lastPresented);
                FramesPerSecond=FramesPerSecond==0?fps:FramesPerSecond*.75+fps*.25;
            }
            _lastPresented=moving?presented:0;
            if(moving && !PreferSharp)
            {
                if(LastRenderMilliseconds>45) _motionPixels=Math.Max(512,_motionPixels-64);
                else if(LastRenderMilliseconds<22) _motionPixels=Math.Min(800,_motionPixels+64);
            }
            InvalidateVisual();
            if(presented-_lastReport>.25 || !moving) { _lastReport=presented;FramePresented?.Invoke(); }
            if(moving)
            {
                var remaining=33-(int)((_clock.Elapsed.TotalSeconds-started)*1000);
                if(remaining>0) await Task.Delay(remaining,token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception e) { AutoRotate=false;RenderFailed?.Invoke(e.Message); }
        finally
        {
            _rendering=false;
            // Start after the previous frame rather than lose an entire timer tick on dense meshes.
            if(!_disposed && _attached && IsEffectivelyVisible && (AutoRotate || _dragging))
                Dispatcher.UIThread.Post(async ()=>await RefreshFrameAsync(),DispatcherPriority.Background);
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side=Math.Max(0,Math.Min(Bounds.Width,Bounds.Height));
        var rect=new Rect((Bounds.Width-side)/2,(Bounds.Height-side)/2,side,side);
        context.FillRectangle(Brush.Parse("#151F2D"),rect);
        if(_bitmap!=null) context.DrawImage(_bitmap,rect);
    }
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;_timer.Stop();_lifetime.Cancel();_bitmap?.Dispose();_bitmap=null;_scene=null;
        // Render tasks retain a token, not the source. Their cancellation callback needs no disposal.
        _lifetime.Dispose();
    }
}
