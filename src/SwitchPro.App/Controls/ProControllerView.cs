using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SwitchPro.Protocol;

namespace SwitchPro.App.Controls;

/// <summary>
/// 矢量绘制的 Switch Pro 手柄实时视图（实现思路参考 Eden PlayerControlPreview 的
/// QPainter 自绘方案：主体多边形 + 黑描边 + 摇杆双圆偏移 + 按下高亮），
/// 配色采用其浅色主题分支：面板 #E1E1E1、按键 #6D6F72/#4D5054、按下 #3BA55D。
/// 坐标布局为原创实现。
/// </summary>
public sealed class ProControllerView : Canvas
{
    // ---- 浅色主题配色 ----
    private static readonly Brush BodyBrush = Frozen(0xE1, 0xE1, 0xE1);
    private static readonly Brush ButtonBrush = Frozen(0x6D, 0x6F, 0x72);
    private static readonly Brush Button2Brush = Frozen(0x4D, 0x50, 0x54);
    private static readonly Brush OutlineBrush = Frozen(0x00, 0x00, 0x00);
    private static readonly Brush GlyphLightBrush = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush GlyphDarkBrush = Frozen(0x00, 0x00, 0x00);
    private static readonly Brush PressedBrush = Frozen(0xAA, 0x00, 0x00); // Eden colors.highlight
    private static readonly Brush PressedTextBrush = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush DeadzoneBrush = Frozen(0xAA, 0x00, 0x00);
    private static readonly Brush ChargingBrush = Frozen(0xFA, 0xA8, 0x1A);
    private static readonly Brush BatteryBrush = Frozen(0x53, 0xC2, 0x6E);
    private static readonly Brush CellBgBrush = Frozen(0xF2, 0xF3, 0xF5);

    private readonly Dictionary<ProButtons, Shape> _buttons = new();
    private readonly Dictionary<ProButtons, TextBlock> _glyphs = new();
    private Rectangle _batteryFill = null!;
    private TranslateTransform _leftTriggerShift = null!;
    private TranslateTransform _rightTriggerShift = null!;

    private StickVisual _leftStick = null!;
    private StickVisual _rightStick = null!;
    private StickIndicator _leftIndicator = null!;
    private StickIndicator _rightIndicator = null!;

    private const double CenterX = 330;
    private const double CenterY = 235;

    public ProControllerView()
    {
        Width = 660;
        Height = 470;

        // Eden 绘制层序：肩部条带 + L/R 触发器 → 握把 → 主体 → 摇杆井 → 细节
        DrawTriggersAndBody();

        _leftStick = BuildStick(CenterX - 111, CenterY - 55);
        _rightStick = BuildStick(CenterX + 51, CenterY + 0);

        BuildDpad(CenterX - 61, CenterY + 0);
        BuildFace(CenterX + 105, CenterY - 56);
        BuildCenterCluster();

        _leftIndicator = BuildIndicator(CenterX - 50, CenterY + 105);
        _rightIndicator = BuildIndicator(CenterX + 50, CenterY + 105);

        DrawBattery();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ================= 主体 =================

    // ================= 主体（形状数据取自 Eden，见 ProControllerShape.cs） =================

    private void DrawTriggersAndBody()
    {
        // 形状数据为中心相对坐标，统一偏移到画布中心 (330,235)

        // 1. 肩部条带（主体顶缘）
        Add(ShapePath(ProControllerShape.BodyTop, BodyBrush), CenterX, CenterY);

        // 2. L / R 触发器（肩键多边形，按下时下移 2px 并变红——Eden DrawProTriggers）
        _leftTriggerShift = new TranslateTransform();
        var leftTrigger = ShapePath(ProControllerShape.LeftTrigger, ButtonBrush);
        leftTrigger.RenderTransform = _leftTriggerShift;
        Add(leftTrigger, CenterX, CenterY);
        _buttons[ProButtons.L] = leftTrigger;

        _rightTriggerShift = new TranslateTransform();
        var rightTrigger = ShapePath(ProControllerShape.RightTrigger, ButtonBrush);
        rightTrigger.RenderTransform = _rightTriggerShift;
        Add(rightTrigger, CenterX, CenterY);
        _buttons[ProButtons.R] = rightTrigger;

        // 3. 握把（左/右，深色）
        Add(ShapePath(ProControllerShape.LeftHandle, ButtonBrush), CenterX, CenterY);
        Add(ShapePath(ProControllerShape.RightHandle, ButtonBrush), CenterX, CenterY);

        // 4. 主体（浅色面板，盖在握把上）
        Add(ShapePath(ProControllerShape.Body, BodyBrush), CenterX, CenterY);

        // 5. ZL / ZR 徽章（主体上缘两侧，Eden DrawTriggerButton 位置 ±210,-120）
        _buttons[ProButtons.Zl] = AddRoundRect(ButtonBrush, 87, 100, 66, 30, 9, OutlineBrush);
        _buttons[ProButtons.Zr] = AddRoundRect(ButtonBrush, 507, 100, 66, 30, 9, OutlineBrush);
        AddGlyph("ZL", 87, 100, 66, 30, GlyphLightBrush);
        AddGlyph("ZR", 507, 100, 66, 30, GlyphLightBrush);
    }

    private Path ShapePath(string data, Brush fill)
        => new()
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = OutlineBrush,
            StrokeThickness = 1.5,
        };

