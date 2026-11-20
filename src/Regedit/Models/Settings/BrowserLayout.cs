// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.Models.Settings;

public sealed record BrowserLayout(
    double NameWidth = 0,
    double TypeWidth = 170,
    double DataWidth = 0,
    double TreeWidth = 290,
    bool TreeHidden = false,
    string SortColumn = "Name",
    bool SortDescending = false,
    Dictionary<string, double>? ExtraColumnWidths = null)
{
    internal BrowserLayout Normalize()
    {
        return this with
        {
            NameWidth = Column(this.NameWidth, 0),
            TypeWidth = Column(this.TypeWidth, 170),
            DataWidth = Column(this.DataWidth, 0),
            TreeWidth = double.IsFinite(this.TreeWidth) ? Math.Clamp(this.TreeWidth, 160, 600) : 290,
            SortColumn = RegistryColumns.TryParse(this.SortColumn, out _) ? this.SortColumn : "Name"
        };
    }

    internal double Width(RegistrySortColumn column)
    {
        return column switch
        {
            RegistrySortColumn.Name => this.NameWidth,
            RegistrySortColumn.Type => this.TypeWidth,
            RegistrySortColumn.Data => this.DataWidth,
            _ => Column(
                this.ExtraColumnWidths?.GetValueOrDefault(column.ToString()) ?? 0, column switch
                {
                    RegistrySortColumn.LastWrite => 170,
                    RegistrySortColumn.Notes or RegistrySortColumn.LinkTarget => 220,
                    RegistrySortColumn.Children => 155,
                    _ => 110
                })
        };
    }

    internal BrowserLayout WithWidth(RegistrySortColumn column, double width)
    {
        return column switch
        {
            RegistrySortColumn.Name => this with { NameWidth = width },
            RegistrySortColumn.Type => this with { TypeWidth = width },
            RegistrySortColumn.Data => this with { DataWidth = width },
            _ => this with
            {
                ExtraColumnWidths = new Dictionary<string, double>(this.ExtraColumnWidths ?? []) { [column.ToString()] = width }
            }
        };
    }

    private static double Column(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? Math.Clamp(value, 80, 1600) : fallback;
    }
}
