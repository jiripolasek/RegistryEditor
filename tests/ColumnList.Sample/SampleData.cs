// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;

namespace JPSoftworks.ColumnList.Sample;

public sealed class SampleEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Name)));
            }
        }
    }

    public required string Kind { get; init; }

    public required string Data { get; init; }

    public required DateTime LastWrite { get; init; }

    public required long Size { get; init; }

    public string Glyph => this.Kind == "Key" ? "" : "";

    public string LastWriteText => this.LastWrite.ToString("yyyy-MM-dd HH:mm");

    public string SizeText => this.Size.ToString("N0") + " B";

    public override string ToString()
    {
        return this.Name;
    }
}

public sealed class SampleEvent
{
    public required string Subject { get; init; }

    public required string Path { get; init; }

    public required string Summary { get; set; }

    public override string ToString()
    {
        return this.Subject;
    }
}

internal static class SampleData
{
    private static readonly string[] Kinds =
        ["Key", "REG_SZ", "REG_DWORD", "REG_BINARY", "REG_EXPAND_SZ", "REG_MULTI_SZ"];

    /// <summary>The first rows carry deliberately long values so auto-fit has something visible to measure.</summary>
    internal static SampleEntry[] Entries(int count)
    {
        var start = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Local);
        return Enumerable.Range(0, count).Select(index => new SampleEntry
        {
            Name = index switch
            {
                0 => "An unusually long value name that needs a much wider column to read",
                1 => "(Default)",
                _ => $"Value {index:D5}"
            },
            Kind = Kinds[index % Kinds.Length],
            Data = index == 2
                ? @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass"
                : $"0x{index * 7919 % 65536:x8} ({index * 7919 % 65536})",
            LastWrite = start.AddMinutes(index * 37 % 100000),
            Size = index * 131 % 9000
        }).ToArray();
    }

    internal static SampleEvent[] Events(int count)
    {
        return Enumerable.Range(0, count).Select(index => new SampleEvent
        {
            Subject = index % 3 == 0 ? "Value changed" : index % 3 == 1 ? "Key created" : "Value deleted",
            Path = $@"HKEY_CURRENT_USER\Software\Contoso\Sample\Group{index % 17}\Item{index}",
            Summary = index % 3 == 0 ? $"{index} → {index + 1}" : "—"
        }).ToArray();
    }
}
