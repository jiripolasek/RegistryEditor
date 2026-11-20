// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Import;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIEditor;

namespace JPSoftworks.Regedit.Views.Files;

public sealed partial class RegistryFilePreview
{
    private const int DiagnosticMarker = 24;
    private const int DiagnosticMargin = 3;

    private void InitializeDiagnostics()
    {
        var editor = this.SourceEditor.Editor;
        for (var margin = 0; margin < editor.Margins; margin++)
        {
            editor.SetMarginMaskN(margin, editor.GetMarginMaskN(margin) & ~(1 << DiagnosticMarker));
        }

        editor.SetMarginTypeN(DiagnosticMargin, MarginType.Symbol);
        editor.SetMarginMaskN(DiagnosticMargin, 1 << DiagnosticMarker);
        editor.SetMarginWidthN(DiagnosticMargin, 0);
        editor.SetMarginSensitiveN(DiagnosticMargin, true);
        editor.MarginClick += this.Source_MarginClick;
    }

    private void ApplyDiagnosticMarkerStyle()
    {
        if (this.ErrorStatusIcon.Foreground is SolidColorBrush brush)
        {
            var color = brush.Color;
            var nativeColor = color.R | color.G << 8 | color.B << 16;
            this.SourceEditor.Editor.MarkerDefine(DiagnosticMarker, MarkerSymbol.Circle);
            this.SourceEditor.Editor.MarkerSetFore(DiagnosticMarker, nativeColor);
            this.SourceEditor.Editor.MarkerSetBack(DiagnosticMarker, nativeColor);
        }
    }

    private void UpdateDiagnosticMarkers()
    {
        // WinUI's AOT collection projection needs a collection of objects at this boundary.
        this.Problems.ItemsSource = this.ViewModel.Diagnostics.Cast<object>().ToArray();
        var editor = this.SourceEditor.Editor;
        editor.MarkerDeleteAll(DiagnosticMarker);
        editor.SetMarginWidthN(DiagnosticMargin, this.ViewModel.HasDiagnostics ? 18 : 0);
        foreach (var diagnostic in this.ViewModel.Diagnostics)
        {
            editor.MarkerAdd(diagnostic.Line - 1, DiagnosticMarker);
        }
    }

    private void Source_MarginClick(Editor sender, MarginClickEventArgs args)
    {
        if (args.Margin == DiagnosticMargin && this.ViewModel is { } model)
        {
            var diagnostic = model.Diagnostics.FirstOrDefault(error => error.Line == sender.LineFromPosition(args.Position) + 1);
            if (model.GoToDiagnosticCommand.CanExecute(diagnostic))
            {
                model.GoToDiagnosticCommand.Execute(diagnostic);
            }
        }
    }

    private void ShowProblems_Click(object sender, RoutedEventArgs args)
    {
        AccessKeyManager.ExitDisplayMode();
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (this.ViewModel.CanNavigateDiagnostics && this.Problems.IsLoaded)
            {
                this.Problems.Focus(FocusState.Programmatic);
            }
        });
    }

    private void Problem_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is RegistryFileDiagnostic diagnostic && this.ViewModel.GoToDiagnosticCommand.CanExecute(diagnostic))
        {
            this.ViewModel.GoToDiagnosticCommand.Execute(diagnostic);
        }
    }
}
