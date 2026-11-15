using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// A 42 x 19 mark placed after the column name (and its plan chip) in the usage panel's title row. The upstream build
// only writes two lines of small text about off-peak hours, and only for a "账户余额" balance column with custom
// periods. This badge is present on every column, carries a 24-hour colour band, the current multiplier and a live
// countdown, and opens a full peak/valley card on click. Apache-2.0 fork of MoFeng2223/BrimDeck v1.3.1.

internal sealed class PeakValleySchedule
{
    public readonly record struct Segment(double From, double To, CreditPeriod Period);

    private readonly List<Segment> _weekday;
    private readonly List<Segment> _weekend;

    public string Source { get; }
    public bool ShowRate { get; }
    public bool IsExample { get; }
    private double BusyRate { get; }
    private double NormalRate { get; }
    private double IdleRate { get; }

    private PeakValleySchedule(string source, bool showRate, bool isExample,
        double busy, double normal, double idle, List<Segment> weekday, List<Segment> weekend)
    {
        Source = source; ShowRate = showRate; IsExample = isExample;
        BusyRate = busy; NormalRate = normal; IdleRate = idle;
        _weekday = weekday; _weekend = weekend;
    }

    public double Rate(CreditPeriod period) => period switch
    {
        CreditPeriod.Busy => BusyRate,
        CreditPeriod.Normal => NormalRate,
        _ => IdleRate
    };

    private List<Segment> Segments(bool weekend) => weekend ? _weekend : _weekday;
    public IReadOnlyList<Segment> SegmentsFor(bool weekend) => Segments(weekend);

    public (CreditPeriod Period, double Rate) At(DateTimeOffset time)
    {
        double hour = time.TimeOfDay.TotalHours;
        bool weekend = time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        foreach (var segment in Segments(weekend))
            if (hour >= segment.From && hour < segment.To) return (segment.Period, Rate(segment.Period));
        return (CreditPeriod.Idle, IdleRate);
    }

    // The first whole-minute boundary at which the period actually flips. Boundaries are taken from the segments
    // themselves, so a boundary hidden inside another period does not look like a change.
    public DateTimeOffset? NextChange(DateTimeOffset time)
    {
        var current = At(time).Period;
        var found = new List<DateTimeOffset>();
        for (int day = 0; day <= 8; day++)
        {
            var date = time.Date.AddDays(day);
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            foreach (var segment in Segments(weekend))
                foreach (double hour in new[] { segment.From, segment.To })
                {
                    if (hour is < 0 or > 24) continue;
                    var at = new DateTimeOffset(date.AddHours(hour), time.Offset);
                    if (at > time && At(at).Period != current) found.Add(at);
                }
        }
        return found.Count > 0 ? found.Min() : null;
    }

    private static List<Segment> Fill(List<Segment> covered, CreditPeriod rest)
    {
        var result = new List<Segment>();
        double cursor = 0;
        foreach (var segment in covered.OrderBy(s => s.From))
        {
            if (segment.From > cursor) result.Add(new Segment(cursor, segment.From, rest));
            result.Add(segment);
            cursor = Math.Max(cursor, segment.To);
        }
        if (cursor < 24) result.Add(new Segment(cursor, 24, rest));
        return result.Count > 0 ? result : [new Segment(0, 24, rest)];
    }

    // The service's own busy / normal / idle definition and coefficients, when the column reports one.
    public static PeakValleySchedule? FromCreditHours(CreditHours hours)
    {
        if (hours is null) return null;
        double Rate(CreditPeriod period) => hours.Rates.TryGetValue(period, out var value) ? value : 1;
        List<Segment> Build(bool weekend)
        {
            var list = new List<Segment>();
            foreach (var period in new[] { CreditPeriod.Busy, CreditPeriod.Normal })
                foreach (var range in hours.Ranges(period, weekend))
                    list.Add(new Segment(range.From, range.To, period));
            return Fill(list, CreditPeriod.Idle);
        }
        var weekday = Build(false);
        if (weekday.All(s => s.Period == CreditPeriod.Idle)) return null;
        return new PeakValleySchedule("服务商峰谷时段 · CodeAgentUsage.cs", true, false,
            Rate(CreditPeriod.Busy), Rate(CreditPeriod.Normal), Rate(CreditPeriod.Idle), weekday, Build(true));
    }

