// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;

namespace RelayBridge.Infrastructure.Storage;

public sealed class LocalQueuePreview
{
    private readonly RelayDatabase _database;

    public LocalQueuePreview(RelayDatabase database)
    {
        _database = database;
    }

    public IReadOnlyList<QueuedMessage> GetMessages(CancellationToken cancellationToken = default)
    {
        return _database.GetQueuedMessages(cancellationToken);
    }

    public string GetSpoolPath(QueuedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _database.GetPendingPath(message.SpoolFileName);
    }
}
