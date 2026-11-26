namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>
/// 由两条单向流拼成的内存双工流，配合 WebSocket.CreateFromStream 在无网络环境下
/// 端到端驱动真实 WebSocket 收发（含分片与 4MB 上限）。
/// </summary>
public sealed class DuplexStream : Stream
{
    private readonly Stream _readSide;
    private readonly Stream _writeSide;

    public DuplexStream(Stream readSide, Stream writeSide)
    {
        _readSide = readSide;
        _writeSide = writeSide;
    }

    /// <summary>创建一对互通的双工流（两端各执其一）。读写各接一条 Pipe，无数据时读侧挂起等待。</summary>
    public static (DuplexStream Left, DuplexStream Right) CreatePair()
    {
        var leftToRight = new System.IO.Pipelines.Pipe();
        var rightToLeft = new System.IO.Pipelines.Pipe();
        return (
            new DuplexStream(rightToLeft.Reader.AsStream(), leftToRight.Writer.AsStream()),
            new DuplexStream(leftToRight.Reader.AsStream(), rightToLeft.Writer.AsStream()));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _writeSide.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        _readSide.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _readSide.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        _writeSide.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _writeSide.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