    // ================= 构建辅助 =================

    private void Add(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Children.Add(element);
    }

    private static Ellipse Ellipse_(Brush fill, double w, double h) => new() { Fill = fill, Width = w, Height = h };

    private Rectangle AddRoundRect(Brush fill, double x, double y, double w, double h, double radius,
        Brush? stroke = null)
    {
        var rect = new Rectangle
        {
            Fill = fill,
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : 1.2,
        };
        Add(rect, x, y);
        return rect;
    }

    private sealed record StickVisual(double Cx, double Cy, Ellipse Outer, Ellipse Inner, Ellipse DeadzoneRing);

    private StickVisual BuildStick(double cx, double cy)
    {
        // 摇杆井：黑描边圆（透明填充）
        var well = new Ellipse
        {
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            Width = 68,
            Height = 68,
        };
        Add(well, cx - 34, cy - 34);

        // 死区圈（红色虚线，Eden deadzone #AA0000）
        var dz = new Ellipse
        {
            Stroke = DeadzoneBrush,
            StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 2.5, 2 },
            Width = 12,
            Height = 12,
        };
        Add(dz, cx - 6, cy - 6);

        // 摇杆外圈（r=24）+ 内圈（r=17）
        var outer = new Ellipse
        {
            Fill = ButtonBrush,
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            Width = 48,
            Height = 48,
        };
        Add(outer, cx - 24, cy - 24);

        var inner = new Ellipse
        {
            Fill = Button2Brush,
            Width = 34,
            Height = 34,
        };
        Add(inner, cx - 17, cy - 17);

