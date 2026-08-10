using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQAvatar.Spike.Launch;

public partial class LaunchMethodPicker : Window
{
    public string? SelectedId { get; private set; }

    public LaunchMethodPicker(string? current = null)
    {
        InitializeComponent();
        if (current != null)
            Subtitle.Text = $"Current: {LaunchMethods.ById(current)?.Title ?? current}.  Pick a different one, or Cancel to keep it.";
        foreach (LaunchMethodInfo m in LaunchMethods.All)
            CardHost.Children.Add(MakeCard(m, m.Id == current));
    }

    private static Brush Hex(string h) => (Brush)new BrushConverter().ConvertFromString(h)!;

    private Button MakeCard(LaunchMethodInfo m, bool isCurrent)
    {
        var stack = new StackPanel();
        stack.Children.Add(MakeDiagram(m.Id));

        stack.Children.Add(new TextBlock { Text = m.Title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Hex("#E6EDF3"), Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(new TextBlock { Text = m.Tagline, FontSize = 12, Foreground = Hex("#4FC3F7"), Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock { Text = m.Involves, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Hex("#9AA7B4") });

        var (bg, fg, label) = m.RiskLevel switch
        {
            LaunchRisk.None => ("#16321F", "#7CE38B", "Lowest risk"),
            LaunchRisk.Low => ("#33301A", "#FFCB6B", "Some risk / setup"),
            _ => ("#3A1A1E", "#FF6B6B", "HIGH risk — account ban possible"),
        };
        stack.Children.Add(new Border
        {
            Background = Hex(bg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 10, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = label, Foreground = Hex(fg), FontSize = 11, FontWeight = FontWeights.Bold }
        });
        stack.Children.Add(new TextBlock { Text = m.Risk, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Hex("#7E8A97") });

        var btn = new Button
        {
            Content = stack,
            Tag = m.Id,
            Margin = new Thickness(8),
            Padding = new Thickness(16),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Hex(isCurrent ? "#182634" : "#141C26"),
            Foreground = Hex("#E6EDF3"),
            BorderBrush = Hex(isCurrent ? "#4FC3F7" : "#2A4A57"),
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        btn.Click += (s, e) => { SelectedId = m.Id; DialogResult = true; Close(); };
        return btn;
    }

    // A small, distinct stylized diagram per method, drawn with primitive shapes on a Canvas.
    private static FrameworkElement MakeDiagram(string id)
    {
        var c = new Canvas { Width = 128, Height = 78 };
        Brush accent = Hex("#4FC3F7"), dim = Hex("#33455A"), red = Hex("#FF6B6B"), panel = Hex("#0C1119");

        void Rect(double x, double y, double w, double h, Brush stroke, double op = 1.0)
        {
            var r = new Rectangle { Width = w, Height = h, Stroke = stroke, StrokeThickness = 2, Fill = panel, RadiusX = 4, RadiusY = 4, Opacity = op };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
        }
        void Bar(double x, double y, double w, double h, Brush fill, double op)
        {
            var r = new Rectangle { Width = w, Height = h, Fill = fill, RadiusX = 4, RadiusY = 4, Opacity = op };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
        }

        switch (id)
        {
            case "Foreground":
                Rect(24, 12, 80, 54, accent);
                Bar(24, 12, 80, 12, accent, 0.5);
                c.Children.Add(new Polygon { Points = new PointCollection { new(62, 38), new(62, 58), new(67, 53), new(72, 62), new(75, 60), new(70, 51), new(78, 51) }, Fill = Hex("#E6EDF3") });
                break;

            case "IsolatedDesktop":
                Rect(8, 22, 76, 46, dim);      // your desktop
                Rect(42, 8, 76, 46, accent);   // the hidden second desktop
                break;

            case "Vm":
                Rect(6, 10, 116, 58, dim);     // host
                Rect(34, 24, 60, 34, accent);  // VM inside
                break;

            case "Injection":
                Rect(40, 14, 74, 50, accent);  // the game window
                c.Children.Add(new Line { X1 = 4, Y1 = 39, X2 = 44, Y2 = 39, Stroke = red, StrokeThickness = 3 });
                c.Children.Add(new Polygon { Points = new PointCollection { new(44, 33), new(54, 39), new(44, 45) }, Fill = red });
                break;
        }
        return new Viewbox { Height = 82, HorizontalAlignment = HorizontalAlignment.Left, Child = c };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
