// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Resources;

public sealed partial class ToolRowResources : ResourceDictionary
{
    // WinUI cannot share one path geometry between multiple rendered icons.
    public static IconSource BinaryIcon => new PathIconSource
    {
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Application.Current.Resources["RegistryBinaryIconData"])
    };

    public ToolRowResources()
    {
        this.InitializeComponent();
    }

    private void NoteMatches_Changed(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var text = (TextBlock)sender;
        text.TextHighlighters.Clear();
        if (args.NewValue is not InvestigationRow { HasNoteSummary: true } row)
        {
            return;
        }

        var (positions, display) = (text.Tag as string) switch
        {
            "Title" => (row.NoteTitleMatches, row.Title),
            "Path" => (row.NotePathMatches, row.Path),
            _ => (row.NoteTextMatches, row.TableSummary)
        };
        if (positions is not { Count: > 0 })
        {
            return;
        }

        var highlight = new TextHighlighter();
        foreach (var index in positions.Where(position => position >= 0 && position < display.Length).Distinct().Order())
        {
            if (char.IsLowSurrogate(display[index]))
            {
                continue;
            }

            highlight.Ranges.Add(new TextRange
            {
                StartIndex = index,
                Length = char.IsHighSurrogate(display[index]) && index + 1 < display.Length ? 2 : 1
            });
        }

        if (highlight.Ranges.Count > 0)
        {
            text.TextHighlighters.Add(highlight);
        }
    }

    public static Style IconStyle(string key)
    {
        return (Style)Application.Current.Resources[key];
    }
}
