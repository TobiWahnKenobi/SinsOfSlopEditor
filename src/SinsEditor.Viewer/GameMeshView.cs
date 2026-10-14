using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SinsEditor.Core.Textures;

namespace SinsEditor.Viewer;

/// <summary>What a picture of the view should be: its size, an optional background, transparent otherwise.</summary>
public sealed record CaptureRequest(int Width, int Height, PixelImage? Background, int Supersample = 4);

/// <summary>Lets a page ask the view for a picture (the view owns the renderer).</summary>
public sealed class ViewportCapture
{
    internal Func<CaptureRequest, PixelImage?>? Handler { get; set; }

    /// <summary>The scene as seen now, without hardpoints and wireframe; null if nothing can be drawn.</summary>
    public PixelImage? Capture(CaptureRequest request) => Handler?.Invoke(request);
}

/// <summary>
/// Shows a <see cref="GameScene"/> drawn by <see cref="GameRenderer"/> with the game's shaders. Drag to rotate (left or right
/// button), wheel to zoom, middle button or Shift+drag to pan, double-click to frame the mesh again.
/// </summary>
public sealed class GameMeshView : Grid
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(GameScene), typeof(GameMeshView),
        new PropertyMetadata(null, (d, e) => ((GameMeshView)d).OnSceneChanged(e.OldValue as GameScene, e.NewValue as GameScene)));

    public static readonly DependencyProperty ShowPointsProperty = DependencyProperty.Register(nameof(ShowPoints), typeof(bool), typeof(GameMeshView),
        new PropertyMetadata(true, (d, _) => ((GameMeshView)d).Invalidate()));

    public static readonly DependencyProperty ShowParticlesProperty = DependencyProperty.Register(nameof(ShowParticles), typeof(bool), typeof(GameMeshView),
        new PropertyMetadata(true, (d, _) => ((GameMeshView)d).Invalidate()));

    /// <summary>Seconds into the particle effects. Changing it (also backwards) runs the simulations there.</summary>
    public static readonly DependencyProperty ParticleTimeProperty = DependencyProperty.Register(nameof(ParticleTime), typeof(double), typeof(GameMeshView),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((GameMeshView)d).OnParticleTimeChanged()));

    /// <summary>While true, <see cref="ParticleTime"/> advances with real time × <see cref="PlaybackSpeed"/>, wrapping at <see cref="ParticleDuration"/>.</summary>
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(GameMeshView),
        new PropertyMetadata(false, (d, _) => ((GameMeshView)d).Invalidate()));

    public static readonly DependencyProperty PlaybackSpeedProperty = DependencyProperty.Register(nameof(PlaybackSpeed), typeof(double), typeof(GameMeshView),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty ParticleDurationProperty = DependencyProperty.Register(nameof(ParticleDuration), typeof(double), typeof(GameMeshView),
        new PropertyMetadata(10.0));

    public static readonly DependencyProperty CaptureProperty = DependencyProperty.Register(nameof(Capture), typeof(ViewportCapture), typeof(GameMeshView),
        new PropertyMetadata(null, (d, e) =>
        {
            if (e.OldValue is ViewportCapture old) old.Handler = null;
            if (e.NewValue is ViewportCapture capture) capture.Handler = ((GameMeshView)d).CaptureImage;
        }));

    public static readonly DependencyProperty ShowWireframeProperty = DependencyProperty.Register(nameof(ShowWireframe), typeof(bool), typeof(GameMeshView),
        new PropertyMetadata(false, (d, _) => ((GameMeshView)d).Invalidate()));

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _error = new()
    {
        Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
    };
    private GameRenderer? _renderer;
    private OrbitCamera _camera;
    private bool _dirty, _hooked;
    private Point _lastMouse;
    private WriteableBitmap? _bitmap;

    public GameMeshView()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 34));
        ClipToBounds = true;
        Children.Add(_image);
        Children.Add(_error);
        SizeChanged += (_, _) => Invalidate();
        Loaded += (_, _) => Invalidate();
        Unloaded += (_, _) => Unhook();
        Focusable = true;
    }

    public GameScene? Scene
    {
        get => (GameScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool ShowPoints
    {
        get => (bool)GetValue(ShowPointsProperty);
        set => SetValue(ShowPointsProperty, value);
    }

    public bool ShowParticles
    {
        get => (bool)GetValue(ShowParticlesProperty);
        set => SetValue(ShowParticlesProperty, value);
    }

    public double ParticleTime
    {
        get => (double)GetValue(ParticleTimeProperty);
        set => SetValue(ParticleTimeProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double PlaybackSpeed
    {
        get => (double)GetValue(PlaybackSpeedProperty);
        set => SetValue(PlaybackSpeedProperty, value);
    }

    public double ParticleDuration
    {
        get => (double)GetValue(ParticleDurationProperty);
        set => SetValue(ParticleDurationProperty, value);
    }

    public ViewportCapture? Capture
    {
        get => (ViewportCapture?)GetValue(CaptureProperty);
        set => SetValue(CaptureProperty, value);
    }

    private TimeSpan _lastFrame;

    private void OnParticleTimeChanged()
    {
        foreach (var effect in Scene?.Particles ?? []) effect.System.RunTo((float)ParticleTime);
        Invalidate();
    }

    /// <summary>A picture of the scene as seen now, rendered larger and scaled down for smooth edges; transparent without a background.</summary>
    private PixelImage? CaptureImage(CaptureRequest request)
    {
        if (_renderer is null || Scene is null) return null;
        int factor = Math.Max(1, Math.Min(request.Supersample, 4096 / Math.Max(request.Width, request.Height)));
        int width = request.Width * factor, height = request.Height * factor;
        byte[] pixels;
        try
        {
            // A picture narrower than the view (e.g. a square icon from a wide view) would cut off what's seen at the sides:
            // move back so the view's width fits.
            double viewAspect = ActualWidth / Math.Max(1, ActualHeight), pictureAspect = (double)request.Width / request.Height;
            var camera = pictureAspect < viewAspect ? _camera with { Distance = _camera.Distance * (float)(viewAspect / pictureAspect) } : _camera;
            pixels = _renderer.Render(width, height, camera,
                new GameRenderOptions(false, false, ShowParticles, Transparent: true, Time: (float)ParticleTime));
        }
        catch (SharpDX.SharpDXException ex)
        {
            ShowError($"Drawing failed: {ex.Message}");
            return null;
        }
        Invalidate();
        var picture = factor == 1 ? new PixelImage(width, height, pixels) : Downsample(pixels, width, height, factor);
        // The model is rendered on transparency and laid over the background afterwards, so the background's own alpha
        // (e.g. a round button) is kept and the model's edges blend into it.
        return request.Background is { } background ? Over(picture, background) : picture;
    }

    /// <summary>Porter-Duff "over": <paramref name="top"/> on <paramref name="bottom"/> (scaled to the same size), straight alpha.</summary>
    public static PixelImage Over(PixelImage top, PixelImage bottom)
    {
        if (bottom.Width != top.Width || bottom.Height != top.Height) bottom = ImageOps.Scale(bottom, top.Width, top.Height);
        var result = new byte[top.Bgra.Length];
        for (int i = 0; i < result.Length; i += 4)
        {
            double at = top.Bgra[i + 3] / 255.0, ab = bottom.Bgra[i + 3] / 255.0;
            double a = at + ab * (1 - at);
            for (int c = 0; c < 3; c++)
                result[i + c] = a <= 0 ? (byte)0 : (byte)Math.Round((top.Bgra[i + c] * at + bottom.Bgra[i + c] * ab * (1 - at)) / a);
            result[i + 3] = (byte)Math.Round(a * 255);
        }
        return new PixelImage(top.Width, top.Height, result);
    }

    /// <summary>Box filter by an integer factor, averaging with premultiplied alpha so transparent edges don't darken.</summary>
    private static PixelImage Downsample(byte[] source, int width, int height, int factor)
    {
        int w = width / factor, h = height / factor;
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double b = 0, g = 0, r = 0, a = 0;
            for (int sy = 0; sy < factor; sy++)
            for (int sx = 0; sx < factor; sx++)
            {
                int i = ((y * factor + sy) * width + x * factor + sx) * 4;
                double alpha = source[i + 3] / 255.0;
                b += source[i] * alpha; g += source[i + 1] * alpha; r += source[i + 2] * alpha; a += alpha;
            }
            int o = (y * w + x) * 4;
            if (a > 0)
            {
                result[o] = (byte)Math.Round(b / a); result[o + 1] = (byte)Math.Round(g / a); result[o + 2] = (byte)Math.Round(r / a);
            }
            result[o + 3] = (byte)Math.Round(a / (factor * factor) * 255);
        }
        return new PixelImage(w, h, result);
    }

    public bool ShowWireframe
    {
        get => (bool)GetValue(ShowWireframeProperty);
        set => SetValue(ShowWireframeProperty, value);
    }

    /// <summary>Why the scene can't be drawn (or parts of it), if so.</summary>
    public string? Error => _renderer?.Error;

    private void OnSceneChanged(GameScene? old, GameScene? scene)
    {
        if (scene is null) return;
        try
        {
            _renderer ??= new GameRenderer();
            _renderer.SetScene(scene);
            // Keep the camera when only options (team colour, skybox) change for the same mesh.
            if (old?.Primary != scene.Primary) _camera = OrbitCamera.Frame(scene.Primary);
            foreach (var effect in scene.Particles ?? []) effect.System.RunTo((float)ParticleTime);
            ShowError(_renderer.Error);
        }
        catch (Exception e) when (e is SharpDX.SharpDXException or DllNotFoundException or ShaderCompileException)
        {
            ShowError($"The game shaders can't be drawn here: {e.Message}");
        }
        Invalidate();
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Invalidate()
    {
        _dirty = true;
        if (_hooked || !IsLoaded) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Playback: advance the particle time by the frame's real time × speed.
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (IsPlaying && Scene?.IsAnimated == true)
        {
            double dt = _lastFrame == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
            double time = ParticleTime + dt * PlaybackSpeed;
            double duration = Math.Max(0.1, ParticleDuration);
            ParticleTime = time > duration ? time % duration : time < 0 ? duration + time % duration : time;
            _lastFrame = now;
            _dirty = true;
        }
        else _lastFrame = TimeSpan.Zero;
        Unhook();
        if (!_dirty || _renderer is null || Scene is null || ActualWidth < 2 || ActualHeight < 2) return;
        _dirty = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)(ActualWidth * dpi.DpiScaleX), height = (int)(ActualHeight * dpi.DpiScaleY);
        byte[] pixels;
        try
        {
            pixels = _renderer.Render(width, height, _camera, new GameRenderOptions(ShowPoints, ShowWireframe, ShowParticles, Time: (float)ParticleTime));
        }
        catch (SharpDX.SharpDXException ex)
        {
            ShowError($"Drawing failed: {ex.Message}");
            return;
        }
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
            _image.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        // Keep rendering frames while playing.
        if (IsPlaying && Scene?.IsAnimated == true) Invalidate();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ClickCount == 2 && Scene is { } scene)
        {
            _camera = OrbitCamera.Frame(scene.Primary);
            Invalidate();
            return;
        }
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;
        var position = e.GetPosition(this);
        var delta = position - _lastMouse;
        _lastMouse = position;
        bool pan = e.MiddleButton == MouseButtonState.Pressed || (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.LeftButton == MouseButtonState.Pressed);
        if (pan)
        {
            // Move the target in the camera plane, as far as the mouse moved at the target's distance.
            var forward = -_camera.Direction;
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            var up = Vector3.Cross(forward, right);
            float perPixel = 2 * _camera.Distance * MathF.Tan(45f * MathF.PI / 360f) / (float)Math.Max(1, ActualHeight);
            _camera = _camera with { Target = _camera.Target + (-right * (float)delta.X + up * (float)delta.Y) * perPixel };
        }
        else if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
        {
            float pitch = Math.Clamp(_camera.Pitch + (float)delta.Y * 0.01f, -1.5f, 1.5f);
            _camera = _camera with { Yaw = _camera.Yaw + (float)delta.X * 0.01f, Pitch = pitch };
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _camera = _camera with { Distance = Math.Max(0.5f, _camera.Distance * MathF.Pow(0.9f, e.Delta / 120f)) };
        Invalidate();
    }
}
