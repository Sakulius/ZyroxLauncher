using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Zyrox.Services;

namespace Zyrox.Controls;

public class SkinViewer : Border
{
    private const double SwayDegrees = 9;
    private const double SwaySpeed = 0.4;
    private const double DragSensitivity = 0.6;
    private const double MaxPitch = 30;
    private const double DefaultYaw = -16;
    private const double OverlayHeadInflate = 0.5;
    private const double OverlayBodyInflate = 0.25;
    private const double VerticalFieldOfView = 47;
    private const double CameraDistance = 50;
    private const double CapeRestAngle = 8;
    private const double IdleSeconds = 45;
    private const double WaveSeconds = 2.2;
    private const double CheerSeconds = 1.8;
    private const double SitDrop = 7;
    private const double ModelCenter = 16;
    private const double ArmReach = 16.5;

    private readonly Model3DGroup _skinModel = new();
    private readonly AxisAngleRotation3D _yawRotation = new(new Vector3D(0, 1, 0), DefaultYaw);
    private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _headYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _headPitch = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rightArmSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftArmSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rightArmSpread = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _leftArmSpread = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _rightLegSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftLegSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _capeSwing = new(new Vector3D(1, 0, 0), CapeRestAngle);
    private readonly TranslateTransform3D _bodyOffset = new(0, -ModelCenter, 0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PerspectiveCamera _camera;

    private double _yaw = DefaultYaw;
    private double _swayStart;
    private double _lastFrame;
    private double _walkPhase;
    private double _walkAmount;
    private double _waveAmount;
    private double _cheerAmount;
    private double _sitAmount;
    private double _waveUntil;
    private double _cheerUntil;
    private double _lastActivity;
    private Point _lastMouse;
    private Point _dragStart;
    private double _dragYaw;
    private double _dragPitch;
    private bool _isDragging;
    private bool _animationEnabled = true;
    private bool _isHooked;

    public SkinViewer()
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeWE;
        ToolTip = "Drag to rotate";

        var rotation = new Transform3DGroup();
        rotation.Children.Add(_bodyOffset);
        rotation.Children.Add(new RotateTransform3D(_yawRotation));
        rotation.Children.Add(new RotateTransform3D(_pitchRotation));
        _skinModel.Transform = rotation;

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0xB8, 0xB8, 0xB8)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x58, 0x58, 0x58), new Vector3D(-0.3, -0.4, -1)));
        scene.Children.Add(_skinModel);

        _camera = new PerspectiveCamera(new Point3D(0, 0, CameraDistance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 30);
        var viewport = new Viewport3D { IsHitTestVisible = false, Camera = _camera };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(viewport, EdgeMode.Aliased);

        var shadow = new Ellipse
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(Color.FromArgb(0x90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))
        };
        var host = new Grid();
        host.Children.Add(shadow);
        host.Children.Add(viewport);
        Child = host;

        SizeChanged += (_, e) =>
        {
            var aspect = e.NewSize.Height > 0 ? e.NewSize.Width / e.NewSize.Height : 1;
            var fullHeight = Math.Tan(VerticalFieldOfView * Math.PI / 360);
            var half = Math.Max(fullHeight, ArmReach / (CameraDistance * Math.Max(aspect, 0.01)));
            _camera.FieldOfView = 2 * Math.Atan(half * aspect) * 180 / Math.PI;

            var zoom = fullHeight / half;
            shadow.Width = e.NewSize.Height * 0.36 * zoom;
            shadow.Height = e.NewSize.Height * 0.07 * zoom;
            shadow.Margin = new Thickness(0, 0, 0, e.NewSize.Height * (0.5 - 0.425 * zoom));
        };
        Loaded += (_, _) => UpdateHook();
        Unloaded += (_, _) => UpdateHook();
    }

    public bool AnimationEnabled
    {
        get => _animationEnabled;
        set
        {
            if (_animationEnabled == value)
                return;
            _animationEnabled = value;
            _lastFrame = _clock.Elapsed.TotalSeconds;
            UpdateHook();
        }
    }

    public bool IsWalking { get; set; }

    public void PlayWave() => _waveUntil = _clock.Elapsed.TotalSeconds + WaveSeconds;

    public void PlayCheer() => _cheerUntil = _clock.Elapsed.TotalSeconds + CheerSeconds;

    public void SetView(double yaw, double pitch)
    {
        _yaw = yaw;
        _yawRotation.Angle = yaw;
        _pitchRotation.Angle = pitch;
        _swayStart = _clock.Elapsed.TotalSeconds;
    }

    public void SetSkin(SkinTexture skin)
    {
        _skinModel.Children.Clear();
        var arm = skin.Slim ? 3 : 4;

        var head = PivotTransform(new Point3D(0, 24, 0), _headYaw, _headPitch);
        var rightArm = PivotTransform(new Point3D(-4 - arm / 2.0, 22, 0), _rightArmSpread, _rightArmSwing);
        var leftArm = PivotTransform(new Point3D(4 + arm / 2.0, 22, 0), _leftArmSpread, _leftArmSwing);
        var rightLeg = PivotTransform(new Point3D(-2, 12, 0), _rightLegSwing);
        var leftLeg = PivotTransform(new Point3D(2, 12, 0), _leftLegSwing);

        AddPart(skin.Base, skin.Height, head, 0, 0, 8, 8, 8, -4, 24, -4, 0);
        AddPart(skin.Base, skin.Height, null, 16, 16, 8, 12, 4, -4, 12, -2, 0);
        AddPart(skin.Base, skin.Height, rightArm, 40, 16, arm, 12, 4, -4 - arm, 12, -2, 0);
        AddPart(skin.Base, skin.Height, rightLeg, 0, 16, 4, 12, 4, -4, 0, -2, 0);
        if (skin.IsLegacy)
        {
            AddPart(skin.Base, skin.Height, leftArm, 40, 16, arm, 12, 4, 4, 12, -2, 0, mirror: true);
            AddPart(skin.Base, skin.Height, leftLeg, 0, 16, 4, 12, 4, 0, 0, -2, 0, mirror: true);
        }
        else
        {
            AddPart(skin.Base, skin.Height, leftArm, 32, 48, arm, 12, 4, 4, 12, -2, 0);
            AddPart(skin.Base, skin.Height, leftLeg, 16, 48, 4, 12, 4, 0, 0, -2, 0);
        }

        if (skin.Cape != null)
        {
            var cape = new Transform3DGroup();
            cape.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180), new Point3D(0, 16, -2.5)));
            cape.Children.Add(new RotateTransform3D(_capeSwing, new Point3D(0, 24, -2)));
            AddPart(skin.Cape, SkinTexture.CapeHeight, cape, 0, 0, 10, 16, 1, -5, 8, -3, 0);
        }

        if (skin.Overlay == null)
            return;

        if (!skin.IsLegacy)
        {
            AddPart(skin.Overlay, skin.Height, rightArm, 40, 32, arm, 12, 4, -4 - arm, 12, -2, OverlayBodyInflate);
            AddPart(skin.Overlay, skin.Height, leftArm, 48, 48, arm, 12, 4, 4, 12, -2, OverlayBodyInflate);
            AddPart(skin.Overlay, skin.Height, rightLeg, 0, 32, 4, 12, 4, -4, 0, -2, OverlayBodyInflate);
            AddPart(skin.Overlay, skin.Height, leftLeg, 0, 48, 4, 12, 4, 0, 0, -2, OverlayBodyInflate);
            AddPart(skin.Overlay, skin.Height, null, 16, 32, 8, 12, 4, -4, 12, -2, OverlayBodyInflate);
        }
        AddPart(skin.Overlay, skin.Height, head, 32, 0, 8, 8, 8, -4, 24, -4, OverlayHeadInflate);
    }

    private static Transform3D PivotTransform(Point3D pivot, params AxisAngleRotation3D[] rotations)
    {
        var group = new Transform3DGroup();
        foreach (var rotation in rotations)
            group.Children.Add(new RotateTransform3D(rotation, pivot));
        return group;
    }

    private void AddPart(ImageSource texture, int textureHeight, Transform3D? transform, int u, int v, int w, int h, int d,
        double x, double y, double z, double inflate, bool mirror = false)
    {
        var mesh = new MeshGeometry3D();
        AddBox(mesh, textureHeight, u, v, w, h, d, x, y, z, inflate, mirror);
        mesh.Freeze();

        var brush = new ImageBrush(texture)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();

        var model = new GeometryModel3D(mesh, new DiffuseMaterial(brush));
        if (transform != null)
            model.Transform = transform;
        _skinModel.Children.Add(model);
    }

    private static void AddBox(MeshGeometry3D mesh, int textureHeight, int u, int v, int w, int h, int d,
        double x, double y, double z, double inflate, bool mirror)
    {
        var x0 = x - inflate;
        var x1 = x + w + inflate;
        var y0 = y - inflate;
        var y1 = y + h + inflate;
        var z0 = z - inflate;
        var z1 = z + d + inflate;

        var sideRight = new Rect(u, v + d, d, h);
        var sideLeft = new Rect(u + d + w, v + d, d, h);
        if (mirror)
            (sideRight, sideLeft) = (sideLeft, sideRight);

        AddFace(mesh, textureHeight, new Rect(u + d, v + d, w, h), mirror,
            new(x0, y1, z1), new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1));
        AddFace(mesh, textureHeight, new Rect(u + d + w + d, v + d, w, h), mirror,
            new(x1, y1, z0), new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0));
        AddFace(mesh, textureHeight, sideRight, mirror,
            new(x0, y1, z0), new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1));
        AddFace(mesh, textureHeight, sideLeft, mirror,
            new(x1, y1, z1), new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0));
        AddFace(mesh, textureHeight, new Rect(u + d, v, w, d), mirror,
            new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0));
        AddFace(mesh, textureHeight, new Rect(u + d + w, v, w, d), mirror,
            new(x0, y0, z1), new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1));
    }

    private static void AddFace(MeshGeometry3D mesh, int textureHeight, Rect uv, bool mirror,
        Point3D topLeft, Point3D bottomLeft, Point3D bottomRight, Point3D topRight)
    {
        const double inset = 0.03;
        var left = (uv.Left + inset) / SkinTexture.Width;
        var right = (uv.Right - inset) / SkinTexture.Width;
        var top = (uv.Top + inset) / textureHeight;
        var bottom = (uv.Bottom - inset) / textureHeight;
        if (mirror)
            (left, right) = (right, left);

        var index = mesh.Positions.Count;
        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(bottomLeft);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(topRight);
        mesh.TextureCoordinates.Add(new Point(left, top));
        mesh.TextureCoordinates.Add(new Point(left, bottom));
        mesh.TextureCoordinates.Add(new Point(right, bottom));
        mesh.TextureCoordinates.Add(new Point(right, top));
        foreach (var offset in new[] { 0, 1, 2, 0, 2, 3 })
            mesh.TriangleIndices.Add(index + offset);
    }

    private void UpdateHook()
    {
        var shouldRun = _animationEnabled && IsLoaded;
        if (shouldRun == _isHooked)
            return;
        _isHooked = shouldRun;
        if (shouldRun)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible)
            return;

        var now = _clock.Elapsed.TotalSeconds;
        var delta = Math.Min(now - _lastFrame, 0.1);
        _lastFrame = now;

        if (!_isDragging)
            _yawRotation.Angle = _yaw + SwayDegrees * Math.Sin((now - _swayStart) * SwaySpeed);

        UpdateHeadLook(delta);

        var mouse = Mouse.GetPosition(this);
        if (_isDragging || IsWalking || mouse != _lastMouse || now < _waveUntil || now < _cheerUntil)
        {
            _lastActivity = now;
            _lastMouse = mouse;
        }

        var blend = Math.Min(1, delta * 6);
        _walkAmount += ((IsWalking ? 1 : 0) - _walkAmount) * blend;
        _waveAmount += ((now < _waveUntil ? 1 : 0) - _waveAmount) * blend;
        _cheerAmount += ((now < _cheerUntil ? 1 : 0) - _cheerAmount) * blend;
        _sitAmount += ((now - _lastActivity > IdleSeconds ? 1 : 0) - _sitAmount) * Math.Min(1, delta * 3);

        _walkPhase += delta * 7;
        var walk = Math.Sin(_walkPhase) * 38 * _walkAmount;
        var breathe = Math.Sin(now * 1.6);

        _rightArmSwing.Angle = Lerp(walk + breathe * 1.5, -38, _sitAmount);
        _leftArmSwing.Angle = Lerp(-walk - breathe * 1.5, -38, _sitAmount);
        _rightLegSwing.Angle = Lerp(-walk, -88, _sitAmount);
        _leftLegSwing.Angle = Lerp(walk, -88, _sitAmount);

        var rightSpread = -3 - breathe * 1.2;
        var leftSpread = 3 + breathe * 1.2;
        rightSpread = Lerp(rightSpread, -150 + Math.Sin(now * 11) * 16, _waveAmount);
        rightSpread = Lerp(rightSpread, -158 + Math.Sin(now * 9) * 8, _cheerAmount);
        leftSpread = Lerp(leftSpread, 158 - Math.Sin(now * 9) * 8, _cheerAmount);
        _rightArmSpread.Angle = rightSpread;
        _leftArmSpread.Angle = leftSpread;

        var bounce = Math.Abs(Math.Sin(now * 7)) * 2.6 * _cheerAmount;
        _bodyOffset.OffsetY = -ModelCenter + bounce - SitDrop * _sitAmount;
        _capeSwing.Angle = CapeRestAngle + 2 * Math.Sin(now * 1.1) + 18 * _walkAmount + Math.Abs(walk) * 0.12 + 62 * _sitAmount;
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private void UpdateHeadLook(double delta)
    {
        double targetYaw = 0;
        double targetPitch = 0;
        if (!_isDragging && Window.GetWindow(this) is { IsMouseOver: true })
        {
            var mouse = Mouse.GetPosition(this);
            var dx = mouse.X - ActualWidth / 2;
            var dy = mouse.Y - ActualHeight * 0.2;
            targetYaw = Math.Clamp(Math.Atan2(dx, 500) * 180 / Math.PI - _yawRotation.Angle, -35, 35);
            targetPitch = Math.Clamp(Math.Atan2(dy, 500) * 180 / Math.PI, -16, 16);
        }
        var smoothing = Math.Min(1, delta * 7);
        _headYaw.Angle += (targetYaw - _headYaw.Angle) * smoothing;
        _headPitch.Angle += (targetPitch - _headPitch.Angle) * smoothing;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _isDragging = CaptureMouse();
        if (!_isDragging)
            return;
        _dragStart = e.GetPosition(this);
        _dragYaw = _yawRotation.Angle;
        _dragPitch = _pitchRotation.Angle;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging)
            return;
        var delta = e.GetPosition(this) - _dragStart;
        _yawRotation.Angle = _dragYaw + delta.X * DragSensitivity;
        _pitchRotation.Angle = Math.Clamp(_dragPitch + delta.Y * DragSensitivity, -MaxPitch, MaxPitch);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
            ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_isDragging)
            return;
        _isDragging = false;
        SetView(_yawRotation.Angle, _pitchRotation.Angle);
    }
}