    // The user's own off-peak periods (Settings.cs). These colour a quota; they do not state a price, so no rate is shown.
    public static PeakValleySchedule FromOffPeak(AppEntry entry)
    {
        var idle = new List<Segment>();
        foreach (var period in entry.OffPeak)
        {
            double start = period.Start / 60.0, end = period.End / 60.0;
            if (Math.Abs(start - end) < 1e-6) { idle.Add(new Segment(0, 24, CreditPeriod.Idle)); continue; }
            if (start < end) idle.Add(new Segment(start, end, CreditPeriod.Idle));
            else { idle.Add(new Segment(0, end, CreditPeriod.Idle)); idle.Add(new Segment(start, 24, CreditPeriod.Idle)); }
        }
        var day = Fill(idle, CreditPeriod.Busy);
        return new PeakValleySchedule("自定义低峰时段 · Settings.cs", false, false, 1, 1, 1, day, day);
    }

    // Nothing configured: an illustrative schedule so the badge is useful right after installing. It is labelled as an
    // example in the tooltip and in the expanded card, and is never used for any real accounting.
    public static PeakValleySchedule BuiltIn()
    {
        var weekday = new List<Segment>
        {
            new(0, 9, CreditPeriod.Idle), new(9, 12, CreditPeriod.Busy), new(12, 14, CreditPeriod.Idle),
            new(14, 18, CreditPeriod.Busy), new(18, 24, CreditPeriod.Idle)
        };
        var weekend = new List<Segment> { new(0, 24, CreditPeriod.Idle) };
        return new PeakValleySchedule("内置示例时段（非官方计费规则）", true, true, 2, 1, 0.5, weekday, weekend);
    }

    public static PeakValleySchedule Resolve(AppEntry entry, IEnumerable<UsageMetric>? quotas)
    {
        var hours = quotas?.FirstOrDefault(q => q.Hours is not null)?.Hours;
        if (FromCreditHours(hours!) is { } fromService) return fromService;
        if (entry is not null && entry.OffPeak is { Count: > 0 }) return FromOffPeak(entry);
        return BuiltIn();
    }
}

internal sealed class PeakValleyBadge : Border
{
    private const double W = 42, H = 19;
    private const double BandX = 3.5, BandY = 3.3, BandW = 35, BandH = 2.6;

    private static readonly Color BusyColor = Hex("#E59A63");
    private static readonly Color NormalColor = Hex("#6F83AB");
    private static readonly Color IdleColor = Hex("#3AD0A6");
    private static readonly Brush Busy = Frozen(BusyColor), Normal = Frozen(NormalColor), Idle = Frozen(IdleColor);
    private static readonly Brush White = Brushes.White;
    private static readonly Brush Muted = Frozen("#9AA8C4");
    private static readonly Brush Faint = Frozen("#6C7C96");
    private static readonly Brush CardFill = Frozen("#111827");
    private static readonly Brush CardLine = Frozen("#34425C");
    private static readonly Brush TextMain = Frozen("#EEF3FB");

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Brush Frozen(string hex) => Frozen(Hex(hex));
    private static Brush BrushFor(CreditPeriod period) => period switch
    {
        CreditPeriod.Busy => Busy,
        CreditPeriod.Normal => Normal,
        _ => Idle
    };
    private static Color ColorFor(CreditPeriod period) => period switch
    {
        CreditPeriod.Busy => BusyColor,
        CreditPeriod.Normal => NormalColor,
        _ => IdleColor
    };
    private static string PeriodName(CreditPeriod period) => period switch
    {
        CreditPeriod.Busy => Loc.T("高峰", "Peak"),
        CreditPeriod.Normal => Loc.T("平时", "Normal"),
        _ => Loc.T("低谷", "Valley")
    };

    private readonly PeakValleySchedule _schedule;
    private readonly Canvas _canvas = new() { Width = W, Height = H, ClipToBounds = true };
    private readonly TextBlock _label = new()
    {
        FontSize = 8.1, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.None,
        FontFamily = new FontFamily("Microsoft YaHei, Segoe UI")
    };
    private readonly TextBlock _tipState = Tip(null), _tipCountdown = Tip(null), _tipNext = Tip(null), _tipHint = Tip(Faint);
    private readonly TextBlock _cardClock = Tip(Muted), _cardSource = Tip(Faint);
    private readonly TextBlock _cardState = Tip(TextMain), _cardSub = Tip(Muted);
    private readonly TextBlock _cardCountdown = Tip(TextMain), _cardNext = Tip(TextMain);
    private readonly TextBlock _cardFoot = Tip(Muted);
    private readonly Canvas _axis = new() { Width = 392, Height = 52, ClipToBounds = true };
    private readonly ToolTip _tooltip = new() { Placement = PlacementMode.Bottom, VerticalOffset = 4 };
    private readonly Popup _popup;
    private readonly DispatcherTimer _timer;

