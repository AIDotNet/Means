namespace Means.Core;

/// <summary>
/// Object metadata plus an open readable content stream.
/// The caller owns disposal so endpoint code can stream without buffering whole objects in memory.
/// </summary>
public sealed record ObjectData(ObjectInfo Info, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Content.Dispose();
        return ValueTask.CompletedTask;
    }
}
