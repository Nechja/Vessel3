namespace Vessel3.UI;

public sealed record ToastMessage(string Id, string Message, string Level, DateTimeOffset CreatedAt);

public sealed class UiNotifications
{
    private readonly List<ToastMessage> messages = [];

    public event Action? OnChange;

    public IReadOnlyList<ToastMessage> Messages => messages;

    public void Success(string message) => Add(message, "success");

    public void Error(string message) => Add(message, "error");

    public void Info(string message) => Add(message, "info");

    public void Dismiss(string id)
    {
        messages.RemoveAll(m => m.Id == id);
        OnChange?.Invoke();
    }

    private void Add(string message, string level)
    {
        var toast = new ToastMessage(Guid.NewGuid().ToString("N"), message, level, DateTimeOffset.UtcNow);
        messages.Add(toast);
        OnChange?.Invoke();
        _ = AutoDismiss(toast.Id);
    }

    private async Task AutoDismiss(string id)
    {
        await Task.Delay(4000);
        Dismiss(id);
    }
}
