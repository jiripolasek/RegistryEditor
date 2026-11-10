// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections;

namespace JPSoftworks.Regedit.Infrastructure.Collections;

/// <summary>
///     A non-generic list that can replace its contents with a single change notification. Controls that write into a
///     host-owned list (for example a selection) use it to avoid one notification per item.
/// </summary>
public interface IRangeList : IList
{
    /// <summary>Replaces all items; raises nothing when the contents are already equal, otherwise one Reset.</summary>
    void ReplaceAll(IEnumerable items);
}
