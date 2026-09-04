using System.Collections.Specialized;
using Avalonia.Controls;
using OpenEC.Inspector.ViewModels;

namespace OpenEC.Inspector.Views;

public partial class EventsView : UserControl
{
    private EventsViewModel? _viewModel;

    public EventsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Rows.CollectionChanged -= OnRowsChanged;
            _viewModel = DataContext as EventsViewModel;
            if (_viewModel is not null) _viewModel.Rows.CollectionChanged += OnRowsChanged;
        };
        EventList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || e.Source is not ScrollViewer scroll) return;
        var atBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y < 0) _viewModel.AutoScroll = false;
        else if (atBottom) _viewModel.AutoScroll = true;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is { AutoScroll: true, Rows.Count: > 0 })
            EventList.ScrollIntoView(_viewModel.Rows[^1]);
    }
}
