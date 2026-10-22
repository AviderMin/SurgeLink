using SurgeLink.App.Services;

namespace SurgeLink.Core.Tests;

public sealed class ModalQueueTests
{
    [Fact]
    public void PromptsWaitForTheCurrentDialogAndKeepTheirOrder()
    {
        var queue = new ModalQueue();
        var settings = Guid.NewGuid();
        var authentication = Guid.NewGuid();
        var files = Guid.NewGuid();
        queue.Enqueue(settings);
        queue.Enqueue(authentication);
        queue.Enqueue(files);
        Assert.Equal(settings, queue.Current);
        queue.Remove(settings);
        Assert.Equal(authentication, queue.Current);
        queue.Remove(authentication);
        Assert.Equal(files, queue.Current);
        queue.Remove(files);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void UnmountingAWaitingPromptDoesNotDisplaceTheOpenDialog()
    {
        var queue = new ModalQueue();
        var active = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var notifications = 0;
        var unsubscribe = queue.Subscribe(() => notifications++);
        queue.Enqueue(active);
        queue.Enqueue(waiting);
        queue.Enqueue(active);
        queue.Remove(waiting);
        Assert.Equal(active, queue.Current);
        Assert.Equal(1, notifications);
        queue.Remove(active);
        queue.Remove(active);
        Assert.Null(queue.Current);
        Assert.Equal(2, notifications);
        unsubscribe();
        queue.Enqueue(Guid.NewGuid());
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void SeparateWindowsCanDisplayTheirOwnDialogs()
    {
        var main = new ModalQueue();
        var editor = new ModalQueue();
        var settings = Guid.NewGuid();
        var discard = Guid.NewGuid();
        main.Enqueue(settings);
        editor.Enqueue(discard);
        Assert.Equal(settings, main.Current);
        Assert.Equal(discard, editor.Current);
        editor.Remove(discard);
        Assert.Equal(settings, main.Current);
    }
}
