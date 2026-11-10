// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.UI.Xaml;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    internal async Task ShowHistoryJournalAsync(Guid id)
    {
        await this.JournalHistory.RefreshAsync();
        this.ViewModel.OpenBottomTool(ToolKind.Journal);
        if (this.ViewModel.ActiveBottomTab is not { } tab || this.GetView(tab) is not ToolView view)
        {
            return;
        }

        if (view.IsLoaded)
        {
            view.JournalSurface!.NavigateCommand.Execute(id);
        }
        else
        {
            view.Loaded += Reveal;
        }

        void Reveal(object sender, RoutedEventArgs args)
        {
            view.Loaded -= Reveal;
            view.JournalSurface!.NavigateCommand.Execute(id);
        }
    }
}
