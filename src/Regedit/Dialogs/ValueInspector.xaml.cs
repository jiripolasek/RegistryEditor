// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Resources;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class ValueInspector : UserControl
{
    private readonly WindowDialogPresenter _dialogs;
    private Action<Func<Task>>? _requestByteDialog;

    internal ValueInspectorViewModel ViewModel { get; }

    internal InspectorNotesViewModel? Notes { get; set; }

    private bool HasDetails => this.ViewModel.HasKeyPath || this.ViewModel.HasText || this.ViewModel.HasStoredValue;

    internal ValueInspector(ValueInspectorViewModel viewModel, WindowDialogPresenter dialogs)
    {
        this.ViewModel = viewModel;
        this._dialogs = dialogs;
        this.InitializeComponent();
        this.Loaded += (_, _) => this.Notes?.Activate();
        this.Unloaded += (_, _) => this.Notes?.Deactivate();
        RevealOnHover(this.PrimaryNote, this.EditNote);
        RevealOnHover(this.KeyNotePreview, this.EditKeyNote);
        // x:Bind would share one geometry instance with the type icon.
        this.RawActionIcon.IconSource = ToolRowResources.BinaryIcon;
    }

    internal static async Task ShowDialogAsync(
        ValueInspectorViewModel viewModel,
        WindowDialogPresenter dialogs,
        Action<ValueInspector, AppDialog>? configure = null)
    {
        Func<Task>? openByteDialog;
        do
        {
            openByteDialog = null;
            var inspector = new ValueInspector(viewModel, dialogs);
            var dialog = new AppDialog("Value inspector", inspector, "Close", string.Empty);
            configure?.Invoke(inspector, dialog);
            inspector._requestByteDialog = open =>
            {
                openByteDialog = open;
                dialog.Hide();
            };
            await dialogs.ShowAsync(dialog);

            if (openByteDialog is not null)
            {
                await openByteDialog();
            }
        } while (openByteDialog is not null);
    }

    private async void OpenByteInterpreter_Click(object sender, RoutedEventArgs args)
    {
        await this.OpenByteDialogAsync(this.ShowInterpreterAsync);
    }

    private async void OpenRawBytes_Click(object sender, RoutedEventArgs args)
    {
        await this.OpenByteDialogAsync(this.ShowRawBytesAsync);
    }

    private async Task OpenByteDialogAsync(Func<Task> open)
    {
        if (this._requestByteDialog is { } request)
        {
            request(open);
        }
        else
        {
            await open();
        }
    }

    private Task ShowInterpreterAsync()
    {
        var view = new ByteInterpreter(this.ViewModel);
        var layout = new DialogLayout(
            "Interpret bytes", view, close: "Close", subject: this.ViewModel.Name, subtitle: this.ViewModel.Heading,
            defaultButton: ContentDialogButton.None)
        {
            OnOpened = view.FocusOffset
        };
        return this._dialogs.ShowWindowAsync(layout, new((int)HexView.DialogWidth + 64, 704));
    }

    private void MoreNotes_Click(object sender, RoutedEventArgs args)
    {
        if (this.Notes is not { } notes)
        {
            return;
        }

        var menu = new MenuFlyout();
        NoteDraft? selected = null;
        foreach (var note in notes.OtherMatches)
        {
            var item = new MenuFlyoutItem { Text = note.RuleSummary };
            ToolTipService.SetToolTip(item, note.Location.Address);
            item.Click += (_, _) => selected = note;
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            if (selected is { } note)
            {
                this.DispatcherQueue.TryEnqueue(() => notes.OpenMatchCommand.Execute(note));
            }
        };
        menu.ShowAt(this.MoreNotes);
    }

    internal static void HighlightBytes(ValueInspectorViewModel viewModel, HexView view)
    {
        if (double.IsFinite(viewModel.Offset) && double.IsFinite(viewModel.Length))
        {
            view.Highlight((long)viewModel.Offset, (long)viewModel.Length);
        }
    }

    private Task ShowRawBytesAsync()
    {
        var view = new HexView { Bytes = this.ViewModel.Bytes, MinWidth = HexView.DialogWidth };
        AutomationProperties.SetAutomationId(view, "ExpandedValueHexView");
        AutomationProperties.SetName(view, "Raw bytes");
        view.Loaded += (_, _) => HighlightBytes(this.ViewModel, view);
        var layout = new DialogLayout("Raw bytes", view, close: "Close", subject: this.ViewModel.Name, subtitle: this.ViewModel.Heading, fillBody: true);
        layout.EnableHorizontalScrolling();
        return this._dialogs.ShowWindowAsync(layout, new((int)HexView.DialogWidth + 16, 588));
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs args)
    {
        for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ContentDialog dialog)
            {
                dialog.Hide();
                break;
            }
        }
    }

    private void CopyReadout_Loaded(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        button.Loaded -= this.CopyReadout_Loaded;
        RevealOnHover((Grid)button.Parent, button);
    }

    private static void RevealOnHover(FrameworkElement area, Button button)
    {
        var pointerOver = false;

        void Refresh() => button.Opacity = pointerOver || button.FocusState == FocusState.Keyboard ? 1 : 0;

        area.PointerEntered += (_, _) =>
        {
            pointerOver = true;
            Refresh();
        };
        area.PointerExited += (_, _) =>
        {
            pointerOver = false;
            Refresh();
        };
        button.GotFocus += (_, _) => Refresh();
        button.LostFocus += (_, _) => Refresh();
    }

    private void CopyReadout_Click(object sender, RoutedEventArgs args)
    {
        ClipboardText.Set((string)((Button)sender).Tag);
    }
}
