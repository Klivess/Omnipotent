using System.Net.Http;
using System.Security.Cryptography;
using Omnipotent.Profiles;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.KliveAPI;

public sealed class KliveAPIStreamingTests
{
    [Fact]
    public async Task AuditedStream_ExactLimit_ReportsLengthAndSha256()
    {
        byte[] body = "brand-kit"u8.ToArray();
        var audit = new ApiService.RequestBodyAuditState(body.Length);
        using var source = new MemoryStream(body);
        using var stream = new ApiService.AuditedRequestBodyStream(source, body.Length, body.Length, audit);
        using var destination = new MemoryStream();

        await stream.CopyToAsync(destination);
        stream.Finish();

        Assert.Equal(body, destination.ToArray());
        Assert.Equal(body.Length, audit.BodyLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), audit.BodySha256);
    }

    [Fact]
    public async Task AuditedStream_UnknownLengthOverflow_ThrowsBeforeReturningOverflowByte()
    {
        byte[] body = [1, 2, 3, 4];
        var audit = new ApiService.RequestBodyAuditState(0);
        using var source = new MemoryStream(body);
        using var stream = new ApiService.AuditedRequestBodyStream(source, 3, -1, audit);
        byte[] destination = new byte[8];

        Assert.Equal(3, await stream.ReadAsync(destination));
        await Assert.ThrowsAsync<ApiService.RequestBodyTooLargeException>(async () =>
            await stream.ReadAsync(destination.AsMemory(3)));

        Assert.Equal(4, audit.BodyLength);
        Assert.Null(audit.BodySha256);
        Assert.Equal(0, destination[3]);
    }

    [Fact]
    public async Task AuditedStream_ZeroByteLimit_AcceptsEmptyBodyAndRejectsData()
    {
        var emptyAudit = new ApiService.RequestBodyAuditState(0);
        using (var emptySource = new MemoryStream())
        using (var emptyStream = new ApiService.AuditedRequestBodyStream(emptySource, 0, 0, emptyAudit))
        {
            Assert.Equal(0, await emptyStream.ReadAsync(new byte[1]));
            Assert.Equal(0, emptyAudit.BodyLength);
        }

        var dataAudit = new ApiService.RequestBodyAuditState(0);
        using var dataSource = new MemoryStream([42]);
        using var dataStream = new ApiService.AuditedRequestBodyStream(dataSource, 0, -1, dataAudit);
        await Assert.ThrowsAsync<ApiService.RequestBodyTooLargeException>(async () =>
            await dataStream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task RequestBodyAudit_ExplicitReportOverridesAutomaticObservation()
    {
        byte[] body = [1, 2, 3];
        var audit = new ApiService.RequestBodyAuditState(body.Length);
        using var source = new MemoryStream(body);
        using var stream = new ApiService.AuditedRequestBodyStream(source, 10, body.Length, audit);

        await stream.CopyToAsync(Stream.Null);
        audit.Report(99, "abcdef");
        stream.Finish();

        Assert.Equal(99, audit.BodyLength);
        Assert.Equal("ABCDEF", audit.BodySha256);
    }

    [Fact]
    public async Task AuditedStream_PartialRead_ReportsOnlyObservedBytesWithoutHash()
    {
        var audit = new ApiService.RequestBodyAuditState(0);
        using var source = new MemoryStream([1, 2, 3, 4, 5]);
        using var stream = new ApiService.AuditedRequestBodyStream(source, 10, -1, audit);
        byte[] prefix = new byte[2];

        Assert.Equal(2, await stream.ReadAsync(prefix));
        stream.Finish();

        Assert.Equal(2, audit.BodyLength);
        Assert.Null(audit.BodySha256);
    }

    [Fact]
    public void AuditedStream_ReadAfterFinish_ThrowsBeforeConsumingSource()
    {
        var audit = new ApiService.RequestBodyAuditState(0);
        using var source = new MemoryStream([1, 2, 3]);
        using var stream = new ApiService.AuditedRequestBodyStream(source, 10, -1, audit);
        stream.Finish();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task AuditedStream_OverflowOverridesEarlierExplicitAudit()
    {
        var audit = new ApiService.RequestBodyAuditState(0);
        using var source = new MemoryStream([1, 2, 3]);
        using var stream = new ApiService.AuditedRequestBodyStream(source, 2, -1, audit);
        byte[] destination = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(destination));
        audit.Report(1, "abc");

        await Assert.ThrowsAsync<ApiService.RequestBodyTooLargeException>(async () =>
            await stream.ReadAsync(new byte[1]));
        stream.Finish();

        Assert.True(audit.BodyLength >= 3);
        Assert.Null(audit.BodySha256);
    }

    [Fact]
    public async Task RouteRegistration_PreservesLegacyBufferedMode_AndAddsStreamingMode()
    {
        var api = new ApiService();

        await api.CreateRoute("/legacy", _ => Task.CompletedTask, HttpMethod.Post, KMProfileManager.KMPermissions.Guest);
        await api.CreateStreamingRoute("/stream", _ => Task.CompletedTask, HttpMethod.Put, KMProfileManager.KMPermissions.Klives, 1234);

        Assert.Equal(ApiService.RequestBodyMode.Buffered, api.ControllerLookup["/legacy"].requestBodyMode);
        Assert.Null(api.ControllerLookup["/legacy"].maxBodyBytes);
        Assert.Equal(ApiService.RequestBodyMode.Streaming, api.ControllerLookup["/stream"].requestBodyMode);
        Assert.Equal(1234, api.ControllerLookup["/stream"].maxBodyBytes);
    }
}
