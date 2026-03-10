// Polyfill for Task.WaitAsync(TimeSpan) which is .NET 6+ only.
namespace System.Threading.Tasks;

internal static class TaskExtensions
{
    internal static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            throw new TimeoutException();
        return await task;
    }

    internal static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            throw new TimeoutException();
        await task;
    }
}
