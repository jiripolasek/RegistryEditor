// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;

namespace JPSoftworks.Regedit.Core.Search;

public sealed record KeyModifiedFilter(TimeSpan? Within = null, DateOnly? From = null, DateOnly? Through = null)
{
    public static ReadOnlyCollection<TimeSpan> Presets { get; } =
        Array.AsReadOnly(new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30) });

    internal void Validate()
    {
        if (this.Within is { } within)
        {
            if (this.From is not null || this.Through is not null || !Presets.Contains(within))
            {
                throw new ArgumentException("Choose a Key last modified preset of 1h, 24h, 7d, or 30d.");
            }

            return;
        }

        if (this.From is null && this.Through is null)
        {
            throw new ArgumentException("Enter a start or end date for Key last modified.");
        }

        if (this.From > this.Through)
        {
            throw new ArgumentException("The start date must be on or before the end date.");
        }

        if (this.From is { Year: < 1601 } || this.Through is { Year: < 1601 })
        {
            throw new ArgumentException("Use dates in 1601 or later for Key last modified.");
        }
    }

    internal bool Matches(DateTimeOffset timestamp, DateTimeOffset now, TimeZoneInfo localZone)
    {
        if (this.Within is { } window)
        {
            return timestamp <= now && now - timestamp <= window;
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, localZone).DateTime);
        return (this.From is null || date >= this.From) && (this.Through is null || date <= this.Through);
    }
}
