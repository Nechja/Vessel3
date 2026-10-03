using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class BucketIndexTxScope : IDisposable
{
    private readonly BucketIndex owner;
    private readonly SqliteTransaction tx;
    private bool committed;

    internal BucketIndexTxScope(BucketIndex owner, SqliteTransaction tx)
    {
        this.owner = owner;
        this.tx = tx;
    }

    public void Commit()
    {
        tx.Commit();
        committed = true;
    }

    public void Dispose()
    {
        if (!committed) tx.Rollback();
        tx.Dispose();
        owner.ClearTransaction(tx);
    }
}
