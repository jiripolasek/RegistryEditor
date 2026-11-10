// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure.Interop;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Foundation;
using Windows.Graphics.Printing;

namespace JPSoftworks.Regedit.Printing;

internal sealed class RegistryPrintDocument(string title, string report)
{
    private readonly PrintDocument _document = new();
    private readonly List<UIElement> _pages = [];
    private readonly HashSet<int> _pendingPreviews = [];
    private readonly RegistryPrintLayoutGeneration _layoutGeneration = new();

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread() ??
                                                   throw new InvalidOperationException(
                                                       "A UI dispatcher is required for print preview.");

    private CancellationToken _token;
    private Action<Exception>? _fail;
    private PrintPageDescription _description;
    private bool _hasLayout;
    private bool _paginationActive;
    private bool _layoutComplete;
    private int _offset;

    internal async Task ShowAsync(nint window, CancellationToken token)
    {
        this._token = token;
        var source = this._document.DocumentSource;
        this._document.Paginate += this.PaginateReport;
        this._document.GetPreviewPage += this.GetPreviewPage;
        this._document.AddPages += this.AddPages;

        var finished
            = new TaskCompletionSource<PrintTaskCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._fail = exception => finished.TrySetException(exception);
        PrintTask? activeTask = null;
        TypedEventHandler<PrintTask, PrintTaskCompletedEventArgs>? completed = null;

        void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            try
            {
                activeTask = args.Request.CreatePrintTask(title, request => request.SetSource(source));
                completed = (_, done) => finished.TrySetResult(done.Completion);
                activeTask.Completed += completed;
            }
            catch (Exception ex)
            {
                finished.TrySetException(ex);
            }
        }

