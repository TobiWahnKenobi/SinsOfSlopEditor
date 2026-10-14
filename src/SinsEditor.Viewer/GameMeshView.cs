using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SinsEditor.Viewer;

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
        Unhook();
        if (!_dirty || _renderer is null || Scene is null || ActualWidth < 2 || ActualHeight < 2) return;
        _dirty = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)(ActualWidth * dpi.DpiScaleX), height = (int)(ActualHeight * dpi.DpiScaleY);
        byte[] pixels;
        try
        {
            pixels = _renderer.Render(width, height, _camera, new GameRenderOptions(ShowPoints, ShowWireframe));
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
