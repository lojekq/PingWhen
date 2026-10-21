using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using PingWhen.Shell;
using Wpf.Ui.Controls;

namespace PingWhen.Features;

/// <summary>One watcher card. Demo cards are not engine watchers: they keep their own live values.</summary>
public sealed class Card : INotifyPropertyChanged
{
    public Card(Watcher w)
    {
        W = w;
        Icon = w.ExePath is { } p && ExeIcon.Get(p) is { } img
            ? new ImageIcon { Source = img, Width = 24, Height = 24 }
            : new SymbolIcon(w.Kind switch { Kind.File => SymbolRegular.Document24, Kind.Window => SymbolRegular.Window24, Kind.Quiet => SymbolRegular.TopSpeed24, _ => SymbolRegular.Apps24 });
    }

    public Watcher W { get; private set; }
    public string Id => W.Id;
    public IconElement Icon { get; }
    public string Title => W.Title;
    public string Condition => Describe.Condition(W);
    public string Author => W.CreatedBy.StartsWith("mcp:") ? W.CreatedBy[4..].Replace("/", " · ") : "";
    public string Channels => Loc.T("Route." + W.Route) + (W.KeepAwake ? " · " + Loc.T("Watchers.Awake") : "");
    public bool IsDemo { get; init; }
    public DateTime Since { get; set; } = DateTime.UtcNow;

    string live = "";
    public string Live { get => live; set => Set(ref live, value); }
    double progress;
    public double Progress { get => progress; set { if (Set(ref progress, value)) Changed(nameof(ProgressVisibility)); } }
    public Visibility ProgressVisibility => progress > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Elapsed => Fmt.Duration(DateTime.UtcNow - Since);

    public void Tick() => Changed(nameof(Elapsed));

    public void Update(Watcher w)
    {
        W = w;
        Changed(nameof(Title));
        Changed(nameof(Condition));
        Changed(nameof(Channels));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
    bool Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(p);
        return true;
    }
}

/// <summary>The cards behind the Watchers page and the tray: engine snapshots merged in on the UI thread.</summary>
public static class Cards
{
    public static ObservableCollection<Card> Items { get; } = [];
    public static event Action? Changed;

    public static void Start()
    {
        Engine.Current.Changed += views => Application.Current?.Dispatcher.BeginInvoke(() => Sync(views));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { foreach (var c in Items) c.Tick(); };
        t.Start();
    }

    static void Sync(IReadOnlyList<WatcherView> views)
    {
        var ids = views.Select(v => v.W.Id).ToHashSet();
        foreach (var gone in Items.Where(c => !c.IsDemo && !ids.Contains(c.Id)).ToList()) Items.Remove(gone);
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i];
            var c = Items.FirstOrDefault(x => x.Id == v.W.Id);
            if (c is null)
            {
                c = new Card(v.W) { Since = v.W.Processes.Count > 0 && v.W.Kind == Kind.Process ? v.W.Processes.Min(p => p.Started) : v.W.CreatedAt };
                Items.Insert(Math.Min(i, Items.Count), c);
            }
            c.Live = v.Live;
            c.Progress = v.Progress;
        }
        Changed?.Invoke();
    }
}

public partial class WatchersPage
{
    public WatchersPage()
    {
        InitializeComponent();
        void Sync()
        {
            EmptyPanel.Visibility = Cards.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorBar.IsOpen = Notifier.DeliveryError is not null;
            ErrorBar.Message = Notifier.DeliveryError ?? "";
        }
        Cards.Items.CollectionChanged += (_, _) => Sync();
        Notifier.StateChanged += () => Dispatcher.BeginInvoke(Sync);
        Sync();
        NewButton.Click += (_, _) => NewWatcherPage.Open(null);
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Card c) return;
        if (c.IsDemo) Cards.Items.Remove(c);
        else _ = Engine.Current.Cancel(c.Id);
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Card c && !c.IsDemo) NewWatcherPage.Open(new() { Edit = c.W });
    }
}
