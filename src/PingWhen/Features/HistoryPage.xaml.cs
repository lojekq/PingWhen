using System.Windows;
using System.Windows.Media;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>Time, outcome, title, duration, channels; filter by outcome, clear (spec §7, page 3).</summary>
public partial class HistoryPage
{
    sealed record Choice(Outcome? Value, string Text);

    public sealed record Row(HistoryEntry E)
    {
        public string Time => E.Ts.ToLocalTime() is var t && t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("dd.MM HH:mm");
        public string Outcome => Loc.T("Outcome." + E.Outcome);
        public string Title => E.Title;
        public string Body => E.Body;
        public string Duration => E.Seconds > 0 ? Fmt.Duration(TimeSpan.FromSeconds(E.Seconds)) : "";
        public string Channels => string.Join(", ", E.Channels.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => Loc.T("Channel." + c.Replace(":", "."))));
        public Brush Badge => E.Outcome switch
        {
            Features.Outcome.Done => Brushes.SeaGreen,
            Features.Outcome.Error => Brushes.IndianRed,
            Features.Outcome.Quiet => Brushes.SteelBlue,
            Features.Outcome.Lost => Brushes.DarkGoldenrod,
            Features.Outcome.Notify => Brushes.MediumPurple,
            _ => Brushes.Gray,
        };
    }

    public HistoryPage()
    {
        InitializeComponent();
        FilterBox.ItemsSource = new[] { new Choice(null, Loc.T("History.All")) }
            .Concat(Enum.GetValues<Outcome>().Select(o => new Choice(o, Loc.T("Outcome." + o)))).ToList();
        FilterBox.SelectedIndex = 0;
        FilterBox.SelectionChanged += (_, _) => Refresh();
        ClearButton.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show(Loc.T("History.Clear.Confirm"), AppInfo.DisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                Notifier.ClearHistory();
        };
        Notifier.History.CollectionChanged += OnChanged;
        Unloaded += (_, _) => Notifier.History.CollectionChanged -= OnChanged;
        Refresh();
    }

    void OnChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

    void Refresh()
    {
        var f = (FilterBox.SelectedItem as Choice)?.Value;
        var rows = Notifier.History.Where(h => f is null || h.Outcome == f).Select(h => new Row(h)).ToList();
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