        return new StickVisual(cx, cy, outer, inner, dz);
    }

    private void BuildDpad(double cx, double cy)
    {
        // 四向箭头臂（黑描边）
        _buttons[ProButtons.Up] = AddRoundRect(ButtonBrush, cx - 8, cy - 31, 16, 24, 4, OutlineBrush);
        _buttons[ProButtons.Down] = AddRoundRect(ButtonBrush, cx - 8, cy + 7, 16, 24, 4, OutlineBrush);
        _buttons[ProButtons.Left] = AddRoundRect(ButtonBrush, cx - 31, cy - 8, 24, 16, 4, OutlineBrush);
        _buttons[ProButtons.Right] = AddRoundRect(ButtonBrush, cx + 7, cy - 8, 24, 16, 4, OutlineBrush);
        AddRoundRect(ButtonBrush, cx - 8, cy - 8, 16, 16, 2, OutlineBrush);
    }

    private void BuildFace(double cx, double cy)
    {
        AddRoundFace(ProButtons.X, "X", cx, cy - 31, 15);
        AddRoundFace(ProButtons.A, "A", cx + 31, cy, 15);
        AddRoundFace(ProButtons.B, "B", cx, cy + 31, 15);
        AddRoundFace(ProButtons.Y, "Y", cx - 31, cy, 15);
    }

    private void AddRoundFace(ProButtons flag, string glyph, double cx, double cy, double r)
    {
        var ellipse = new Ellipse
        {
            Fill = ButtonBrush,
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            Width = r * 2,
            Height = r * 2,
        };
        Add(ellipse, cx - r, cy - r);
        _buttons[flag] = ellipse;

        var text = new TextBlock
        {
            Text = glyph,
            Foreground = GlyphLightBrush,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
        };
        Add(text, cx - 6.5, cy - 11.5);
        _glyphs[flag] = text;
    }

    private void BuildCenterCluster()
    {
        // - 与 +
        AddSmallRound(ProButtons.Minus, "−", CenterX - 50, CenterY - 86, 9, GlyphDarkBrush);
        AddSmallRound(ProButtons.Plus, "+", CenterX + 50, CenterY - 86, 9, GlyphDarkBrush);

        // CAPTURE（小方块）与 HOME（圆）
        _buttons[ProButtons.Capture] = AddRoundRect(ButtonBrush, CenterX - 33, CenterY - 62, 16, 16, 3, OutlineBrush);
        var home = new Ellipse
        {
            Fill = Frozen(0x99, 0x95, 0x95),
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            Width = 20,
            Height = 20,
        };
        Add(home, CenterX + 29 - 10, CenterY - 56 - 10);
        _buttons[ProButtons.Home] = home;
        var homeRing = new Ellipse
        {
            Fill = Brushes.Transparent,
            Stroke = GlyphDarkBrush,
            StrokeThickness = 1.6,
            Width = 11,
            Height = 11,
        };
        Add(homeRing, CenterX + 29 - 5.5, CenterY - 56 - 5.5);
    }

    private void AddSmallRound(ProButtons flag, string glyph, double cx, double cy, double r, Brush glyphBrush)
    {
        var ellipse = new Ellipse
        {
            Fill = ButtonBrush,
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            Width = r * 2,
            Height = r * 2,
        };
        Add(ellipse, cx - r, cy - r);
        _buttons[flag] = ellipse;

        var text = new TextBlock
        {
            Text = glyph,
            Foreground = glyphBrush,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
        };
        Add(text, cx - 5, cy - 10);
        _glyphs[flag] = text;
    }

    private void AddGlyph(string glyph, double x, double y, double w, double h, Brush brush)
    {
        var text = new TextBlock
        {
            Text = glyph,
            Foreground = brush,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
        };
        Add(text, x + w / 2 - 10, y + h / 2 - 11);
    }

    // ================= 底部摇杆原始指示器（Eden DrawRawJoystick） =================

    private sealed record StickIndicator(double Cx, double Cy, Ellipse Ring, Ellipse Dot);

    private StickIndicator BuildIndicator(double cx, double cy)
    {
        var ring = new Ellipse
        {
            Stroke = OutlineBrush,
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 3, 2.5 },
            Width = 44,
            Height = 44,
        };
        Add(ring, cx - 22, cy - 22);

        var dot = new Ellipse
        {
            Fill = Frozen(0x3B, 0xA5, 0x5D), // Eden colors.indicator2（绿色指示点）
            Stroke = Frozen(0xFF, 0xFF, 0xFF),
            StrokeThickness = 1,
            Width = 11,
            Height = 11,
        };
        Add(dot, cx - 5.5, cy - 5.5);
        return new StickIndicator(cx, cy, ring, dot);
    }

    private static void MoveIndicator(StickIndicator ind, (double x, double y) norm)
    {
        double max = 14;
        SetPos(ind.Dot, ind.Cx + norm.x * max - 5.5, ind.Cy - norm.y * max - 5.5);
    }

    // ================= 电池 =================

    private void DrawBattery()
    {
        // 外框
        var outline = new Rectangle
        {
            Stroke = OutlineBrush,
            StrokeThickness = 1.6,
            Width = 44,
            Height = 20,
            RadiusX = 4,
            RadiusY = 4,
            Fill = CellBgBrush,
        };
        Add(outline, CenterX - 20, CenterY - 160);
        // 电极
        AddRoundRect(OutlineBrush, CenterX + 24, CenterY - 155, 4, 10, 1);
        // 填充
        _batteryFill = new Rectangle
        {
            Fill = BatteryBrush,
            Width = 38,
            Height = 14,
            RadiusX = 2,
            RadiusY = 2,
        };
        Add(_batteryFill, CenterX - 17, CenterY - 157);
    }

    private static void SetPos(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }

    // ================= 实时更新 =================

    /// <summary>刷新视觉状态（UI 线程调用，33ms 级轮询）。</summary>
    public void UpdateState(ProControllerState? stateOrNull,
        (double x, double y) leftNorm, (double x, double y) rightNorm)
    {
        var pressed = stateOrNull?.Buttons ?? ProButtons.None;

        foreach (var (flag, shape) in _buttons)
        {
            shape.Fill = pressed.HasFlag(flag) ? PressedBrush : ButtonBrush;
        }
        // 触发器按下时下沉 2px（Eden DrawProTriggers）
        _leftTriggerShift.Y = pressed.HasFlag(ProButtons.L) ? 2 : 0;
        _rightTriggerShift.Y = pressed.HasFlag(ProButtons.R) ? 2 : 0;
        foreach (var (flag, text) in _glyphs)
        {
            text.Foreground = pressed.HasFlag(flag) ? PressedTextBrush : GlyphLightBrush;
        }

        MoveStick(_leftStick, leftNorm);
        MoveStick(_rightStick, rightNorm);
        MoveIndicator(_leftIndicator, leftNorm);
        MoveIndicator(_rightIndicator, rightNorm);

        if (stateOrNull is { } state)
        {
            double pct = Math.Clamp(state.BatteryLevel / 8.0, 0, 1);
            _batteryFill.Width = Math.Max(2, 38 * pct);
            _batteryFill.Fill = state.Charging ? ChargingBrush : BatteryBrush;
        }
    }

    public void SetDeadzones(double leftRatio, double rightRatio)
    {
        UpdateDeadzone(_leftStick, leftRatio);
        UpdateDeadzone(_rightStick, rightRatio);
    }

    private static void UpdateDeadzone(StickVisual stick, double ratio)
    {
        double r = 34 * Math.Clamp(ratio, 0, 0.9);
        SetPos(stick.DeadzoneRing, stick.Cx - r, stick.Cy - r);
        stick.DeadzoneRing.Width = stick.DeadzoneRing.Height = r * 2;
    }

    private static void MoveStick(StickVisual stick, (double x, double y) norm)
    {
        double max = 34 - 24 + 6;
        SetPos(stick.Outer, stick.Cx + norm.x * max - 24, stick.Cy - norm.y * max - 24);
        SetPos(stick.Inner, stick.Cx + norm.x * max - 17, stick.Cy - norm.y * max - 17);
    }
}