    private static TextBlock Tip(Brush? brush) => new()
    {
        FontSize = 10.5, Foreground = brush ?? TextMain, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Microsoft YaHei, Segoe UI")
    };

    public PeakValleyBadge(PeakValleySchedule schedule)
    {
        _schedule = schedule;
        Width = W; Height = H;
        CornerRadius = new CornerRadius(4); BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(6, 0, 0, 0);
        Cursor = Cursors.Hand;
        Child = _canvas;

        var tipPanel = new StackPanel { Margin = new Thickness(9, 7, 11, 8) };
        tipPanel.Children.Add(_tipState); tipPanel.Children.Add(_tipCountdown);
        tipPanel.Children.Add(_tipNext); tipPanel.Children.Add(_tipHint);
        _tooltip.Content = tipPanel;
        _tipHint.Text = Loc.T("点击展开完整峰谷卡片", "Click to open the full peak/valley card");
        ToolTip = _tooltip;

        _popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, VerticalOffset = 5, Child = BuildCard()
        };
        MouseLeftButtonUp += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        Unloaded += (_, _) => { _timer?.Stop(); _popup.IsOpen = false; };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render();
        Render();
        _timer.Start();
    }

    private Border BuildCard()
    {
        var root = new Border
        {
            Width = 420, Background = CardFill, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 11, 14, 12)
        };
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_cardClock, 0); Grid.SetColumn(_cardSource, 1);
        _cardSource.HorizontalAlignment = HorizontalAlignment.Right; _cardSource.FontSize = 9.5;
        top.Children.Add(_cardClock); top.Children.Add(_cardSource);
        stack.Children.Add(top);

        var main = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        main.ColumnDefinitions.Add(new ColumnDefinition());
        main.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        _cardState.FontSize = 27; _cardState.FontWeight = FontWeights.Bold;
        left.Children.Add(_cardState); left.Children.Add(_cardSub);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _cardCountdown.FontSize = 25; _cardCountdown.FontWeight = FontWeights.Bold;
        _cardCountdown.HorizontalAlignment = HorizontalAlignment.Right;
        _cardNext.HorizontalAlignment = HorizontalAlignment.Right; _cardNext.FontSize = 10.5;
        right.Children.Add(_cardCountdown); right.Children.Add(_cardNext);
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 1);
        main.Children.Add(left); main.Children.Add(right);
        stack.Children.Add(main);

        stack.Children.Add(_axis);
        _cardFoot.FontSize = 10; stack.Children.Add(_cardFoot);
        root.Child = stack;
        return root;
    }

    private static string Pad(int n) => n < 10 ? "0" + n : n.ToString(CultureInfo.InvariantCulture);
    private static string Hms(TimeSpan t)
    {
        long seconds = (long)Math.Max(0, t.TotalSeconds);
        return Pad((int)(seconds / 3600)) + ":" + Pad((int)(seconds % 3600 / 60)) + ":" + Pad((int)(seconds % 60));
    }
    private static string Hm(double hour) => Pad((int)Math.Floor(hour)) + ":" + Pad((int)Math.Round((hour % 1) * 60));
    private static string DayLabel(DateTimeOffset now, DateTimeOffset next)
    {
        if (next.Date == now.Date) return Loc.T("今天 ", "Today ");
        if (next.Date == now.Date.AddDays(1)) return Loc.T("明天 ", "Tomorrow ");
        return next.ToString("MM-dd ");
    }
    private static string RateText(bool show, double rate) =>
        show ? " ×" + rate.ToString("0.#", CultureInfo.InvariantCulture) : "";

    private CreditPeriod PeriodAt(double hour, bool weekend)
    {
        foreach (var segment in _schedule.SegmentsFor(weekend))
            if (hour >= segment.From && hour < segment.To) return segment.Period;
        return CreditPeriod.Idle;
    }

    private void DrawBand(DateTimeOffset now)
    {
        double hour = now.TimeOfDay.TotalHours;
        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        foreach (var segment in _schedule.SegmentsFor(weekend))
        {
            var rect = new Rectangle
            {
                Width = Math.Max(0.6, (segment.To - segment.From) / 24 * BandW),
                Height = BandH, Fill = BrushFor(segment.Period), Opacity = 0.92
            };
            Canvas.SetLeft(rect, BandX + segment.From / 24 * BandW);
            Canvas.SetTop(rect, BandY);
            _canvas.Children.Add(rect);
        }
        var pointer = new Rectangle { Width = 1.3, Height = BandH + 2.4, Fill = White };
        Canvas.SetLeft(pointer, BandX + hour / 24 * BandW - 0.65);
        Canvas.SetTop(pointer, BandY - 1.2);
        _canvas.Children.Add(pointer);
    }

    private void DrawAxis(DateTimeOffset now)
    {
        _axis.Children.Clear();
        double x0 = 6, total = 380, gap = 3, cw = (total - gap * 23) / 24, y = 20, h = 13;
        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        for (int i = 0; i < 24; i++)
        {
            var period = PeriodAt(i + 0.5, weekend);
            bool isNow = (int)now.TimeOfDay.TotalHours == i;
            var rect = new Rectangle
            {
                Width = cw, Height = h, RadiusX = 2, RadiusY = 2,
                Fill = BrushFor(period), Opacity = isNow ? 1 : 0.78
            };
            Canvas.SetLeft(rect, x0 + i * (cw + gap)); Canvas.SetTop(rect, y);
            _axis.Children.Add(rect);
            if (isNow)
            {
                var frame = new Rectangle
                {
                    Width = cw + 2, Height = h + 2, RadiusX = 3, RadiusY = 3,
                    Fill = Brushes.Transparent, Stroke = White, StrokeThickness = 1.2
                };
                Canvas.SetLeft(frame, x0 + i * (cw + gap) - 1); Canvas.SetTop(frame, y - 1);
                _axis.Children.Add(frame);
            }
        }
        double cx = x0 + now.TimeOfDay.TotalHours / 24 * (total + gap);
        var arrow = new Polygon { Fill = White, Points = new PointCollection { new Point(cx - 5, 6), new Point(cx + 5, 6), new Point(cx, 15) } };
        _axis.Children.Add(arrow);
        foreach (int t in new[] { 0, 6, 12, 18, 24 })
        {
            var text = new TextBlock { Text = Pad(t) + ":00", FontSize = 9, Foreground = Muted };
            double tx = x0 + t / 24.0 * total;
            Canvas.SetLeft(text, tx - (t == 24 ? 26 : t == 0 ? 0 : 13)); Canvas.SetTop(text, y + h + 3);
            _axis.Children.Add(text);
        }
    }

    private void Render()
    {
        var now = DateTimeOffset.Now;
        var (period, rate) = _schedule.At(now);
        var next = _schedule.NextChange(now);
        var brush = BrushFor(period);
        var tint = ColorFor(period);
        var background = new SolidColorBrush(Color.FromArgb(38, tint.R, tint.G, tint.B));
        background.Freeze();

        BorderBrush = brush;
        Background = background;
        _canvas.Children.Clear();
        DrawBand(now);
        _label.Text = PeriodName(period) + RateText(_schedule.ShowRate, rate);
        _label.Foreground = brush;
        Canvas.SetLeft(_label, 3.5); Canvas.SetTop(_label, 7.4);
        _canvas.Children.Add(_label);

        string countdown = next is { } at ? Hms(at - now) : "—";
        string nextText = next is { } change
            ? DayLabel(now, change) + Hm(change.TimeOfDay.TotalHours) + " " + Loc.T("转", "→") + PeriodName(_schedule.At(change).Period)
            : Loc.T("全天不变", "No change today");
        string state = PeriodName(period) + RateText(_schedule.ShowRate, rate);
        _tipState.Text = Loc.T("当前  ", "Now  ") + state;
        _tipState.Foreground = brush;
        _tipCountdown.Text = Loc.T("距切换  ", "Next in  ") + countdown;
        _tipNext.Text = Loc.T("下次    ", "Next    ") + nextText;
        _cardClock.Text = now.ToString("HH:mm");
        _cardSource.Text = _schedule.Source;
        _cardState.Text = state;
        _cardState.Foreground = brush;
        _cardSub.Text = (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Loc.T("周末", "Weekend") : Loc.T("工作日", "Weekday"))
            + (_schedule.IsExample ? Loc.T(" · 示例时段", " · example schedule") : "");
        _cardCountdown.Text = countdown;
        _cardNext.Text = nextText;
        _cardFoot.Text = "■ " + PeriodName(CreditPeriod.Busy) + RateText(_schedule.ShowRate, _schedule.Rate(CreditPeriod.Busy))
            + "   ■ " + PeriodName(CreditPeriod.Idle) + RateText(_schedule.ShowRate, _schedule.Rate(CreditPeriod.Idle));
        DrawAxis(now);
    }
}
