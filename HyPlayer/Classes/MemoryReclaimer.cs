using System;
using System.Threading;
using System.Threading.Tasks;

namespace HyPlayer.Classes;

public static class MemoryReclaimer
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private static int _version;

    public static void Schedule()
    {
        var version = Interlocked.Increment(ref _version);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay);
            if (version != Volatile.Read(ref _version)) return;
            Collect();
        });
    }

    public static void CollectNow()
    {
        Interlocked.Increment(ref _version);
        _ = Task.Run(Collect);
    }

    private static void Collect() => GC.Collect(2, GCCollectionMode.Forced, blocking: false);
}