        var manager = PrintManagerInterop.GetForWindow(window);
        manager.PrintTaskRequested += PrintTaskRequested;
        try
        {
            if (!await PrintManagerWindowInterop.ShowAsync(window))
            {
                throw new InvalidOperationException("Windows could not open print preview.");
            }

            if (await finished.Task.WaitAsync(token) == PrintTaskCompletion.Failed)
            {
                throw new InvalidOperationException("Windows could not complete the print job.");
            }
        }
        finally
        {
            this._layoutGeneration.Cancel();
            this._paginationActive = false;
            this._fail = null;
            manager.PrintTaskRequested -= PrintTaskRequested;
            if (activeTask is not null && completed is not null)
            {
                activeTask.Completed -= completed;
            }

            this._document.Paginate -= this.PaginateReport;
            this._document.GetPreviewPage -= this.GetPreviewPage;
            this._document.AddPages -= this.AddPages;
            this._pages.Clear();
            this._pendingPreviews.Clear();
        }
    }

    private void PaginateReport(object sender, PaginateEventArgs args)
    {
        var description = args.PrintTaskOptions.GetPageDescription(0);
        if (this._hasLayout && SamePage(this._description, description))
        {
            if (this._layoutComplete)
            {
                this._document.SetPreviewPageCount(this._pages.Count, PreviewPageCountType.Final);
            }

            return;
        }

        this.BeginLayout(description);
        this.QueueNextPage(this._layoutGeneration.Current);
    }

    private void BeginLayout(PrintPageDescription description)
    {
        this._description = description;
        this._hasLayout = true;
        this._layoutGeneration.Begin();
        this._pages.Clear();
        this._pendingPreviews.Clear();
        this._offset = 0;
        this._paginationActive = report.Length > 0;
        this._layoutComplete = !this._paginationActive;
        if (!this._paginationActive)
        {
            this._document.SetPreviewPageCount(0, PreviewPageCountType.Final);
        }
    }

    private void GetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        var index = args.PageNumber - 1;
        if ((uint)index < (uint)this._pages.Count)
        {
            this._document.SetPreviewPage(args.PageNumber, this._pages[index]);
        }
        else if (this._paginationActive)
        {
            this._pendingPreviews.Add(args.PageNumber);
        }
    }

    private void AddPages(object sender, AddPagesEventArgs args)
    {
        // AddPages has no deferral. Finish the current layout here so the platform always gets
        // the complete page collection before AddPagesComplete.
        var description = args.PrintTaskOptions.GetPageDescription(0);
        if (!this._hasLayout || !SamePage(this._description, description))
        {
            this.BeginLayout(description);
        }

        var generation = this._layoutGeneration.Current;
        try
        {
            var complete = this._layoutGeneration.CompleteSynchronously(
                generation, () => this._paginationActive && !this._token.IsCancellationRequested,
                () => this.BuildNextPage(generation));
            if (complete && !this._token.IsCancellationRequested)
            {
                foreach (var page in this._pages)
                {
                    this._document.AddPage(page);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The preview was cancelled while the system was requesting its final page collection.
        }
        catch (Exception exception)
        {
            this._pages.Clear();
            this._paginationActive = false;
            this._fail?.Invoke(exception);
        }
        finally
        {
            this._document.AddPagesComplete();
        }
    }

    private void QueueNextPage(long generation)
    {
        if (!this.IsCurrent(generation) || !this._paginationActive)
        {
            return;
        }

        if (!this._dispatcher.TryEnqueue(() =>
            {
                try
                {
                    this.BuildNextPage(generation);
                    if (this._paginationActive)
                    {
                        this.QueueNextPage(generation);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (this.IsCurrent(generation))
                    {
                        this._paginationActive = false;
                    }
                }
                catch (Exception exception)
                {
                    if (this.IsCurrent(generation))
                    {
                        this._pages.Clear();
                        this._paginationActive = false;
                        this._fail?.Invoke(exception);
                    }
                }
            }))
        {
            this._pages.Clear();
            this._paginationActive = false;
            this._fail?.Invoke(new InvalidOperationException("The print preview dispatcher is unavailable."));
        }
    }

    private void BuildNextPage(long generation)
    {
        this._token.ThrowIfCancellationRequested();
        if (!this.IsCurrent(generation) || !this._paginationActive)
        {
            return;
        }

        var description = this._description;
        var left = Math.Max(48, description.ImageableRect.Left);
        var top = Math.Max(48, description.ImageableRect.Top);
        var width = Math.Max(80, Math.Min(description.PageSize.Width - 2 * left, description.ImageableRect.Width));
        var height = Math.Max(
            80, Math.Min(description.PageSize.Height - 2 * top, description.ImageableRect.Height) - 72);
        var body = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Width = width,
            Foreground = new SolidColorBrush(Colors.Black)
        };

        bool Fits(int start, int length)
        {
            this._token.ThrowIfCancellationRequested();
            body.Text = report.Substring(start, length);
            body.Measure(new Size(width, double.PositiveInfinity));
            return body.DesiredSize.Height <= height;
        }

        var slice = RegistryPrintPagination.FindNextSlice(report, this._offset, Fits, this._token);
        body.Text = report.Substring(slice.Start, slice.Length);
        this._offset += slice.Length;

        var content = new StackPanel
        {
            Spacing = 12,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0)
        };
        content.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Colors.Black)
            });
        content.Children.Add(body);
        content.Children.Add(
            new TextBlock
            {
                Text = $"Registry Editor · Page {this._pages.Count + 1}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.Black)
            });
        var page = new Grid
        {
            Width = description.PageSize.Width,
            Height = description.PageSize.Height,
            Background = new SolidColorBrush(Colors.White),
            Children = { content }
        };
        page.Measure(description.PageSize);
        page.Arrange(new Rect(0, 0, description.PageSize.Width, description.PageSize.Height));
        this._pages.Add(page);
        this._document.SetPreviewPageCount(this._pages.Count, PreviewPageCountType.Intermediate);
        this.CompletePendingPreviews();
        if (this._offset >= report.Length)
        {
            this._paginationActive = false;
            this._layoutComplete = true;
            this._document.SetPreviewPageCount(this._pages.Count, PreviewPageCountType.Final);
            this._pendingPreviews.RemoveWhere(number => number > this._pages.Count);
        }
    }

    private void CompletePendingPreviews()
    {
        foreach (var pageNumber in this._pendingPreviews.Where(number => number <= this._pages.Count).ToArray())
        {
            this._document.SetPreviewPage(pageNumber, this._pages[pageNumber - 1]);
            this._pendingPreviews.Remove(pageNumber);
        }
    }

    private bool IsCurrent(long generation)
    {
        return this._layoutGeneration.IsCurrent(generation);
    }

    private static bool SamePage(PrintPageDescription left, PrintPageDescription right)
    {
        return left.PageSize.Width == right.PageSize.Width && left.PageSize.Height == right.PageSize.Height &&
               left.ImageableRect.Left == right.ImageableRect.Left &&
               left.ImageableRect.Top == right.ImageableRect.Top &&
               left.ImageableRect.Width == right.ImageableRect.Width &&
               left.ImageableRect.Height == right.ImageableRect.Height;
    }
}
