using System.IO.Pipes;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal sealed class MonitoringHistory
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private readonly Queue<MonitoringSample> samples = new();
    private readonly object gate = new();

    internal void Add(MonitoringSample sample)
    {
        lock (gate)
        {
            if (samples.Count > 0 && sample.TimestampUtc <= samples.Last().TimestampUtc) return;
            samples.Enqueue(sample);
            Trim(sample.TimestampUtc);
        }
    }

    internal MonitoringSample[] Read(DateTime now)
    {
        lock (gate) { Trim(now); return samples.ToArray(); }
    }

    private void Trim(DateTime now)
    {
        while (samples.Count > 0 && (samples.Count > 7200 || samples.Peek().TimestampUtc <= now - Retention)) samples.Dequeue();
    }
}

internal sealed class MonitoringClient : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;

    internal MonitoringClient(MonitoringHistory history)
    {
        polling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    var now = DateTime.UtcNow;
                    var sample = await ReadSampleAsync(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    // Use the local sampling clock to retain gaps and one point per
                    // second, even when the helper is disconnected or restarting.
                    sample.TimestampUtc = now;
                    history.Add(sample);
                }
                while (await timer.WaitForNextTickAsync(stop.Token));
            }
            catch (OperationCanceledException) { }
        });
    }

    internal static async Task<MonitoringSample> ReadSampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(700);
            using var pipe = new NamedPipeClientStream(".", MonitoringProtocol.PipeName, PipeDirection.In,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await pipe.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > 4096) throw new InvalidDataException("Monitoring snapshot is too large.");
                output.Write(buffer, 0, count);
            }
            output.Position = 0;
            var sample = MonitoringProtocol.Decode(output);
            var age = DateTime.UtcNow - sample.TimestampUtc;
            if (age < TimeSpan.FromSeconds(-1) || age > TimeSpan.FromSeconds(3)) return new MonitoringSample();
            return sample;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException or ArgumentException)
        { return new MonitoringSample(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new MonitoringSample(); }
    }

    public void Dispose()
    {
        stop.Cancel();
        _ = polling.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
