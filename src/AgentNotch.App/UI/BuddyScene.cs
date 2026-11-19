using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AgentNotch.App.UI;

/// <summary>One vector character with distinct idle, reasoning, execution,
/// question, approval and completion choreography, at both notch sizes.</summary>
public sealed class BuddyScene : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(string), typeof(BuddyScene),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));
    public static readonly DependencyProperty AgentProperty = DependencyProperty.Register(
        nameof(Agent), typeof(string), typeof(BuddyScene),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
        nameof(Project), typeof(string), typeof(BuddyScene),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(BuddyScene),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnExpansionChanged));
    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string Agent { get => (string)GetValue(AgentProperty); set => SetValue(AgentProperty, value); }
    public string Project { get => (string)GetValue(ProjectProperty); set => SetValue(ProjectProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _changedAt, _expandedAt, _expandFrom;
    private Color _fromColor = Color.FromRgb(142, 161, 202);
    private double Time => _watch.Elapsed.TotalSeconds;
    private double Age => Math.Max(0, Time - _changedAt);
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static readonly Geometry Body = FrozenGeometry(
        "M 0,15 C -1,4 5,0 17,0 C 28,-1 45,-1 49,7 C 52,14 52,26 48,33 C 44,39 34,38 25,38 C 13,39 3,39 1,30 Z");
    private static readonly Brush BodyFill = Freeze(new LinearGradientBrush(
        new GradientStopCollection {
            new GradientStop(Color.FromRgb(255, 255, 255), 0),
            new GradientStop(Color.FromRgb(240, 245, 254), .52),
            new GradientStop(Color.FromRgb(181, 206, 244), 1)
        }, new Point(.2, 0), new Point(.65, 1)));
    private static readonly Brush BodyHighlight = Freeze(new LinearGradientBrush(
        Color.FromArgb(210,255,255,255), Colors.Transparent, 90));
    private static readonly Brush CrownBrush = Freeze(new LinearGradientBrush(
        Color.FromArgb(22,168,196,255), Colors.Transparent, new Point(.5,0), new Point(.5,1)));
    private static readonly Pen BodyOutlinePen = Freeze(new Pen(Ink("#DBE7FF"), .65));
    private static readonly Pen SmilePen = Freeze(new Pen(Ink("#253149"), 1.8));
    private static readonly Pen StarGlintPen = Freeze(new Pen(Ink("#C8D7F1"), .5));
    private static readonly Geometry SmileGeometry = BuildSmileGeometry();
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly FontFamily SegoeFont = new("Segoe UI");
    private static readonly Typeface SegoeRegular = new(SegoeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SegoeSemiBold = new(SegoeFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static Geometry BuildSmileGeometry()
    {
        var smile = new StreamGeometry();
        using (var c = smile.Open())
        {
            c.BeginFigure(new Point(12,23), false, false);
            c.QuadraticBezierTo(new Point(16,16),new Point(20,23),true,false);
            c.BeginFigure(new Point(30,23), false, false);
            c.QuadraticBezierTo(new Point(34,16),new Point(38,23),true,false);
            c.BeginFigure(new Point(21,28), false, false);
            c.QuadraticBezierTo(new Point(25,32),new Point(29,28),true,false);
        }
        smile.Freeze();
        return smile;
    }

    public BuddyScene()
    {
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _clock.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => SyncClock();
        Unloaded += (_, _) => _clock.Stop();
        IsVisibleChanged += (_, _) => SyncClock();
        ClipToBounds = true;
    }
    private void SyncClock()
    {
        if (IsLoaded && IsVisible)
        {
            // Idle collapsed only needs a gentle 10fps breathe instead of 20fps CPU blits.
            _clock.Interval = (!IsExpanded && State == "idle")
                ? TimeSpan.FromMilliseconds(100)
                : TimeSpan.FromMilliseconds(50);
            _clock.Start();
        }
        else _clock.Stop();
    }
    private static void OnStateChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var scene = (BuddyScene)obj;
        scene._fromColor = scene.CurrentColor(e.OldValue as string);
        scene._changedAt = scene.Time;
        scene.SyncClock();
    }
    private static void OnExpansionChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var scene = (BuddyScene)obj;
        scene._expandFrom = scene.Expansion((bool)e.OldValue);
        scene._expandedAt = scene.Time;
        scene.SyncClock();
        if (scene.IsLoaded)
            scene.BeginAnimation(HeightProperty, new DoubleAnimation(
                scene.ActualHeight, (bool)e.NewValue ? 76 : 38, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
    private double Expansion(bool expanded)
    {
        var p = Math.Clamp((Time-_expandedAt)/.18,0,1);
        var eased = (1-Math.Cos(p*Math.PI))/2;
        return _expandFrom + ((expanded ? 1 : 0)-_expandFrom)*eased;
    }

    private static Color StateColor(string state) => (Color)ColorConverter.ConvertFromString(state switch {
        "thinking" => "#6D9EFF", "working" => "#6ACBFF",
        "needsyou" => "#FFC087", "prompt" => "#E8ACFF",
        "finished" => "#7CE6BA", _ => "#8EA1CA"
    });
    private Color CurrentColor(string? state = null)
    {
        var to = StateColor(state ?? State);
        double p = Math.Clamp(Age / .5, 0, 1);
        return Color.FromRgb((byte)(_fromColor.R + (to.R-_fromColor.R)*p),
            (byte)(_fromColor.G + (to.G-_fromColor.G)*p),
            (byte)(_fromColor.B + (to.B-_fromColor.B)*p));
    }
    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private static Geometry FrozenGeometry(string data) => Freeze(Geometry.Parse(data));
    private static SolidColorBrush Ink(string color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
            Brushes[color] = brush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
        return brush;
    }
    private static void Stroke(DrawingContext dc, Brush ink, double thickness, params Point[] points)
    {
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        path.Freeze();
        var pen = new Pen(ink, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, path);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight, t = Time, age = Age;
        if (width < 60 || height < 20) return;
        bool compact = !IsExpanded, active = State is "thinking" or "working";
        bool waiting = State is "needsyou" or "prompt", done = State == "finished";
        double expansion = Expansion(IsExpanded);
        var accent = Freeze(new SolidColorBrush(CurrentColor()));
        var center = new Point(width/2+(48-width/2)*expansion, height/2+2);
        DrawSky(dc, width, height, center, accent, t, active);

        double scale = .65 + .39*expansion;
        // Brief spring on state change and on expansion; loops use softer breathing.
        double arrival = 1 + .07 * Math.Exp(-age*4) * Math.Sin(age*13);
        double hover = 1 + .035 * Math.Exp(-Math.Max(0,t-_expandedAt)*5) * Math.Sin((t-_expandedAt)*12);
        double breathe = 1 + .012 * Math.Sin(t*1.7);
        double bob = State switch {
            "thinking" => Math.Sin(t*2)*1.1,
            "working" => Math.Sin(t*3.4)*1.6,
            "needsyou" or "prompt" => Math.Sin(t*2.3)*1.7,
            "finished" when age < 2.2 => -Math.Abs(Math.Sin(age*5))*4*Math.Exp(-age*.65),
            _ => Math.Sin(t*1.3)*.8
        };
        double tilt = State switch {
            "thinking" => -5+Math.Sin(t*1.4)*2,
            "working" => Math.Sin(t*3)*3,
            "needsyou" or "prompt" => Math.Sin(t*1.8)*6,
            "finished" => Math.Sin(t*1.6)*2,
            _ => Math.Sin(t*.8)*2.5
        };

        dc.PushOpacity(.2);
        dc.DrawEllipse(accent, null, new Point(center.X, center.Y+19*scale), 21*scale, 2*scale);
        dc.Pop();
        dc.PushTransform(new TranslateTransform(center.X, center.Y+bob*scale));
        dc.PushTransform(new ScaleTransform(scale*arrival*hover*breathe, scale*arrival*hover/breathe));
        DrawAura(dc, accent, t, age);
        dc.PushTransform(new RotateTransform(tilt));
        dc.PushTransform(new TranslateTransform(-25,-19));
        dc.DrawGeometry(BodyFill, BodyOutlinePen, Body);
        dc.DrawRoundedRectangle(BodyHighlight, null, new Rect(7,3,33,6), 5,5);
        // Rosy cheeks and expressive eyes make the icon read as a character.
        dc.PushOpacity(waiting || done ? .75 : .35);
        dc.DrawEllipse(Ink("#F9B6CB"),null,new Point(10,26),3.2,1.4);
        dc.DrawEllipse(Ink("#F9B6CB"),null,new Point(40,26),3.2,1.4);
        dc.Pop();

        double blink = t % 4.1 > 3.94 ? .55 : 1;
        if (done)
        {
            dc.DrawGeometry(null, SmilePen, SmileGeometry);
        }
        else
        {
            double lookX = State == "thinking" ? Math.Sin(t*1.1)*1.3 : State == "working" ? Math.Sin(t*2.7)*1.8 : Math.Sin(t*.5);
            double lookY = State == "thinking" ? -2 : waiting ? -1 : 0;
            double eyeHeight = waiting ? 4.3 : State == "working" ? 3 : 3.9;
            dc.DrawEllipse(Ink("#253149"),null,new Point(16+lookX,21+lookY),2.5,eyeHeight*blink);
            dc.DrawEllipse(Ink("#253149"),null,new Point(34+lookX,21+lookY),2.5,eyeHeight*blink);
            if (blink > .7)
            {
                dc.DrawEllipse(Ink("#F9FCFF"),null,new Point(16+lookX-.5,19+lookY),.7,.9);
                dc.DrawEllipse(Ink("#F9FCFF"),null,new Point(34+lookX-.5,19+lookY),.7,.9);
            }
            if (waiting) dc.DrawEllipse(Ink("#435370"),null,new Point(25,28),1.8,2);
            else Stroke(dc,Ink("#53647F"),1,new Point(22,28),new Point(25,29),new Point(28,28));
        }
        dc.Pop();
        dc.Pop();

        if (active) DrawThought(dc, t, State == "working");
        else if (waiting) DrawQuestion(dc,t,State == "prompt");
        else if (done) DrawSuccess(dc,t,age,accent);
        dc.Pop();
        dc.Pop();

        if (expansion > .01)
        {
            dc.PushOpacity(expansion*Math.Min(1,.45+age*2));
            string name = Agent switch { "codex" => "Codex", "claude" => "Claude",
                "cursor" => "Cursor", "antigravity" => "Gemini", "opencode" => "OpenCode",
                _ => "Buddy" };
            string verb = State switch { "thinking" => "düşünüyor", "working" => "çalışıyor",
                "needsyou" => "sana soruyor", "prompt" => "onay bekliyor",
                "finished" => "tamamladı", _ => "hazır" };
            Label(dc,name+" "+verb,94,23,13,Ink("#EEF3FF"),true,width-110);
            string subtitle = State switch {
                "thinking" => "Bir fikrim var…",
                "working" => "İşleri yoluna koyuyorum",
                "needsyou" => "Fikrini duymak istiyorum",
                "prompt" => "Devam edebilir miyim?",
                "finished" => "Hazır! Birlikte başardık.",
                _ => "Buradayım, başlayabiliriz"
            };
            if (!string.IsNullOrWhiteSpace(Project))
            {
                double projectWidth = Math.Min(205, Math.Max(0, width-205));
                double used = Label(dc,Project,94,44,11,Ink("#DDE7FC"),true,projectWidth);
                Label(dc,"· " + subtitle,94+used+9,44,10.5,Ink("#9CA9C2"),false,width-110-used-9);
            }
            else Label(dc,subtitle,94,44,10.5,Ink("#9CA9C2"),false,width-110);
            dc.Pop();
        }
    }

    private void DrawSky(DrawingContext dc, double width, double height, Point center, Brush accent, double t, bool active)
    {
        // No inset card: depth comes from two very low-alpha washes rather than a
        // border. A bloom gathers behind the character; a cool wash pools at the
        // crown so the top curve catches light the way the rim hairline implies.
        var bloom = new RadialGradientBrush(
            Color.FromArgb(30,108,138,220),Colors.Transparent)
        { Center = new Point(center.X/width,.55), GradientOrigin = new Point(center.X/width,.55), RadiusX = .7, RadiusY = 1.35 };
        bloom.Freeze();
        dc.DrawRectangle(bloom,null,new Rect(0,0,width,height));

        dc.DrawRectangle(CrownBrush,null,new Rect(0,0,width,height));

        // Fine stars of three sizes and a never-quite-dark twinkle; they drift a
        // touch faster while an agent is active so the field feels alive.
        int count = IsExpanded ? 42 : 30;
        for (int i = 0; i < count; i++)
        {
            double x = (i*53.1+t*(active ? 3.1 : 1.2))%width;
            double y = 3+(i*23.3%(height-6))+Math.Sin(t*.5+i)*.6;
            double intensity = .32+.4*(.5+.5*Math.Sin(t*.7+i*2.1));
            double radius = i%7 == 0 ? .95 : i%3 == 0 ? .72 : .55;
            dc.PushOpacity(intensity);
            var ink = i%5 == 0 ? accent : Ink("#C8D7F1");
            dc.DrawEllipse(ink,null,new Point(x,y),radius,radius);
            // One rare four-point glint instead of a crowd of crosses.
            if (i%13 == 0)
            {
                double arm = 1.5+.5*(.5+.5*Math.Sin(t+i));
                var pen = i%5 == 0 ? Freeze(new Pen(ink,.5)) : StarGlintPen;
                dc.DrawLine(pen,new Point(x-arm,y),new Point(x+arm,y));
                dc.DrawLine(pen,new Point(x,y-arm*.7),new Point(x,y+arm*.7));
            }
            dc.Pop();
        }
        // An occasional short shooting star fades fully between passes.
        double pass = t%11;
        if (pass < 1.1)
        {
            double x = width*.25+pass*width*.32, y = 8+pass*5;
            dc.PushOpacity(Math.Sin(pass/1.1*Math.PI)*.32);
            dc.DrawLine(Freeze(new Pen(accent,.8)),new Point(x,y),new Point(x-10,y-2));
            dc.Pop();
        }
    }
    private void DrawAura(DrawingContext dc, Brush accent, double t, double age)
    {
        if (State == "working")
        {
            // Orbiting sparks run behind the character instead of a progress bar.
            for (int i = 0; i < 3; i++)
            {
                double angle=t*2.6+i*Math.PI*2/3;
                dc.PushOpacity(.45+.3*Math.Sin(angle));
                dc.DrawEllipse(accent,null,new Point(Math.Cos(angle)*32,Math.Sin(angle)*20),1.4,1.4);
                dc.Pop();
            }
        }
        if (State is "needsyou" or "prompt")
        {
            double pulse=(t%2)/2;
            dc.PushOpacity((1-pulse)*.3);
            dc.DrawEllipse(null,new Pen(accent,1),new Point(0,0),27+pulse*6,20+pulse*4);
            dc.Pop();
        }
        if (age < .8)
        {
            dc.PushOpacity((1-age/.8)*.2);
            dc.DrawEllipse(null,new Pen(accent,1),new Point(0,0),25+age*16,19+age*10);
            dc.Pop();
        }
    }
    private void DrawThought(DrawingContext dc, double t, bool executing)
    {
        var blue=Ink("#458BFF");
        double y=-19+Math.Sin(t*2.5)*1.2;
        dc.DrawEllipse(blue,null,new Point(-23,y),10.5,8.5);
        if (executing)
        {
            // A moving three-spoke work mark remains legible in the closed notch.
            for (int i=0;i<3;i++)
            {
                double angle=t*3.2+i*Math.PI*2/3;
                double x=-23+Math.Cos(angle)*4.5, yy=y+Math.Sin(angle)*4.5;
                dc.DrawEllipse(Ink("#FAFCFF"),null,new Point(x,yy),1.35,1.35);
            }
            dc.DrawEllipse(Ink("#D9EEFF"),null,new Point(-23,y),1.2,1.2);
            return;
        }
        dc.DrawEllipse(blue,null,new Point(-18,y+8),2.2,2.2);
        dc.DrawEllipse(blue,null,new Point(-15,y+11),1.1,1.1);
        for (int i=0;i<3;i++)
        {
            double wave=Math.Max(0,Math.Sin(t*(executing ? 6 : 4.5)-i*1.1));
            dc.PushOpacity(.5+.5*wave);
            dc.DrawEllipse(Ink("#FAFCFF"),null,new Point(-27+i*4,y-wave*1.7),1.2,1.2);
            dc.Pop();
        }
    }
    private void DrawQuestion(DrawingContext dc,double t,bool approval)
    {
        double lift=Math.Max(0,Math.Sin(t*2.3))*2;
        var fill=approval ? Ink("#DA9BFC") : Ink("#FFB876");
        dc.DrawEllipse(fill,null,new Point(-22,-20-lift),10,9);
        dc.DrawEllipse(fill,null,new Point(-16,-12-lift),2,2);
        Label(dc,approval ? "!" : "?",-25.5,-28-lift,13,Ink("#362639"),true,13);
    }
    private void DrawSuccess(DrawingContext dc,double t,double age,Brush accent)
    {
        dc.DrawEllipse(accent,null,new Point(-22,-20),9,9);
        Stroke(dc,Ink("#173F32"),1.7,new Point(-26,-20),new Point(-23,-17),new Point(-18,-23));
        // Celebrate once per completion; then settle into the smiling done pose.
        if (age > 3) return;
        for (int i=0;i<12;i++)
        {
            double angle=i*Math.PI*2/12;
            double distance=19+age*14;
            double x=Math.Cos(angle)*distance;
            double y=Math.Sin(angle)*distance*.65+age*age*3;
            dc.PushOpacity(Math.Max(0,1-age/3)*.8);
            var color=i%3==0 ? accent : i%3==1 ? Ink("#F4BDD9") : Ink("#B7B0FF");
            dc.DrawEllipse(color,null,new Point(x,y),i%2==0 ? 1.4 : .9,1);
            dc.Pop();
        }
    }
    private double Label(DrawingContext dc,string value,double x,double y,double size,Brush brush,bool bold,double maxWidth)
    {
        if (maxWidth < 1) return 0;
        var text=new FormattedText(value,TrCulture,FlowDirection.LeftToRight,
            bold ? SegoeSemiBold : SegoeRegular,
            size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth=maxWidth,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis };
        dc.DrawText(text,new Point(x,y));
        return Math.Min(text.Width, maxWidth);
    }
}
