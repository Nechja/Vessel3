using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class BucketIndexTxScope : IDisposable
{
    private readonly BucketIndex owner;
    private readonly SqliteTransaction tx;
    private readonly List<Action> postCommitActions = [];
    private bool committed;

    internal BucketIndexTxScope(BucketIndex owner, SqliteTransaction tx)
    {
        this.owner = owner;
        this.tx = tx;
    }

    public void AddPostCommitAction(Action action) => postCommitActions.Add(action);

    public void Commit()
    {
        tx.Commit();
        committed = true;
        foreach (var action in postCommitActions)
        {
            action();
        }
    }

    public void Dispose()
    {
        if (!committed) tx.Rollback();
        tx.Dispose();
        owner.ClearTransaction(this);
    }
}
